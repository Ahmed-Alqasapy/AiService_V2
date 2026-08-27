namespace AiService.Models
{

    public class SearchResult
    {
        public string EntityType { get; set; } = "";
        public decimal EntityId { get; set; }
        public Dictionary<string, object> Payload { get; set; } = new();
        public float Score { get; set; }
    }

  
        #region Unified_Search_Result
        public class UnifiedSearchResult
        {
            public string SourceType { get; set; } = ""; // "file" or "db"

            // Common
            public string Text { get; set; } = "";
            public float Score { get; set; } = 0f;
            public float FinalScore { get; set; } = 0f; // after boosting
            public Dictionary<string, object> RawPayload { get; set; } = new();

            // File-specific
            public string? FileName { get; set; }
            public int? Page { get; set; }
            public string? Section { get; set; }
            public string? UploadDate { get; set; }

            // DB-specific
            public string? EntityType { get; set; }
            public decimal? EntityId { get; set; }

        }
        #endregion


    

}
