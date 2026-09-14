using System.Net.Http.Json;

namespace AiService.Services
{
    /// <summary>
    /// Calls tts_server.py (Nabra-7M-Distill). Returns 24 kHz WAV bytes.
    /// If the server isn't reachable, asks TtsServerLauncher to start it and retries once.
    /// </summary>
    public class TtsService
    {
        private readonly HttpClient _http;
        private readonly TtsServerLauncher _launcher;
        private readonly ILogger<TtsService> _logger;

        public TtsService(HttpClient http, TtsServerLauncher launcher, ILogger<TtsService> logger)
        {
            _http = http;
            _launcher = launcher;
            _logger = logger;
        }

        public async Task<byte[]?> SynthesizeAsync(string text, double speed = 1.0, CancellationToken ct = default)
        {
            try
            {
                return await PostAsync(text, speed, ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("TTS server unreachable ({Message}); starting it", ex.Message);
            }

            if (!await _launcher.EnsureRunningAsync(ct)) return null;

            try
            {
                return await PostAsync(text, speed, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "TTS request failed after starting the server");
                return null;
            }
        }

        private async Task<byte[]?> PostAsync(string text, double speed, CancellationToken ct)
        {
            using var response = await _http.PostAsJsonAsync("tts", new { text, speed }, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TTS server returned {Status}: {Body}",
                    (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
    }
}
