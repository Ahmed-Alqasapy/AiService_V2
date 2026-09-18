using Microsoft.EntityFrameworkCore;

namespace AiService.Models
{
    // Kept out of the scaffolded MyDbContext.cs so re-scaffolding from the Oracle DB
    // never erases the admin-dashboard tables. See AI_Admin_Dashboard_Schema.sql for the
    // DDL (sequences + tables) these mappings assume already exist.
    public partial class MyDbContext
    {
        public virtual DbSet<AiChatLog> AiChatLogs { get; set; } = null!;
        public virtual DbSet<AiIndexedFile> AiIndexedFiles { get; set; } = null!;

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AiChatLog>(e =>
            {
                e.ToTable("AI_CHAT_LOG", "APP_AML");
                e.HasKey(x => x.LogId);
                e.Property(x => x.LogId).HasColumnName("LOG_ID").HasDefaultValueSql("AI_CHAT_LOG_SEQ.NEXTVAL");
                e.Property(x => x.CreatedOn).HasColumnName("CREATED_ON").HasDefaultValueSql("SYSTIMESTAMP");
                e.Property(x => x.UserId).HasColumnName("USER_ID");
                e.Property(x => x.Channel).HasColumnName("CHANNEL");
                e.Property(x => x.Question).HasColumnName("QUESTION");
                e.Property(x => x.QuestionNorm).HasColumnName("QUESTION_NORM");
                e.Property(x => x.QuestionHash).HasColumnName("QUESTION_HASH");
                e.Property(x => x.Intent).HasColumnName("INTENT");
                e.Property(x => x.AnswerStatus).HasColumnName("ANSWER_STATUS");
                e.Property(x => x.Answer).HasColumnName("ANSWER");
                e.Property(x => x.TopScore).HasColumnName("TOP_SCORE");
                e.Property(x => x.FileHits).HasColumnName("FILE_HITS");
                e.Property(x => x.DbHits).HasColumnName("DB_HITS");
                e.Property(x => x.SourcesJson).HasColumnName("SOURCES_JSON");
                e.Property(x => x.Model).HasColumnName("MODEL");
                e.Property(x => x.InputTokens).HasColumnName("INPUT_TOKENS");
                e.Property(x => x.OutputTokens).HasColumnName("OUTPUT_TOKENS");
                e.Property(x => x.CostUsd).HasColumnName("COST_USD");
                e.Property(x => x.SearchMs).HasColumnName("SEARCH_MS");
                e.Property(x => x.LlmMs).HasColumnName("LLM_MS");
                e.Property(x => x.TotalMs).HasColumnName("TOTAL_MS");
                e.Property(x => x.ErrorMsg).HasColumnName("ERROR_MSG");
            });

            modelBuilder.Entity<AiIndexedFile>(e =>
            {
                e.ToTable("AI_INDEXED_FILE", "APP_AML");
                e.HasKey(x => x.FileId);
                e.Property(x => x.FileId).HasColumnName("FILE_ID").HasDefaultValueSql("AI_INDEXED_FILE_SEQ.NEXTVAL");
                e.Property(x => x.FileName).HasColumnName("FILE_NAME");
                e.Property(x => x.FileSize).HasColumnName("FILE_SIZE");
                e.Property(x => x.UploadedBy).HasColumnName("UPLOADED_BY");
                e.Property(x => x.UploadedOn).HasColumnName("UPLOADED_ON").HasDefaultValueSql("SYSTIMESTAMP");
                e.Property(x => x.Status).HasColumnName("STATUS");
                e.Property(x => x.Collection).HasColumnName("COLLECTION");
                e.Property(x => x.ChunkCount).HasColumnName("CHUNK_COUNT");
                e.Property(x => x.PageCount).HasColumnName("PAGE_COUNT");
                e.Property(x => x.DurationMs).HasColumnName("DURATION_MS");
                e.Property(x => x.ErrorMsg).HasColumnName("ERROR_MSG");
                e.Property(x => x.IndexedOn).HasColumnName("INDEXED_ON");
            });
        }
    }
}
