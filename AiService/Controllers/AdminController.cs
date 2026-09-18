using AiService.Filters;
using AiService.Models;
using AiService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiService.Controllers
{
    [Route("api/admin")]
    [ApiController]
    [ServiceFilter(typeof(AdminApiKeyFilter))]
    public class AdminController : ControllerBase
    {
        private static readonly string[] UnansweredStatuses =
            { "NoContext", "LowConfidence", "ModelNoInfo", "Error", "RateLimited" };

        private readonly MyDbContext _db;
        private readonly EmbeddingService _embeddingService;
        private readonly IConfiguration _cfg;
        private readonly ILogger<AdminController> _logger;

        public AdminController(MyDbContext db, EmbeddingService embeddingService,
            IConfiguration cfg, ILogger<AdminController> logger)
        {
            _db = db;
            _embeddingService = embeddingService;
            _cfg = cfg;
            _logger = logger;
        }

        private static (DateTime from, DateTime to) Range(DateTime? from, DateTime? to)
        {
            var t = (to ?? DateTime.Today).Date.AddDays(1); // exclusive upper bound
            var f = (from ?? DateTime.Today.AddDays(-29)).Date;
            return (f, t);
        }

        [HttpGet("summary")]
        public async Task<IActionResult> Summary(DateTime? from, DateTime? to)
        {
            try
            {
                var (f, t) = Range(from, to);
                var rate = _cfg.GetValue<decimal>("Admin:UsdToKwd");

                var logs = _db.AiChatLogs.AsNoTracking().Where(x => x.CreatedOn >= f && x.CreatedOn < t);

                var totalQuestions = await logs.CountAsync(x => x.AnswerStatus != "Greeting");
                var unansweredCount = await logs.CountAsync(x => UnansweredStatuses.Contains(x.AnswerStatus));
                var totalCostUsd = await logs.SumAsync(x => (decimal?)x.CostUsd) ?? 0m;
                var avgTotalMs = await logs.Where(x => x.TotalMs != null).AverageAsync(x => (double?)x.TotalMs) ?? 0;

                var files = await _db.AiIndexedFiles.AsNoTracking().ToListAsync();
                var filesIndexed = files.Count(x => x.Status == "Indexed");
                var filesFailed = files.Count(x => x.Status == "Failed");

                var fileChunks = await _embeddingService.CountBySourceTypeAsync("file");
                var dbChunks = await _embeddingService.CountBySourceTypeAsync("db");

                return Ok(new
                {
                    totalQuestions,
                    unansweredCount,
                    unansweredPct = totalQuestions == 0 ? 0 : Math.Round(100.0 * unansweredCount / totalQuestions, 1),
                    totalCostUsd,
                    totalCostKwd = Math.Round(totalCostUsd * rate, 4),
                    avgTotalMs,
                    filesIndexed,
                    filesFailed,
                    totalChunks = fileChunks + dbChunks,
                    fileChunks,
                    dbChunks
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AdminController.Summary failed");
                return StatusCode(500, new { error = "Failed to compute summary." });
            }
        }

        [HttpGet("files")]
        public async Task<IActionResult> Files()
        {
            try
            {
                var rows = await _db.AiIndexedFiles.AsNoTracking()
                    .OrderByDescending(x => x.UploadedOn)
                    .ToListAsync();
                var qdrantCounts = await _embeddingService.GetFileChunkCountsAsync();

                var result = rows.Select(r => new
                {
                    fileName = r.FileName,
                    status = r.Status,
                    qdrantChunks = qdrantCounts.GetValueOrDefault(r.FileName, 0),
                    oracleChunks = r.ChunkCount,
                    pageCount = r.PageCount,
                    uploadedBy = r.UploadedBy,
                    uploadedOn = r.UploadedOn,
                    durationMs = r.DurationMs,
                    errorMsg = r.ErrorMsg
                });
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AdminController.Files failed");
                return StatusCode(500, new { error = "Failed to load indexed files." });
            }
        }

        /// <summary>
        /// One-time catch-up for files that were indexed into Qdrant before AI_INDEXED_FILE
        /// existed (or by any path other than FileUploadController.Upload). Inserts an
        /// "Indexed" row per file found in Qdrant that isn't already tracked; never touches
        /// or overwrites a file that already has a row (real upload history wins).
        /// </summary>
        [HttpPost("files/backfill")]
        public async Task<IActionResult> BackfillFiles()
        {
            try
            {
                var qdrantCounts = await _embeddingService.GetFileChunkCountsAsync();
                var existing = new HashSet<string>(await _db.AiIndexedFiles.Select(f => f.FileName).ToListAsync());

                int inserted = 0;
                foreach (var kv in qdrantCounts)
                {
                    if (existing.Contains(kv.Key)) continue;
                    _db.AiIndexedFiles.Add(new AiIndexedFile
                    {
                        FileName = kv.Key,
                        Status = "Indexed",
                        ChunkCount = (int)kv.Value,
                        IndexedOn = DateTime.Now
                    });
                    inserted++;
                }
                await _db.SaveChangesAsync();

                return Ok(new { filesFoundInQdrant = qdrantCounts.Count, inserted, alreadyTracked = qdrantCounts.Count - inserted });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AdminController.BackfillFiles failed");
                return StatusCode(500, new { error = "Backfill failed." });
            }
        }

        [HttpGet("unanswered")]
        public async Task<IActionResult> Unanswered(DateTime? from, DateTime? to, int top = 50, string? status = null)
        {
            try
            {
                var (f, t) = Range(from, to);
                var q = _db.AiChatLogs.AsNoTracking()
                    .Where(x => x.CreatedOn >= f && x.CreatedOn < t && UnansweredStatuses.Contains(x.AnswerStatus));
                if (!string.IsNullOrEmpty(status))
                    q = q.Where(x => x.AnswerStatus == status);

                // Group in memory: LISTAGG/DISTINCT-in-GroupBy over the Oracle EF provider
                // is fragile, and this table is small enough (a few thousand rows/month)
                // that pulling the window and grouping in C# is simpler and safe.
                var rows = await q.Select(x => new
                {
                    x.QuestionHash,
                    x.QuestionNorm,
                    x.UserId,
                    x.CreatedOn,
                    x.TopScore,
                    x.AnswerStatus
                }).ToListAsync();

                var groups = rows.GroupBy(x => x.QuestionHash)
                    .Select(g => new
                    {
                        hash = g.Key,
                        sample = g.OrderByDescending(x => x.CreatedOn).First().QuestionNorm,
                        count = g.Count(),
                        users = g.Select(x => x.UserId).Distinct().Count(),
                        lastAsked = g.Max(x => x.CreatedOn),
                        avgScore = g.Where(x => x.TopScore.HasValue).Select(x => (double)x.TopScore!.Value).DefaultIfEmpty().Average(),
                        statuses = string.Join(",", g.Select(x => x.AnswerStatus).Distinct())
                    })
                    .OrderByDescending(x => x.count).ThenByDescending(x => x.lastAsked)
                    .Take(Math.Clamp(top, 1, 200))
                    .ToList();

                return Ok(groups);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AdminController.Unanswered failed");
                return StatusCode(500, new { error = "Failed to load unanswered questions." });
            }
        }

        [HttpGet("unanswered/{hash}/samples")]
        public async Task<IActionResult> UnansweredSamples(string hash)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(hash ?? "", "^[A-Fa-f0-9]{64}$"))
                return BadRequest(new { error = "Invalid hash." });

            try
            {
                var rows = await _db.AiChatLogs.AsNoTracking()
                    .Where(x => x.QuestionHash == hash)
                    .OrderByDescending(x => x.CreatedOn)
                    .Take(50)
                    .Select(x => new
                    {
                        x.CreatedOn,
                        x.Channel,
                        x.AnswerStatus,
                        x.TopScore,
                        x.FileHits,
                        x.DbHits,
                        x.Question,
                        x.Answer,
                        x.SourcesJson
                    })
                    .ToListAsync();
                return Ok(rows);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AdminController.UnansweredSamples failed");
                return StatusCode(500, new { error = "Failed to load samples." });
            }
        }

        [HttpGet("cost/daily")]
        public async Task<IActionResult> CostDaily(DateTime? from, DateTime? to)
        {
            try
            {
                var (f, t) = Range(from, to);
                var rate = _cfg.GetValue<decimal>("Admin:UsdToKwd");

                var rows = await _db.AiChatLogs.AsNoTracking()
                    .Where(x => x.CreatedOn >= f && x.CreatedOn < t)
                    .Select(x => new { x.CreatedOn, x.InputTokens, x.OutputTokens, x.CostUsd })
                    .ToListAsync();

                var byDay = rows.GroupBy(x => x.CreatedOn.Date)
                    .ToDictionary(g => g.Key, g => new
                    {
                        questions = g.Count(),
                        inputTokens = g.Sum(x => x.InputTokens),
                        outputTokens = g.Sum(x => x.OutputTokens),
                        costUsd = g.Sum(x => x.CostUsd)
                    });

                var days = (t - f).Days;
                var result = Enumerable.Range(0, days).Select(i => f.AddDays(i)).Select(d =>
                    byDay.TryGetValue(d, out var r)
                        ? new { day = d.ToString("yyyy-MM-dd"), r.questions, r.inputTokens, r.outputTokens, r.costUsd, costKwd = Math.Round(r.costUsd * rate, 4) }
                        : new { day = d.ToString("yyyy-MM-dd"), questions = 0, inputTokens = 0, outputTokens = 0, costUsd = 0m, costKwd = 0m });

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AdminController.CostDaily failed");
                return StatusCode(500, new { error = "Failed to load cost data." });
            }
        }
    }
}
