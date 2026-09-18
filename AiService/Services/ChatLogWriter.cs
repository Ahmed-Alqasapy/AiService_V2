using AiService.Models;

namespace AiService.Services
{
    // Drains ChatLogQueue and writes one AI_CHAT_LOG row per chat turn. Runs in its own
    // DI scope per row (MyDbContext isn't thread-safe / long-lived) and never lets a
    // logging failure take down the host.
    public class ChatLogWriter : BackgroundService
    {
        private readonly ChatLogQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ChatLogWriter> _logger;

        public ChatLogWriter(ChatLogQueue queue, IServiceScopeFactory scopeFactory, ILogger<ChatLogWriter> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var trace in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<MyDbContext>();
                    var cost = scope.ServiceProvider.GetRequiredService<CostCalculator>();

                    var norm = QuestionNormalizer.Normalize(trace.Question);
                    db.AiChatLogs.Add(new AiChatLog
                    {
                        CreatedOn = DateTime.Now,
                        UserId = trace.UserId,
                        Channel = trace.Channel,
                        Question = Trunc(trace.Question, 2000) ?? "",
                        QuestionNorm = Trunc(norm, 2000),
                        QuestionHash = QuestionNormalizer.Hash(norm),
                        Intent = trace.Intent,
                        AnswerStatus = trace.Status,
                        Answer = trace.Answer,
                        TopScore = trace.TopScore,
                        FileHits = trace.FileHits,
                        DbHits = trace.DbHits,
                        SourcesJson = System.Text.Json.JsonSerializer.Serialize(trace.Sources),
                        Model = trace.Model,
                        InputTokens = trace.InputTokens,
                        OutputTokens = trace.OutputTokens,
                        CostUsd = cost.Calculate(trace.Model, trace.InputTokens, trace.OutputTokens),
                        SearchMs = trace.SearchMs,
                        LlmMs = trace.LlmMs,
                        TotalMs = trace.TotalMs,
                        ErrorMsg = Trunc(trace.Error, 1000)
                    });
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write chat log for question: {Question}", trace.Question);
                }
            }
        }

        private static string? Trunc(string? s, int n) => s == null ? null : (s.Length <= n ? s : s[..n]);
    }
}
