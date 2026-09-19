using System.Diagnostics;

namespace AiService.Services
{
    /// <summary>
    /// Owns a long-running Python model server (tts_server.py, stt_server.py). Started with the
    /// API (IHostedService) and again on demand by the calling service if the server is not
    /// answering, e.g. under IIS Express, where the app can start before or without the startup
    /// launch succeeding, or after the process died. Python output goes to &lt;script&gt;.log next
    /// to the script. Settings come from one config section: BaseUrl, AutoStart, PythonPath,
    /// StartupTimeoutSeconds, ModelDir.
    /// </summary>
    public abstract class PythonServerLauncher : IHostedService
    {
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private Process? _process;
        private StreamWriter? _log;

        protected PythonServerLauncher(IConfiguration config, IWebHostEnvironment env, ILogger logger)
        {
            _config = config;
            _env = env;
            _logger = logger;
        }

        /// <summary>Config section, e.g. "Tts".</summary>
        protected abstract string Section { get; }
        protected abstract string ScriptName { get; }
        protected abstract string DefaultBaseUrl { get; }
        /// <summary>Environment variables that pass the port and model folder to the script.</summary>
        protected abstract string PortEnvVar { get; }
        protected abstract string ModelDirEnvVar { get; }

        /// <summary>
        /// Reads {section}:Models:{Active}:{name} when the section has an Active model, otherwise
        /// (or when that model doesn't set it) {section}:{name}. Stt uses this to switch between
        /// Whisper and Audar with one word; Tts has no Active and reads its flat keys as before.
        /// </summary>
        public static string? Setting(IConfiguration config, string section, string name)
        {
            var active = config[$"{section}:Active"];
            var value = string.IsNullOrWhiteSpace(active) ? null : config[$"{section}:Models:{active}:{name}"];
            return string.IsNullOrWhiteSpace(value) ? config[$"{section}:{name}"] : value;
        }

        private string? Setting(string name) => Setting(_config, Section, name);

        private string BaseUrl => Setting("BaseUrl") ?? DefaultBaseUrl;
        private string Name => Section.ToUpperInvariant();

        public Task StartAsync(CancellationToken ct)
        {
            if (_config.GetValue($"{Section}:AutoStart", true))
            {
                // Don't block API startup on model load.
                _ = Task.Run(() => EnsureRunningAsync(CancellationToken.None));
            }
            return Task.CompletedTask;
        }

        /// <summary>Starts the server if it isn't answering and waits until it is. Returns true when healthy.</summary>
        public async Task<bool> EnsureRunningAsync(CancellationToken ct)
        {
            if (await IsHealthyAsync()) return true;

            await _startLock.WaitAsync(ct);
            try
            {
                if (await IsHealthyAsync()) return true;

                if (_process is null || _process.HasExited)
                {
                    if (!TryStartProcess()) return false;
                }

                var timeout = TimeSpan.FromSeconds(_config.GetValue($"{Section}:StartupTimeoutSeconds", 120));
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < timeout)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await IsHealthyAsync())
                    {
                        _logger.LogInformation("{Name} server ready at {Url} after {Seconds:F0}s", Name, BaseUrl, sw.Elapsed.TotalSeconds);
                        return true;
                    }
                    if (_process!.HasExited)
                    {
                        _logger.LogError("{Name} server exited with code {Code}. See {Log}", Name, _process.ExitCode, LogPath);
                        return false;
                    }
                    await Task.Delay(1000, ct);
                }
                _logger.LogError("{Name} server not ready after {Timeout}. See {Log}", Name, timeout, LogPath);
                return false;
            }
            finally
            {
                _startLock.Release();
            }
        }

        private string ScriptPath => Path.Combine(_env.ContentRootPath, Setting("Script") is { Length: > 0 } s ? s : ScriptName);
        private string LogPath => Path.ChangeExtension(ScriptPath, ".log");

        private bool TryStartProcess()
        {
            if (!File.Exists(ScriptPath))
            {
                _logger.LogError("{Name} not started: {Script} not found", Name, ScriptPath);
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = Setting("PythonPath") ?? @"C:\Python313\python.exe",
                Arguments = $"\"{ScriptPath}\"",
                WorkingDirectory = _env.ContentRootPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment[PortEnvVar] = new Uri(BaseUrl).Port.ToString();
            if (Setting("ModelDir") is { Length: > 0 } modelDir)
                psi.Environment[ModelDirEnvVar] = modelDir;

            try
            {
                // The previous run's writer still holds the file after that process died; without
                // closing it every restart failed with "file is being used by another process".
                _log?.Dispose();
                var log = _log = new StreamWriter(
                    new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                _process = Process.Start(psi)!;
                // A dying process can still flush output after its writer was disposed by a
                // restart; an exception here would be unhandled on the event thread.
                void Write(string? line)
                {
                    if (line == null) return;
                    lock (log) { try { log.WriteLine(line); } catch (ObjectDisposedException) { } }
                }
                _process.OutputDataReceived += (_, e) => Write(e.Data);
                _process.ErrorDataReceived += (_, e) => Write(e.Data);
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                _logger.LogInformation("Started {Name} server {Script} (pid {Pid}) at {Url}, log: {Log}",
                    Name, Path.GetFileName(ScriptPath), _process.Id, BaseUrl, LogPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start {Name} server ({Python} {Script})", Name, psi.FileName, ScriptPath);
                return false;
            }
        }

        public Task StopAsync(CancellationToken ct)
        {
            try
            {
                if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not stop {Name} server", Name);
            }
            return Task.CompletedTask;
        }

        private async Task<bool> IsHealthyAsync()
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(2) };
                return (await http.GetAsync("health")).IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>tts_server.py (Nabra-7M-Distill).</summary>
    public class TtsServerLauncher : PythonServerLauncher
    {
        public TtsServerLauncher(IConfiguration config, IWebHostEnvironment env, ILogger<TtsServerLauncher> logger)
            : base(config, env, logger) { }

        protected override string Section => "Tts";
        protected override string ScriptName => "tts_server.py";
        protected override string DefaultBaseUrl => "http://127.0.0.1:8010/";
        protected override string PortEnvVar => "TTS_PORT";
        protected override string ModelDirEnvVar => "NABRA_DIR";
    }

    /// <summary>stt_server.py (whisper-large-v3-turbo-arabic-dialectal), or stt_server_audar.py
    /// (Audar-ASR-V1-Flash via llama.cpp), picked by Stt:Active.</summary>
    public class SttServerLauncher : PythonServerLauncher
    {
        public SttServerLauncher(IConfiguration config, IWebHostEnvironment env, ILogger<SttServerLauncher> logger)
            : base(config, env, logger) { }

        protected override string Section => "Stt";
        protected override string ScriptName => "stt_server.py";
        protected override string DefaultBaseUrl => "http://127.0.0.1:8011/";
        protected override string PortEnvVar => "STT_PORT";
        protected override string ModelDirEnvVar => "STT_MODEL_DIR";
    }
}
