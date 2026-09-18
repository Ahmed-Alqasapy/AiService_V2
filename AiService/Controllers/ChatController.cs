using AiService.Interfaces;
using AiService.Models;
using AiService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using System.Diagnostics;

namespace AiService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ChatController : ControllerBase
    {
        private readonly OpenAIService _openAIService;
        private readonly ChatTrace _trace;
        private readonly ChatLogQueue _chatLogQueue;


        public readonly GPTService _gptService;
        public ChatController(OpenAIService openAIService
              , GPTService gPTService, ChatTrace trace, ChatLogQueue chatLogQueue)
        {
            _openAIService = openAIService;

            _gptService = gPTService;
            _trace = trace;
            _chatLogQueue = chatLogQueue;
        }

        [HttpPost("ChatWithRAG")]
        public async Task<IActionResult> Chat([FromBody] ChatMessage message)
        {



            if (string.IsNullOrWhiteSpace(message.Text))
            {
                return BadRequest(new { error = "Message cannot be empty." });
            }

            _trace.Question = message.Text;
            _trace.UserId = message.UserId;
            _trace.Channel = string.IsNullOrWhiteSpace(message.Channel) ? "text" : message.Channel;
            var sw = Stopwatch.StartNew();

            try
            {

                // Use the updated GenerateResponseAsync method from the service
                string response = await _gptService.ProcessMessage(message.Text);
                _trace.Answer = response;

                if (string.IsNullOrWhiteSpace(response))
                {

                    return BadRequest(new { error = "Failed to generate a response." });

                }

                return Ok(new { message = response });



            }
            catch (Exception ex)
            {
                _trace.Status = "Error";
                _trace.Error = ex.Message;
                return StatusCode(500, new { error = "An error occurred.", details = ex.Message });
            }
            finally
            {
                _trace.TotalMs = sw.ElapsedMilliseconds;
                _chatLogQueue.Enqueue(_trace);
            }

        }

        /// <summary>
        /// Speaks a chat reply. Call it after the text from ChatWithRAG is on screen,
        /// so the text is never held back waiting for audio. Returns audio/wav.
        /// </summary>
        [HttpPost("Speak")]
        [Produces("audio/wav", "application/json")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "audio/wav")]
        public async Task<IActionResult> Speak([FromBody] SpeakRequest request,
            [FromServices] TtsService tts, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return BadRequest(new { error = "Text cannot be empty." });
            }

            byte[]? wav = await tts.SynthesizeAsync(request.Text, request.Speed ?? 1.0, ct);
            if (wav == null)
            {
                return StatusCode(503, new { error = "Text-to-speech service is unavailable." });
            }

            return File(wav, "audio/wav", "speech.wav");
        }

        /// <summary>
        /// GET version for testing: open /api/Chat/Speak?text=مرحبا in a browser tab and it plays.
        /// </summary>
        [HttpGet("Speak")]
        [Produces("audio/wav", "application/json")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "audio/wav")]
        public async Task<IActionResult> SpeakGet([FromQuery] string text, [FromQuery] double? speed,
            [FromServices] TtsService tts, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return BadRequest(new { error = "Text cannot be empty." });
            }

            byte[]? wav = await tts.SynthesizeAsync(text, speed ?? 1.0, ct);
            if (wav == null)
            {
                return StatusCode(503, new { error = "Text-to-speech service is unavailable." });
            }

            // No file name, so the browser plays it inline instead of downloading.
            return File(wav, "audio/wav");
        }

        /// <summary>
        /// Browser-friendly two-step TTS. POST the text, get back an id and an audioUrl; then load
        /// audioUrl with GET (e.g. as an &lt;audio&gt; src). A web-protection proxy on this machine
        /// (Kaspersky) swallows audio returned from a browser POST and hands back an empty 204,
        /// while GET audio passes, and GET alone would cap reply length at the URL limit.
        /// </summary>
        [HttpPost("SpeakPrepare")]
        public async Task<IActionResult> SpeakPrepare([FromBody] SpeakRequest request,
            [FromServices] TtsService tts, [FromServices] IMemoryCache cache,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return BadRequest(new { error = "Text cannot be empty." });
            }

            byte[]? wav = await tts.SynthesizeAsync(request.Text, request.Speed ?? 1.0, ct);
            if (wav == null)
            {
                return StatusCode(503, new { error = "Text-to-speech service is unavailable." });
            }

            var id = Guid.NewGuid().ToString("N");
            cache.Set("tts:" + id, wav, TimeSpan.FromMinutes(10));
            return Ok(new { id, audioUrl = Url.Action(nameof(SpeakAudio), new { id }) });
        }

        /// <summary>Audio prepared by SpeakPrepare. Kept for 10 minutes.</summary>
        [HttpGet("SpeakAudio/{id}")]
        [Produces("audio/wav", "application/json")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "audio/wav")]
        public IActionResult SpeakAudio(string id, [FromServices] IMemoryCache cache)
        {
            return cache.TryGetValue("tts:" + id, out byte[]? wav) && wav != null
                ? File(wav, "audio/wav", enableRangeProcessing: true)
                : NotFound(new { error = "Audio not found or expired." });
        }

        /// <summary>
        /// Voice button: upload the recording (form field "file"; webm/ogg from MediaRecorder, or
        /// wav/mp3/m4a), get back the Arabic text. Put the text in the chat box, then send it to
        /// ChatWithRAG as usual. Max 120 s of audio.
        /// </summary>
        [HttpPost("Transcribe")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(15 * 1024 * 1024)]
        public async Task<IActionResult> Transcribe(IFormFile file,
            [FromServices] SttService stt, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { error = "Audio file is empty." });
            }

            byte[] audio;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms, ct);
                audio = ms.ToArray();
            }

            try
            {
                var result = await stt.TranscribeAsync(audio, file.FileName, file.ContentType, ct);
                if (result == null)
                {
                    return StatusCode(503, new { error = "Speech-to-text service is unavailable." });
                }
                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    return UnprocessableEntity(new { error = "No speech was recognized.", duration = result.Duration });
                }
                return Ok(new { text = result.Text, duration = result.Duration, seconds = result.Seconds });
            }
            catch (SttRejectedException ex)
            {
                return StatusCode(ex.StatusCode, new { error = ex.Message });
            }
        }

    }
        public class ChatMessage
        {
            public string Text { get; set; }
            public string? UserId { get; set; }
            public string? Channel { get; set; }    // "text" | "voice"
        }

        public class SpeakRequest
        {
            public string Text { get; set; }
            public double? Speed { get; set; }
        }


    }


