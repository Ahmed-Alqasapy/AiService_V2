namespace AiService.Models
{
    public class AiIndexedFile
    {
        public decimal FileId { get; set; }
        public string FileName { get; set; } = "";
        public long? FileSize { get; set; }
        public string? UploadedBy { get; set; }
        public DateTime? UploadedOn { get; set; }
        public string Status { get; set; } = "Queued"; // Queued | Indexing | Indexed | Failed | Skipped
        public string? Collection { get; set; }
        public int? ChunkCount { get; set; }
        public int? PageCount { get; set; }
        public long? DurationMs { get; set; }
        public string? ErrorMsg { get; set; }
        public DateTime? IndexedOn { get; set; }
    }
}
