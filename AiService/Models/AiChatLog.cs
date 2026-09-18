namespace AiService.Models
{
    public class AiChatLog
    {
        public decimal LogId { get; set; }
        public DateTime CreatedOn { get; set; }
        public string? UserId { get; set; }
        public string? Channel { get; set; }
        public string Question { get; set; } = "";
        public string? QuestionNorm { get; set; }
        public string? QuestionHash { get; set; }
        public string? Intent { get; set; }
        public string AnswerStatus { get; set; } = "";
        public string? Answer { get; set; }
        public float? TopScore { get; set; }
        public int? FileHits { get; set; }
        public int? DbHits { get; set; }
        public string? SourcesJson { get; set; }
        public string? Model { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public decimal CostUsd { get; set; }
        public long? SearchMs { get; set; }
        public long? LlmMs { get; set; }
        public long? TotalMs { get; set; }
        public string? ErrorMsg { get; set; }
    }
}
