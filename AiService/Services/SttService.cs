using System.Net.Http.Headers;
using System.Text.Json;

namespace AiService.Services
{
    public record SttResult(string Text, double Duration, double Seconds);

    /// <summary>
    /// Calls stt_server.py (arabic_speech_to_text). Sends the recorded audio as-is; the server
    /// decodes webm/ogg/wav/mp3 itself. If the server isn't reachable, asks SttServerLauncher to
    /// start it and retries once.
    /// </summary>
    public class SttService
    {
        private readonly HttpClient _http;
        private readonly SttServerLauncher _launcher;
        private readonly ILogger<SttService> _logger;

        public SttService(HttpClient http, SttServerLauncher launcher, ILogger<SttService> logger)
        {
            _http = http;
            _launcher = launcher;
            _logger = logger;
        }

        /// <summary>Returns null when the server is unavailable; throws SttRejectedException for bad audio.</summary>
        public async Task<SttResult?> TranscribeAsync(byte[] audio, string fileName, string? contentType, CancellationToken ct = default)
        {
            try
            {
                return await PostAsync(audio, fileName, contentType, ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("STT server unreachable ({Message}); starting it", ex.Message);
            }

            if (!await _launcher.EnsureRunningAsync(ct)) return null;

            try
            {
                return await PostAsync(audio, fileName, contentType, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "STT request failed after starting the server");
                return null;
            }
        }

        private async Task<SttResult?> PostAsync(byte[] audio, string fileName, string? contentType, CancellationToken ct)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(audio);
            // MediaRecorder sends "audio/webm;codecs=opus"; the MediaTypeHeaderValue constructor
            // rejects parameters, Parse accepts them. The server sniffs the format anyway.
            file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type)
                ? type
                : new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", fileName);

            using var response = await _http.PostAsync("transcribe", form, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode is 400 or 413)
            {
                throw new SttRejectedException((int)response.StatusCode, ReadDetail(body));
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("STT server returned {Status}: {Body}", (int)response.StatusCode, body);
                return null;
            }
            return JsonSerializer.Deserialize<SttResult>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        private static string ReadDetail(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("detail", out var d) ? d.ToString() : body;
            }
            catch (JsonException)
            {
                return body;
            }
        }
    }

    /// <summary>The server understood the request but refused the audio (undecodable, empty, too long).</summary>
    public class SttRejectedException : Exception
    {
        public int StatusCode { get; }
        public SttRejectedException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    }
}
