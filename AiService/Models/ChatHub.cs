using AiService.Services;
using Microsoft.AspNetCore.SignalR;

namespace AiService.Models
{
    public class ChatHub : Hub
    {
        //private readonly GPTService _gpt;

        //public ChatHub(GPTService gpt) => _gpt = gpt;

        //public async Task SendMessage(string message, string sessionId)
        //{
        //    // 1. Show "Thinking..."
        //    await Clients.Caller.SendAsync("ReceiveMessage", "Thinking...", sessionId);

        //    // 2. Stream tokens
        //    var full = "";
        //    await foreach (var token in _gpt.StreamResponseAsync(message))
        //    {
        //        full += token;
        //        await Clients.Caller.SendAsync("ReceiveToken", token, sessionId);
        //    }

        //    // 3. Final answer
        //    await Clients.Caller.SendAsync("ReceiveMessage", full, sessionId);
        //}
    }
}
