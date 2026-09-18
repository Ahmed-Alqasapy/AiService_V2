namespace AiService.Models
{
    // Scoped per-request. Filled in while ChatController/GPTService/OpenAIService handle
    // one ChatWithRAG call, then handed to ChatLogQueue in a `finally` so logging never
    // blocks or fails the chat response.
    public class ChatTrace
    {
        public string? UserId { get; set; }
        public string Channel { get; set; } = "text";
        public string Question { get; set; } = "";
        public string? Intent { get; set; }
        public string Status { get; set; } = "Answered";
        public string? Answer { get; set; }
        public float? TopScore { get; set; }
        public int FileHits { get; set; }
        public int DbHits { get; set; }
        public List<object> Sources { get; } = new();
        public string? Model { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public long SearchMs { get; set; }
        public long LlmMs { get; set; }
        public long TotalMs { get; set; }
        public string? Error { get; set; }
    }
}
