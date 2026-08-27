using AiService.Interfaces;
using AiService.Models;
using AiService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Diagnostics;

namespace AiService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ChatController : ControllerBase
    {
        private readonly OpenAIService _openAIService;


        public readonly GPTService _gptService;
        public ChatController(OpenAIService openAIService
              , GPTService gPTService)
        {
            _openAIService = openAIService;

            _gptService = gPTService;
        }

        [HttpPost("ChatWithRAG")]
        public async Task<IActionResult> Chat([FromBody] ChatMessage message)
        {



            if (string.IsNullOrWhiteSpace(message.Text))
            {
                return BadRequest(new { error = "Message cannot be empty." });
            }

            try
            {

                // Use the updated GenerateResponseAsync method from the service
                string response = await _gptService.ProcessMessage(message.Text);

                if (string.IsNullOrWhiteSpace(response))
                {

                    return BadRequest(new { error = "Failed to generate a response." });

                }

                return Ok(new { message = response });



            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "An error occurred.", details = ex.Message });
            }

        }

    }
        public class ChatMessage
        {
            public string Text { get; set; }
        }


    }


