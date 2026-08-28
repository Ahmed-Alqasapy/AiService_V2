using AiService.Models;
using Docnet.Core;
using Docnet.Core.Models;
using DocumentFormat.OpenXml.Office2019.Drawing.Model3D;
using DocumentFormat.OpenXml.Packaging;
using EvoPdf.PdfToText;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NPOI.HPSF;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using SemanticChunkerNET;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using JsonSerializer = System.Text.Json.JsonSerializer;



namespace AiService.Services
{
    public class EmbeddingService
    {
        private readonly HttpClient _ollamaClient;
        private readonly HttpClient _ollama;

        private readonly HttpClient _ollamaLocal;

        private readonly HttpClient _qdrantClient;
        private readonly MyDbContext _db;
        private readonly ILogger<EmbeddingService> _logger;

        //private readonly EnhancedSemanticChunker _semantic;
        private readonly ImprovedSemanticChunker _semantic;

        //private readonly ArabicDocumentChunker _arabicChunker = new ArabicDocumentChunker();

        private readonly UniversalDocumentChunker _universalDocumentChunker = new UniversalDocumentChunker();


        private readonly SmartChunker _smartChunker = new SmartChunker();

        //new///
        private readonly HttpClient _qwenFactory;




        private const int VECTOR_SIZE = 1024;
        #region Old Colllection And Models
        //private const string COLLECTION = "questionnaire_embeddings";
        //private const string COLLECTION = "For_Embedding";
        //private const string EMBEDDING_MODEL = "snowflake-arctic-embed2:latest"; 
        //private const string EMBEDDING_MODEL = "bge-m3:latest"; 
        #endregion

        private const string EMBEDDING_MODEL = "/opt/hf/intfloat__multilingual-e5-large-instruct";
        private const int EMBEDDING_CONCURRENCY = 12;
        private const int BATCH_SIZE = 750;
        private const int CHUNK_SIZE = 512;     // Target chunk size
        private const int MIN_CHUNK_SIZE = 250;  // Don't make chunks too tiny
        private const int OVERLAP = 150;
        //Rag_Modified_For_ORIGINAL_PROJECT
        //private const string UNIFIED_COLLECTION = "rag_knowledge_unified";

        //rag_new Collection for test new Embeddings
        //private const string UNIFIED_COLLECTION = "rag_new"; // OLD 1/12/2026 FOR FILES
        //private const string UNIFIED_COLLECTION = "rag_semantic"; // NEW JINA

        //private const string UNIFIED_COLLECTION = "rag_sem"; // For Original Project#############

        //private const string UNIFIED_COLLECTION = "new_Doc"; // Hybird chunker class

       // private const string UNIFIED_COLLECTION = "sem_Doc"; // Hybird chunker class using new Qdrant Configuration
     private const string UNIFIED_COLLECTION = "sem_New_Test"; // Hybird chunker class using new Qdrant Configuration
        //private const string UNIFIED_COLLECTION = "rag_knowledge_unified"; // لإقغ صهفا ×مي ُةلاثييهىل 
        private const int TOP_K = 5;
        private static ulong pointId = 1;
        private const string RERANK_MODEL = "qllama/bge-reranker-v2-m3";
        private const int RERANK_VECTOR_SIZE = 1024;
        //private readonly SemanticChunker _semanticChunker;

        //private readonly DummyEmbeddingGenerator _dummy;
        private readonly HttpClient _jinaClient;
        private readonly HttpClient _localAiClient;

        private readonly AdvancedSemanticChunker _advSemantic;

        private readonly ChunkingService _chunkService;

        //Data For Qwen

        private const string QWEN_RERANKER_URL = "http://10.21.2.143:8000/rerank";

        //new Http Client For Pyhton Code
        private static readonly HttpClient _client = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:8000"),
            Timeout = TimeSpan.FromMinutes(10)   // ← 10 دقايق كافية أول مرة
        };
        public EmbeddingService(IHttpClientFactory factory, MyDbContext db, ILogger<EmbeddingService> logger , ImprovedSemanticChunker semantic , AdvancedSemanticChunker advSemantic , ChunkingService chunkService, IConfiguration? config = null)
        {
            _db = db;
            _logger = logger;

            // Stage B switch. Off by default: indexing keeps re-chunking the flat
            // _clean.txt with SemanticChunkAsync exactly as before. Turn it on in
            // appsettings.json to index the chunks extract_universal.py already
            // produced (heading paths, real page numbers, one chunk per table row).
            // Switch UNIFIED_COLLECTION at the same time to compare side by side
            // rather than mixing two chunkings in one collection.
            _usePythonChunks = config?.GetValue<bool>("Extraction:UsePythonChunks") ?? false;
            _ollamaClient = factory.CreateClient();
            //_ollamaClient.BaseAddress = new Uri("http://localhost:11434/api/");
            //_ollamaClient.BaseAddress = new Uri("http://localhost:11434/api/");
            _ollamaClient.BaseAddress = new Uri("http://10.21.2.143:8000/");


            _ollamaLocal = factory.CreateClient();
            _ollamaLocal.BaseAddress = new Uri("http://localhost:11434/api/");

            _ollama = factory.CreateClient();
            _ollama.BaseAddress = new Uri("http://10.21.2.143:11434/api/");

            _qdrantClient = factory.CreateClient();
            _qdrantClient.BaseAddress = new Uri("http://localhost:6333/");

            _jinaClient = factory.CreateClient("JinaClient");


            _localAiClient = factory.CreateClient("LocalAIClient");

            //------------------------
            _semantic = semantic;

            //-------------------------

            _advSemantic = advSemantic;

            //-----------------------For Qwen.6B_embeddings
            _qwenFactory = factory.CreateClient();
            _qwenFactory.BaseAddress = new Uri(QWEN_RERANKER_URL);
            _qdrantClient.Timeout = TimeSpan.FromSeconds(120);

            //---------------------------
            _chunkService = chunkService;


        }


        // ============================================================
        // 1. TEXT CLEANING
        // ============================================================
        private string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = Regex.Replace(text, @"\s+", " ").Trim();
            text = Regex.Replace(text, @"[^\u0600-\u06FFa-zA-Z0-9\s\.\-\/]", "");
            return text;
        }

        //------Get Number Of Points

        public  async Task<ulong> GetQdrantPointsCountAsync( string collectionName)
        {
            // Qdrant REST API endpoint لعدد النقاط
            var url = $"collections/{collectionName}/points/count";

            var jsonContent = new { }; // الـ body فاضي لكن لازم يكون JSON

            var content = new StringContent(JsonSerializer.Serialize(jsonContent), Encoding.UTF8, "application/json");

            var response = await _qdrantClient.PostAsync(url, content);


            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"فشل جلب العدد: {response.StatusCode}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();

            // مثال على الـ response:
            // { "result": { "count": 15420 }, "status": "ok", "time": 0.0012 }
            using var doc = JsonDocument.Parse(jsonResponse);
            var count = doc.RootElement
                           .GetProperty("result")
                           .GetProperty("count")
                           .GetUInt64();

            return count;
        }

        //--------------------------------
        // ============================================================



        // ===================================================================
        #region SEMANTIC_CHUNK_NEW_3_12_2025
        // YOUR FINAL SEMANTIC CHUNK FUNCTION (copy-paste ready)
        // ===================================================================
        public async Task<IEnumerable<(string Text, int PageNumber, string Section, int ApproxTokens)>> SemanticChunkAsync(
      string fullText,
      int startPage = 1)
        {
            var chunks = new List<(string Text, int PageNumber, string Section, int ApproxTokens)>();
            if (string.IsNullOrWhiteSpace(fullText)) return chunks;

            var lines = fullText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Select(line =>
                {
                    if ((line.StartsWith("\"") && line.EndsWith("\"")) || (line.StartsWith("'") && line.EndsWith("'")))
                        line = line[1..^1];
                    return line.Trim();
                })
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();

            var bufferLines = new List<string>();
            int currentPage = startPage;
            string currentSection = "مقدمة";

            // CRITICAL SETTINGS FOR KUWAIT GOVERNMENT OCR TEXT
            const double BREAK_THRESHOLD = 0.83;        // Very high because text is super repetitive
            const int MIN_LINES_PER_CHUNK = 4;
            const int MAX_LINES_PER_CHUNK = 50;         // HARD LIMIT – this is what saved you
            const int FORCE_BREAK_EVERY_LINES = 45;     // Safety net
            const int OVERLAP_SENTENCES = 4;
            const int VECTOR_SIZE = 1024;

            int EstimateTokens(string s) => s.Length / 4 + 50;

            async Task<float[][]> GetEmbeddingsBatchAsync(IEnumerable<string> texts)
            {
                var prefixed = texts.Select(t => "query: " + t.Trim()).ToArray();
                var payload = new { inputs = prefixed };
                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                var response = await _ollamaClient.PostAsync("bulk/embed", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Embedding failed: {Body}", body);
                    return prefixed.Select(_ => new float[VECTOR_SIZE]).ToArray();
                }

                var json = JObject.Parse(body);
                return json["embeddings"]!
                        .Select(t => t.Select(v => (float)v).ToArray())
                        .ToArray();
            }

            void FlushBuffer(string because = "")
            {
                if (bufferLines.Count < MIN_LINES_PER_CHUNK) return;
                var text = string.Join("\n", bufferLines).Trim();
                if (text.Length < 180) return;

                chunks.Add((text, currentPage, currentSection, EstimateTokens(text)));
                _logger.LogInformation($"Chunk flushed ({because}): {bufferLines.Count} lines, page {currentPage}, section: {currentSection[..Math.Min(40, currentSection.Length)]}...");

                // Overlap: keep last N real sentences
                var lastSentences = text
                    .Split(new[] { '.', '!', '?', '۔', ';', '،' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 10)
                    .TakeLast(OVERLAP_SENTENCES)
                    .ToList();

                bufferLines.Clear();

                if (lastSentences.Any())
                {
                    var overlap = string.Join(". ", lastSentences);
                    bufferLines.AddRange(overlap.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine;

                // 1. PAGE MARKER → FORCE BREAK
                if (line.Contains("<<PAGE:"))
                {
                    if (bufferLines.Count > 8) FlushBuffer("PAGE MARKER");
                    if (Regex.Match(line, @"\d+").Success)
                        currentPage = int.Parse(Regex.Match(line, @"\d+").Value);
                    continue;
                }

                // 2. SKIP FOOTERS
                if (line.Length < 140 && (
                    line.Contains("وزارة التجارة") ||
                    line.Contains("دولة الكويت") ||
                    line.Contains("State of Kuwait") ||
                    Regex.IsMatch(line, @"صفحة\s*\d+", RegexOptions.IgnoreCase)))
                    continue;

                // 3. SECTION TITLE → FORCE BREAK + start new chunk
                if (line.Length > 15 && line.Length < 220 &&
                    Regex.IsMatch(line, @"^[أ-ي٠-٩\s\.\-:()،؛""'']+$") &&
                    !line.StartsWith("•") && !line.StartsWith("*") && !line.Contains("▪") &&
                    !line.Contains("استعلام") && !line.Contains("اختيار") && !line.Contains("إضافة") &&
                    !line.Contains("تقديم") && !line.Contains("ادخال"))
                {
                    if (bufferLines.Count > 6) FlushBuffer("SECTION TITLE");
                    currentSection = line;
                    bufferLines.Add(line);
                    continue;
                }

                bufferLines.Add(line);

                // 4. HARD LIMIT – THIS IS THE LINE THAT FIXES YOUR PROBLEM
                if (bufferLines.Count >= MAX_LINES_PER_CHUNK)
                {
                    FlushBuffer("MAX LINES REACHED");
                    continue;
                }

                // 5. Safety net every ~45 lines
                if (bufferLines.Count >= FORCE_BREAK_EVERY_LINES && bufferLines.Count % 20 == 0)
                {
                    FlushBuffer("SAFETY NET");
                }

                // 6. Semantic break (still useful for subtle shifts)
                if (bufferLines.Count >= 20 && bufferLines.Count % 15 == 0)
                {
                    var embeddings = await GetEmbeddingsBatchAsync(bufferLines);
                    var recent = embeddings.TakeLast(6).ToArray();
                    var previous = embeddings.SkipLast(6).TakeLast(6).ToArray();

                    if (previous.Length == 6)
                    {
                        float sim = CosineSimilarity_NEW(Average(recent), Average(previous));
                        if (sim < BREAK_THRESHOLD)
                            FlushBuffer("SEMANTIC BREAK");
                    }
                }
            }

            if (bufferLines.Count >= MIN_LINES_PER_CHUNK)
                FlushBuffer("FINAL");

            // Fallback (never triggers now)
            if (chunks.Count == 0)
            {
                var fallback = string.Join("\n", lines.Where(l => l.Length > 20));
                chunks.Add((fallback, 2, "كامل الوثيقة", EstimateTokens(fallback)));
            }

            return chunks;
        }

        // ── Helpers (keep these in your class) ─────────────────────────────────────
        private static float[] Average(float[][] vectors)
        {
            var result = new float[1024];
            foreach (var v in vectors)
                for (int i = 0; i < 1024; i++)
                    result[i] += v[i];
            for (int i = 0; i < 1024; i++)
                result[i] /= vectors.Length;
            return result;
        }

        private static float CosineSimilarity_NEW(float[] a, float[] b)
        {
            float dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB) + 1e-8f);
        }


        #endregion




        //------------------------------
        #region GenerateEmbeddingsBatchAsync For Multi Model Embedding e5 
        //Make IT OLD TO TRY JINA For e5 Multi Model
        public async Task<float[][]> GenerateEmbeddingsBatchAsync_OLD_ONE(string[] texts, bool isQuery = false)
        {
            var cleaned = texts.Where(t => !string.IsNullOrWhiteSpace(CleanText(t))).Select(CleanText).ToArray();
            if (!cleaned.Any()) return System.Array.Empty<float[]>();

            // هذي الخطوة اللي كانت ناقصة من شهور!
            if (EMBEDDING_MODEL.Contains("multilingual-e5-large-instruct", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Select(t => isQuery ? $"query: {t}" : $"passage: {t}").ToArray();
            }

            var payload = new
            {
                model_id = EMBEDDING_MODEL,
                texts = cleaned,
                // احذف is_query = true خالص – النموذج ما بيستخدمهاش
                batch_size = cleaned.Length
            };

            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await _ollamaClient.PostAsync("bulk/embed", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Batch embed failed: {Status} {Body}", response.StatusCode, body);
                    return cleaned.Select(_ => new float[VECTOR_SIZE]).ToArray();
                }

                var root = JObject.Parse(body);
                if (!root.TryGetValue("embeddings", out var embeddingsToken) || embeddingsToken.Type != JTokenType.Array)
                {
                    return cleaned.Select(_ => new float[VECTOR_SIZE]).ToArray();
                }

                var result = new List<float[]>();
                foreach (var token in embeddingsToken.Children())
                {
                    var vec = token.ToObject<float[]>();
                    if (vec != null && vec.Length == VECTOR_SIZE)
                    {
                        // L2 Normalization
                        double sum = 0;
                        for (int i = 0; i < vec.Length; i++) sum += vec[i] * vec[i];
                        float norm = (float)Math.Sqrt(sum);
                        if (norm > 1e-8)
                            for (int i = 0; i < vec.Length; i++) vec[i] /= norm;

                        result.Add(vec);
                    }
                    else
                    {
                        result.Add(new float[VECTOR_SIZE]);
                    }
                }

                return result.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch embedding failed");
                return cleaned.Select(_ => new float[VECTOR_SIZE]).ToArray();
            }
        } 
        #endregion



        #region BatchEmbeding For Files Generate
        // ============================================================
        // 2. BATCHED EMBEDDING
        // ============================================================
        public async Task<float[][]> GenerateEmbeddingsBatchAsync_OLD(string[] texts)
        {


            var cleaned = texts;

            if (!cleaned.Any()) return System.Array.Empty<float[]>();

            var payload = new
            {
                model_id = EMBEDDING_MODEL,
                texts = cleaned,
                is_query = true,
                batch_size = cleaned.Length
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _ollamaClient.PostAsync("bulk/embed", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Embed failed: {Status} {Body}", response.StatusCode, body);
                return cleaned.Select(_ => new float[VECTOR_SIZE]).ToArray();
            }

            var root = JsonSerializer.Deserialize<JsonElement>(body);
            if (!root.TryGetProperty("embeddings", out var arr)) return cleaned.Select(_ => new float[VECTOR_SIZE]).ToArray();

            var result = new List<float[]>();
            foreach (var emb in arr.EnumerateArray())
            {
                var vec = emb.Deserialize<float[]>()!;
                if (vec?.Length == VECTOR_SIZE)
                {
                    double sum = vec.Sum(x => (double)x * x);
                    float norm = (float)Math.Sqrt(sum);
                    if (norm > 0) for (int i = 0; i < vec.Length; i++) vec[i] /= norm;
                    result.Add(vec);
                }
                else result.Add(new float[VECTOR_SIZE]);
            }
            return result.ToArray();
        }

        #endregion


        // ============================================================
        // 2. SAFE EMBEDDING GENERATION (Retry + Debug + No Zeros)
        // ============================================================
        #region 2. Generate embedding – **prompt** field _ FOR e5 Multi Model
        //MAKE IT OLD ONE TO TRY JINA
        public async Task<float[]> GenerateEmbedding_OLD_ONE(string text, bool isQuery = false)
        {
            text = CleanText(text);

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogDebug("Empty text → zero vector.");
                return new float[VECTOR_SIZE];
            }

            // هذي الخطوة هي اللي كانت ناقصة عندك!
            if (EMBEDDING_MODEL.Contains("multilingual-e5-large-instruct", StringComparison.OrdinalIgnoreCase))
            {
                text = isQuery
                    ? $"query: {text}"
                    : $"passage: {text}";
            }
            // لو في المستقبل استخدمت bge-m3 أو nomic-embed-text-v2
            // else if (model.Contains("bge-m3")) text = isQuery ? $"Represent this sentence for retrieval: {text}" : text;

            var payload = new
            {
                model_id = EMBEDDING_MODEL,
                texts = new[] { text },
                // is_query = true,  // ← احذفها خالص، e5-large-instruct ما بيستخدمهاش
                batch_size = 1
            };

            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await _ollamaClient.PostAsync("bulk/embed", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Ollama error {Status}: {Body}", response.StatusCode, body);
                    return new float[VECTOR_SIZE];
                }

                var root = JObject.Parse(body);

                if (!root.TryGetValue("embeddings", out var embeddingsToken) ||
                    embeddingsToken.Type != JTokenType.Array ||
                    embeddingsToken.Count() == 0)
                {
                    _logger.LogWarning("No embeddings in response");
                    return new float[VECTOR_SIZE];
                }

                var embedding = embeddingsToken[0]!.ToObject<float[]>();

                if (embedding == null || embedding.Length != VECTOR_SIZE)
                {
                    _logger.LogError("Wrong embedding size: {0}", embedding?.Length ?? 0);
                    return new float[VECTOR_SIZE];
                }

                // L2 Normalization (إلزامي للـ e5 models)
                double sum = 0;
                for (int i = 0; i < embedding.Length; i++)
                    sum += embedding[i] * embedding[i];
                float norm = (float)Math.Sqrt(sum);
                if (norm > 0)
                    for (int i = 0; i < embedding.Length; i++)
                        embedding[i] /= norm;
                _logger.LogDebug("Embedding OK – {Len} dims", embedding.Length);
                return embedding;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding failed");
                return new float[VECTOR_SIZE];
            }
        }
        #endregion


        #region 2. Generate embedding – **prompt** field
        public async Task<float[]> GenerateEmbedding_OLD(string text)
        {
            text = CleanText(text);

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogDebug("Empty text → zero vector.");
                return new float[VECTOR_SIZE];

            }

            var payload = new
            {
                model_id = EMBEDDING_MODEL,
                texts = new[] { text },  // ← Wrap in array!
                is_query = true,
                batch_size = 1
            };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await _ollamaClient.PostAsync("bulk/embed", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Ollama error {Status}: {Body}", response.StatusCode, body);
                    return new float[VECTOR_SIZE];
                }

                var root = JsonSerializer.Deserialize<JsonElement>(body);
           
                // Correct: Get "embeddings" array

                if (!root.TryGetProperty("embeddings", out var embeddingsArray) ||
                    embeddingsArray.ValueKind != JsonValueKind.Array ||
                    embeddingsArray.GetArrayLength() == 0)
                {
                    _logger.LogWarning("No embeddings array in response: {Body}", body);
                    return new float[VECTOR_SIZE];
                }

                // Get first embedding: embeddings[0]
                var firstEmbedding = embeddingsArray[0];

                // Now deserialize as float[]
                var embedding = firstEmbedding.Deserialize<float[]>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (embedding == null || embedding.Length != VECTOR_SIZE)
                {
                    _logger.LogError("Invalid embedding size: {Actual}", embedding?.Length ?? 0);
                    return new float[VECTOR_SIZE];
                }

                // L2 Normalization
                double sum = 0;
                for (int i = 0; i < embedding.Length; i++)
                    sum += embedding[i] * embedding[i];

                float norm = (float)Math.Sqrt(sum);
                if (norm > 0)
                    for (int i = 0; i < embedding.Length; i++)
                        embedding[i] /= norm;

                _logger.LogDebug("Embedding OK – {Len} dims", embedding.Length);
                return embedding;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding failed for: {Text}", text);
                return new float[VECTOR_SIZE];
            }
        }
        #endregion

        // ============================================================
        // 3. HYBRID SEARCH + RE-RANKING
        // ============================================================

        //Clean Arabic Text Before put it into UnifiedResult

        private string CleanArabicText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Fix corrupted Bengali numerals that should be Arabic/English
            text = text.Replace("ক", "د");  // Bengali 'k' → Arabic 'd'
            text = text.Replace("।", ".");  // Bengali danda → period
            text = text.Replace("১", "1");  // Bengali digit 1
            text = text.Replace("২", "2");  // Bengali digit 2
            text = text.Replace("৩", "3");  // Bengali digit 3
            text = text.Replace("৪", "4");  // Bengali digit 4
            text = text.Replace("৫", "5");  // Bengali digit 5
            text = text.Replace("এ", "");   // Remove Bengali 'e'

            // Remove zero-width characters and other invisible Unicode
            text = text.Replace("\u200B", "")  // Zero-width space
                       .Replace("\u200C", "")  // Zero-width non-joiner
                       .Replace("\u200D", "")  // Zero-width joiner
                       .Replace("\uFEFF", ""); // Zero-width no-break space

            // Remove control characters except newlines
            text = new string(text.Where(c =>
                c == '\n' || c == '\r' || c == '\t' || !char.IsControl(c)
            ).ToArray());

            // Normalize multiple spaces
            text = System.Text.RegularExpressions.Regex.Replace(text, @" +", " ");

            // Fix common OCR errors in Arabic
            text = text.Replace("انيات", "انيات")  // Fix spacing
                       .Replace("وقيع", "توقيع")    // Fix missing character
                       .Replace("ولين", "والين");   // Fix spacing

            return text.Trim();
        }
        //---------------------------------------------

        #region Unified Search At "D"B" QD OLD UNIFIED SEARCH
        public async Task<List<UnifiedSearchResult>> SearchUnifiedAsync(string query, int topK = 10)
        {

           
            var queryEmbedding = await GenerateEmbedding_OLD_ONE(query,isQuery:true); // your existing method

            // Extract license number for smart boosting
            var licMatch = Regex.Match(query, @"LIC?\d+/\d+", RegexOptions.IgnoreCase);
            var licNo = licMatch.Success ? licMatch.Value.Replace("LIC", "", StringComparison.OrdinalIgnoreCase).Trim() : null;

            var searchPayload = new
            {
                vector = new { name = "smart", vector = queryEmbedding },
                limit = Math.Min(100, topK * 10),
                with_payload = true,
                with_vectors = false
            };


            var content = new StringContent(JsonSerializer.Serialize(searchPayload), Encoding.UTF8, "application/json");
            var response = await _qdrantClient.PostAsync($"collections/{UNIFIED_COLLECTION}/points/search", content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            var hits = result.GetProperty("result").EnumerateArray();

            var candidates = new List<UnifiedSearchResult>();

            //var sourceType = "";


            foreach (var hit in hits)
            {
                var score = hit.GetProperty("score").GetSingle();
                var payload = hit.GetProperty("payload");
                var sourceType = payload.GetProperty("source_type").GetString() ?? "unknown";

                var item = new UnifiedSearchResult
                {
                    SourceType = sourceType,
                    Score = score,
                    RawPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(payload.GetRawText())!,
                };

                // File fields
                if (sourceType == "file")
                {
                    item.FileName = payload.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
                    item.UploadDate = payload.TryGetProperty("upload_date", out var ud) ? ud.GetString() : null;
                    item.Text = payload.GetProperty("Text").GetString() ?? "";
                }

                // DB fields
                if (sourceType == "db")
                {
                    item.EntityType = payload.GetProperty("EntityType").GetString() ?? "";
                    item.EntityId = payload.TryGetProperty("EntityId", out var id) ? id.GetDecimal() : 0;
                    item.Text = payload.GetProperty("EntityType").GetString() ?? "";
                }

                candidates.Add(item);
            }

            // Final Boosting & Ranking (this is where accuracy explodes)
            var finalResults = candidates.Take(topK).ToList();


        //    var fileSourceCount = finalResults.Where(e => e.SourceType == "file").Count();
          //  var dbSourceCount = finalResults.Where(e => e.SourceType == "db").Count();
            
            //rerank with jina
            //var finals = await RerankWithJinaV3Async(query, finalResults);
            //var finals = await RerankWithJinaV3Async_LOCAL(query, finalResults);
            //var finals = await RerankWithJinaV3Async_API(query, finalResults);
            // var finals = await RerankWithQwen3Async_LOCAL(query, finalResults);

            //Stop Rerak For Now Untill Modify Code python Of Semantic Chunks....DOne

       //     var finals = new List<UnifiedSearchResult>();

         //   if(fileSourceCount>dbSourceCount)
           // {
                var finals = await RerankWithQwen3Async_Server(query, finalResults);

                  return finals;

            //}

            //else if (fileSourceCount<dbSourceCount)
            //{
            //   finals = await RerankWithQwen3Async_Server(query, finalResults, 30);
            //   return finals;

            //}
            //else
            //{
            //    //WE Will TEST This for later and then we will try the above.
            //    var finalDbResults = finalResults.Where(e => e.SourceType == "db").ToList();

            //    var DBfinals = finalDbResults.Take(10).ToList();
            //    return DBfinals;
            //}

               // return finalResults;
            //return finalResults;
        }
        #endregion


        #region Unified Search At DB QD OLD UNIFIED SEARCH
        public async Task<List<UnifiedSearchResult>> SearchUnifiedAsync_OLD(string query, int topK = 30)
        {
            var queryEmbedding = await GenerateEmbedding(query); // your existing method

            // Extract license number for smart boosting
            var licMatch = Regex.Match(query, @"LIC?\d+/\d+", RegexOptions.IgnoreCase);
            var licNo = licMatch.Success ? licMatch.Value.Replace("LIC", "", StringComparison.OrdinalIgnoreCase).Trim() : null;

            var searchPayload = new
            {
                vector = new
                {
                    name = "smart",
                    vector = queryEmbedding
                },
                limit = topK , // get more candidates for reranking
                with_payload = true
            };



            var content = new StringContent(JsonSerializer.Serialize(searchPayload), Encoding.UTF8, "application/json");
            var response = await _qdrantClient.PostAsync($"collections/{UNIFIED_COLLECTION}/points/search", content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            var hits = result.GetProperty("result").EnumerateArray();

            var candidates = new List<UnifiedSearchResult>();

            foreach (var hit in hits)
            {
                var score = hit.GetProperty("score").GetSingle();
                var payload = hit.GetProperty("payload");
                var sourceType = payload.GetProperty("source_type").GetString() ?? "unknown";

                var item = new UnifiedSearchResult
                {
                    SourceType = sourceType,
                    Score = score,
                    RawPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(payload.GetRawText())!,
                };

                // File fields
                if (sourceType == "file")
                {
                    item.FileName = payload.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
                    item.Page = payload.TryGetProperty("page", out var pg) ? (int?)pg.GetInt32() : null;
                    item.Section = payload.TryGetProperty("section", out var sec) ? sec.GetString() : null;
                    item.UploadDate = payload.TryGetProperty("upload_date", out var ud) ? ud.GetString() : null;
                    item.Text = payload.GetProperty("text").GetString() ?? "";
                }

                // DB fields
                if (sourceType == "db")
                {
                    item.EntityType = payload.GetProperty("EntityType").GetString() ?? "";
                    item.EntityId = payload.TryGetProperty("EntityId", out var id) ? id.GetDecimal() : 0;


                }

                candidates.Add(item);
            }

            // Final Boosting & Ranking (this is where accuracy explodes)
            var finalResults = candidates.Take(topK).ToList();


            //old_Rank_With_RerankWithBgeRerankerAsync
            //var finals =await RerankWithBgeRerankerAsync(query, finalResults);

            //New_Rank_With_TrueCrossEncoder
            //var finals =await RerankWithTrueCrossEncoderAsync(query, finalResults);

            return finalResults;
        }
        #endregion

        #region ReRanking With bge-3
        //Rerank The Results//
        // دالة الـ Reranking (شغالة على 8GB VRAM بسهولة)
        public async Task<List<UnifiedSearchResult>> RerankWithBgeRerankerAsync(
             string query,
             List<UnifiedSearchResult> candidates,
             int topK = 20)
        {
            if (candidates == null || candidates.Count <= topK)
                return candidates ?? new List<UnifiedSearchResult>();

            var candidateTexts = candidates
                .Select(c => (c.Text ?? c.FileName ?? c.EntityType ?? "").Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();

            if (!candidateTexts.Any())
                return candidates.Take(topK).ToList();

            try
            {
                // 1. جلب embedding للـ query (واحد بس)
                var queryEmbedding = await GenerateEmbedding(query , isQuery:true);

                // 2. جلب embeddings لكل المرشحين دفعة واحدة (batch)
                var docEmbeddings = await GenerateEmbeddingsBatchAsync(candidateTexts,isQuery:false);

                // 3. حساب Cosine Similarity → Reranking
                var scoredResults = new List<(UnifiedSearchResult Result, double Score, int OriginalIndex)>();

                for (int i = 0; i < candidates.Count; i++)
                {
                    double score = 0.0;
                    if (i < docEmbeddings.Length && docEmbeddings[i] != null && docEmbeddings[i].Length == RERANK_VECTOR_SIZE)
                    {
                        score = CosineSimilarity(queryEmbedding, docEmbeddings[i]);
                    }

                    scoredResults.Add((candidates[i], score, i));
                }

                // 4. ترتيب وأخذ أفضل topK
                var finalResults = scoredResults
                    .OrderByDescending(x => x.Score)
                    .Take(topK)
                    .Select(x =>
                    {
                        x.Result.Score = (float)x.Score; // اختياري: تحديث الـ Score في النتيجة
                        return x.Result;
                    })
                    .ToList();

                _logger.LogInformation(
                    "Reranking completed with {Model} | Candidates: {Count} → Top {TopK} | Best score: {Best:F4}",
                    RERANK_MODEL, candidates.Count, topK, finalResults.FirstOrDefault()?.Score ?? 0);

                return finalResults;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reranking failed with {Model}", RERANK_MODEL);
                return candidates.Take(topK).ToList(); // fallback
            }
        }

        #endregion


        //------------------------------------
       



        //-----------------------------------------
        private double CosineSimilarity(float[] a, float[] b)
        {
            double dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            double norm = Math.Sqrt(normA) * Math.Sqrt(normB);
            return norm > 0 ? dot / norm : 0;
        }

        #region Old Sreach DB
        //public async Task<List<SearchResult>> SearchAsync(string query, int topK = 30)
        //{
        //    var queryEmbedding = await GenerateEmbedding(query);
        //    var licnMatch = Regex.Match(query, @"LIC?\d+/\d+", RegexOptions.IgnoreCase);
        //    var licnNo = licnMatch.Success ? licnMatch.Value.ToUpper() : null;

        //    #region Filter
        //    //var filter = new Dictionary<string, object>();
        //    //if (licnNo != null)
        //    //{
        //    //    filter["must"] = new[]
        //    //    {
        //    //        new { should = new[]
        //    //        {
        //    //            new { key = "LicnNo", match = new { value = licnNo } },
        //    //            new { key = "LicnCommNo", match = new { value = licnNo } }
        //    //        }}
        //    //    };
        //    //} 
        //    #endregion

        //    var payload = new
        //    {
        //        vector = new
        //        {
        //            name = "smart",
        //            vector = queryEmbedding
        //        },
        //        limit = 30,
        //        with_payload = true
        //    };

        //    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        //    var response = await _qdrantClient.PostAsync($"collections/{UNIFIED_COLLECTION}/points/search", content);

        //    response.EnsureSuccessStatusCode();

        //    var json = await response.Content.ReadAsStringAsync();
        //    var result = JsonSerializer.Deserialize<JsonElement>(json);
        //    var hits = result.GetProperty("result").EnumerateArray();

        //    var candidates = new List<SearchResult>();

        //    foreach (var hit in hits)
        //    {
        //        var score = hit.GetProperty("score").GetSingle();
        //        var p = hit.GetProperty("payload");

        //        candidates.Add(new SearchResult
        //        {
        //            EntityType = p.GetProperty("EntityType").GetString() ?? "",
        //            EntityId = p.TryGetProperty("EntityId", out var id) ? id.GetDecimal() : 0,
        //            Payload = JsonSerializer.Deserialize<Dictionary<string, object>>(p.GetRawText())!,
        //            Score = score
        //        });
        //    }

        //    #region ReRANK
        //    // Re-rank: boost exact LicnNo
        //    //if (licnNo != null)
        //    //{
        //    //    candidates = candidates
        //    //        .OrderByDescending(c => c.Payload.Values.Any(v => v?.ToString()?.Contains(licnNo) == true) ? 1 : 0)
        //    //        .ThenByDescending(c => c.Score)
        //    //        .ToList();
        //    //}
        //    //else
        //    //{
        //    //    candidates = candidates.OrderByDescending(c => c.Score).ToList();
        //    //} 
        //    #endregion


        //    return candidates.Take(topK).ToList();
        //}

        #endregion





        // ============================================================
        // 4. FULL ASYNC INDEXING
        // ============================================================
        #region Embedding Data Of Database
        public async Task IndexAllDBAsync()
        {

            await EnsureCollectionAsync();

            var ID = pointId;
            var points_count = await GetQdrantPointsCountAsync(UNIFIED_COLLECTION);

            if(points_count >= pointId)
            {
                ID= points_count + 1;
            }

            var points = new List<object>();

            async Task AddPointAsync(string text, string entityType, Dictionary<string, object> payload)
            {
                text = CleanText(text);
                if (string.IsNullOrWhiteSpace(text)) return;

                var embedding = await GenerateEmbedding_OLD_ONE(text);
                if (embedding.Length != VECTOR_SIZE) return;

                points.Add(new
                {
                    id = ID++,
                    vectors = new { smart = embedding },  // ← بدل "vector"
                    payload = new Dictionary<string, object>(payload) { ["EntityType"] = entityType, ["source_type"] = "db" }
                });
            }

            // --- QuestionnaireData ---
            var questionnaires = await _db.QuestionnaireData.Select(q => new
            {
                q.QuestionnaireDataRecid,
                q.QuestionnaireDate,
                q.QuestionnaireNo,
                q.QuestionnaireResult,
                q.CompName,
                q.LicnNo
             ,
                q.LicnStatus,
                q.ComplienceOfficerName,
                q.IsActive,
                q.QuestionnaireTypeId,
                q.ExpieryDate,
                q.EmployeeCount


            })

                                .ToListAsync();

            var qTasks = questionnaires.Select(q => AddPointAsync(
                 $"شركة '{q.CompName}' \n رقم الرخصه {q.LicnNo}\n  حالة الرخصه {q.LicnStatus}\n  اسم ظابط الامتثال {q.ComplienceOfficerName ?? ""}\n {q.IsActive}\n {q.QuestionnaireTypeId}\n {q.QuestionnaireDataRecid}\n تاريخ الانتهاء : {q.ExpieryDate}\n {q.QuestionnaireResult} \n رقم الاستبيان: {q.QuestionnaireNo}",
                    "QuestionnaireDatum",
                    new Dictionary<string, object>
                    {
                        ["EntityId"] = 1,
                        ["QuestionnaireDataRecid"] = q.QuestionnaireDataRecid!,
                        ["LicnNo"] = q.LicnNo ?? "",
                        ["CompName"] = q.CompName ?? "",
                        ["LicnStatus"] = q.LicnStatus ?? "",
                        ["ComplienceOfficerName"] = q.ComplienceOfficerName ?? null,
                        ["IsActive"] = q.IsActive!,
                        ["QuestionnaireTypeId"] = q.QuestionnaireTypeId!,
                        ["QuestionnaireResult"] = q.QuestionnaireResult ?? null,
                        ["EmployeeCount"] = q.EmployeeCount ?? null,


                    }
                ));


            // --- Licenses ---
            var licenses = await _db.QuestionnaireAllLicns.Select(l => new { l.CompanyName, l.ActivityDesc, l.QuestionnaireDataRecid, l.LicnId, l.LicCivilId, l.LicnTypeDesc, l.CreatedOn, l.CreatedBy, l.LicnCommNo }).AsNoTracking().ToListAsync();

            var licTasks = licenses.Select(l => AddPointAsync(
                 $" شركة '{l.CompanyName}' , نوع الرخصه {l.LicnTypeDesc} , النشاط {l.ActivityDesc} , {l.CreatedOn} , رقم الرخصه المدنى {l.LicCivilId} , {l.QuestionnaireDataRecid} , رقم الرخصه  {l.LicnId} , رقم السجل التجارى {l.LicnCommNo}",
                    "QuestionnaireAllLicn",
                    new Dictionary<string, object>
                    {
                        ["EntityId"] = 2,
                        ["LicnId"] = l.LicnId!,
                        ["LicCivilId"] = l.LicCivilId ?? "",
                        ["CompanyName"] = l.CompanyName ?? "",
                        ["LicnTypeDesc"] = l.LicnTypeDesc ?? "",
                        ["ActivityDesc"] = l.ActivityDesc ?? "",
                        ["LicnCommNo"] = l.LicnCommNo ?? "",
                        ["CreatedOn"] = l.CreatedOn ?? null,
                        ["QuestionnaireDataRecid"] = l.QuestionnaireDataRecid ?? null

                    }
                ));



            // --- Owners ---
            var owners = await _db.QuestionnaireLicOwners.Select(o => new { o.OwnerName, o.LicCivilId, o.ReqOwnId, o.LicnRecId, o.CivilId, o.CivilNationName, o.CivilNationId, o.QuestionnaireDataRecid }).AsNoTracking().ToListAsync();

            var ownTasks = owners.Select(o => AddPointAsync(
                 $"مالك {o.OwnerName} , جنسيه {o.CivilNationName} , هوية {o.CivilId} , رقم الرخصه المدنى {o.LicCivilId} , {o.LicnRecId} ,  رقم طلب  {o.ReqOwnId} , {o.QuestionnaireDataRecid},",
                    "QuestionnaireLicOwner",
                    new Dictionary<string, object>
                    {
                        ["EntityId"] = 3,
                        ["ReqOwnId"] = o.ReqOwnId!,
                        ["OwnerName"] = o.OwnerName ?? "",
                        ["CivilNationName"] = o.CivilNationName ?? "",
                        ["CivilId"] = o.CivilId ?? "",
                        ["LicCivilId"] = o.LicCivilId ?? null,
                        ["QuestionnaireDataRecid"] = o.QuestionnaireDataRecid ?? null,
                        ["LicnRecId"] = o.LicnRecId ?? null
                    }
                ));




            await Task.WhenAll(
                            qTasks.Concat(licTasks).Concat(ownTasks)
                                  .Chunk(EMBEDDING_CONCURRENCY)               // groups of N
                                  .Select(chunk => Task.WhenAll(chunk))       // await each group
                        );




            const int BATCH_SIZE = 500;               // Qdrant recommends ≤ 512
            var pointArray = points.ToArray();

            for (int i = 0; i < pointArray.Length; i += BATCH_SIZE)
            {
                var batch = pointArray.Skip(i).Take(BATCH_SIZE).ToArray();
                var payload = new { points = batch };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var resp = await _qdrantClient.PutAsync($"collections/{UNIFIED_COLLECTION}/points", content);
                resp.EnsureSuccessStatusCode();

                _logger.LogInformation("Upserted batch {0}-{1}", i + 1, Math.Min(i + BATCH_SIZE, pointArray.Length));
            }

            _logger.LogInformation("Indexing finished – {0} points", pointArray.Length);

        }
        #endregion


        #region NEW_SURE_COLLECTION
        //NEw COLLECTION

        private async Task EnsureCollectionAsync()
        {
          

            //// 3. امسح القديمة إذا موجودة
            //if (exists)
            //{
            //    var deleteResp = await _qdrantClient.DeleteAsync(collectionUrl);
            //    if (deleteResp.IsSuccessStatusCode)
            //        _logger.LogInformation("Old collection deleted: {Collection}", UNIFIED_COLLECTION);
            //    else
            //        _logger.LogWarning("Could not delete old collection: {Status}", deleteResp.StatusCode);
            //}

            // 4. أنشئ الكوليكشن بالإعدادات الذهبية (2025 Gold Standard)
            var optimalConfig = new
            {
                vectors = new
                {
                    smart = new
                    {
                        size = VECTOR_SIZE,
                        distance = "Cosine",
                        // مهم جدًا للـ multilingual-e5-large-instruct
                        on_disk = true
                    }
                },
                quantization_config = new
                {
                    scalar = new
                    {
                        type = "int8",
                        quantile = 0.99,
                        always_ram = true
                    }
                },
                hnsw_config = new
                {
                    m = 16,
                    ef_construct = 200,      // أعلى = أدق indexing
                    ef_search = 128           // أعلى = أدق بحث
                },
                optimizers_config = new
                {
                    deleted_threshold = 0.2,
                    vacuum_min_vector_number = 1000,
                    default_segment_number = 5,
                    memmap_threshold = 50000,
                    indexing_threshold = 20000
                }
            };

            var json = JsonConvert.SerializeObject(optimalConfig, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            const string collectionUrl = $"collections/{UNIFIED_COLLECTION}";

            // 1. جيب التفاصيل الكاملة (مش بس الوجود)
            var getResp = await _qdrantClient.GetAsync(collectionUrl);
            bool exists = getResp.IsSuccessStatusCode;

            JObject currentConfig = null;
            if (exists)
            {
                var body = await getResp.Content.ReadAsStringAsync();
                var jsn = JObject.Parse(body);
                currentConfig = jsn["result"]?["config"]?.Value<JObject>();
            }



            // 2. تحقق إذا كانت الإعدادات الحالية مطابقة للي عايزينه
            bool needsRecreate = !exists ||
                currentConfig == null ||
                currentConfig["vectors"]?["smart"]?["size"]?.Value<int>() != VECTOR_SIZE ||
                !currentConfig["quantization_config"]?["scalar"]?.Value<bool>("enabled") == true ||
                currentConfig["hnsw_config"]?["m"]?.Value<int>() != 16;

            if (!needsRecreate)
            {
                _logger.LogInformation("Collection '{Collection}' already has optimal config (int8 + HNSW).", UNIFIED_COLLECTION);
                return;
            }

            _logger.LogWarning("Collection needs recreation with optimal settings (current: {Exists})", exists);

            var content = new StringContent(json, Encoding.UTF8, "application/json");


            if (getResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var putResp = await _qdrantClient.PutAsync(collectionUrl, content);
                putResp.EnsureSuccessStatusCode();
                _logger.LogInformation("Collection created with int8.");
            }
            else
            {
                _logger.LogInformation("Collection exists (int8 + main already applied).");
            }


    
          
        }
        //--------------------

        #endregion

        #region Ensure Collection is made
        private async Task EnsureCollectionAsync_OLD()
        {
            var config = new
            {
                vectors = new { smart = new { size = VECTOR_SIZE, distance = "Cosine" } },
                quantization_config = new { scalar = new { type = "int8", quantile = 0.99, always_ram = true } },
                hnsw_config = new { m = 16, ef_construct = 100 }
            };

            var content = new StringContent(JsonSerializer.Serialize(config), Encoding.UTF8, "application/json");
            var resp = await _qdrantClient.GetAsync($"collections/{UNIFIED_COLLECTION}");

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var putResp = await _qdrantClient.PutAsync($"collections/{UNIFIED_COLLECTION}", content);
                putResp.EnsureSuccessStatusCode();
                _logger.LogInformation("Collection created with int8.");
            }
            else
            {
                _logger.LogInformation("Collection exists (int8 + main already applied).");
            }
        }
        #endregion




        //------------------------------------------------------

        //IndexUploadedFilesAsync , Upsert , EnsureColllection
        // ============================================================
        // 3. فهرسة الملفات (تم تعديلها بالكامل) - تعديل 4
        // ============================================================

        #region Embedding Data From Files

        // ---- Stage B: consume the chunks extract_universal.py already produced ----

        private readonly bool _usePythonChunks;

        private static readonly JsonSerializerOptions PyChunkJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        private sealed class PyChunk
        {
            public string ChunkId { get; set; } = "";
            public string Content { get; set; } = "";
            public PyChunkMeta Metadata { get; set; } = new();
        }

        private sealed class PyChunkMeta
        {
            public string SectionTitle { get; set; } = "";
            public string Type { get; set; } = "";
            public int PageStart { get; set; }
            public int PageEnd { get; set; }
            public int TokenEstimate { get; set; }
            public int Level { get; set; }
        }

        /// <summary>
        /// Reads &lt;dir&gt;/Extracted/&lt;stem&gt;/&lt;stem&gt;_chunks.json, written by the same
        /// extractor run that produced _clean.txt. Returns null when the file is
        /// missing or unreadable so the caller falls back to the existing chunker
        /// rather than dropping the document.
        /// </summary>
        private List<Chunk_NEW>? LoadPythonChunks(string file)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(file));
                if (string.IsNullOrEmpty(dir)) return null;

                var stem = Path.GetFileNameWithoutExtension(file);
                var chunksPath = Path.Combine(dir, "Extracted", stem, stem + "_chunks.json");
                if (!File.Exists(chunksPath))
                {
                    _logger.LogWarning("لم يتم العثور على ملف القطع: {Path} — سيتم استخدام المقسّم القديم", chunksPath);
                    return null;
                }

                var json = File.ReadAllText(chunksPath);
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<PyChunk>>(json, PyChunkJsonOptions);
                if (parsed == null || parsed.Count == 0) return null;

                var fileName = Path.GetFileName(file);
                var mapped = parsed
                    .Where(c => !string.IsNullOrWhiteSpace(c.Content))
                    .Select(c => new Chunk_NEW
                    {
                        Text = c.Content,
                        CharacterCount = c.Content.Length,
                        file_name = fileName,
                        Section = c.Metadata.SectionTitle,
                        ChunkType = c.Metadata.Type,
                        PageNumber = c.Metadata.PageStart,
                        ApproxTokens = c.Metadata.TokenEstimate,
                        HierarchyLevel = c.Metadata.Level,
                        ChunkId = c.ChunkId
                    })
                    .ToList();

                return mapped.Count > 0 ? mapped : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "فشل قراءة قطع بايثون لـ {File} — سيتم استخدام المقسّم القديم", Path.GetFileName(file));
                return null;
            }
        }

        // File types extract_universal.py can read. Anything else is skipped rather
        // than fed to the extension switch.
        private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
            ".docx", ".docm", ".xlsx", ".xlsm", ".xls", ".pptx", ".pptm",
            ".html", ".htm", ".txt", ".csv", ".tsv", ".rtf",
            ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp"
        };

        // Folders the extractor writes into. Without this the indexer walks its own
        // output: pointing extract_universal.py at Uploads/ once found 458 "documents"
        // in a folder holding seven real ones.
        private static readonly HashSet<string> NonInputFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Extracted", ".ocr_cache"
        };

        private static bool IsIndexableInput(string path)
        {
            if (!IndexableExtensions.Contains(Path.GetExtension(path)))
                return false;

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(dir))
            {
                if (NonInputFolders.Contains(Path.GetFileName(dir)))
                    return false;
                dir = Path.GetDirectoryName(dir);
            }
            return true;
        }

        public async Task IndexUploadedFilesAsync(string folderPath = "Uploads")
        {

         

            await EnsureCollectionAsync();

            var ID = pointId;

            var points_count = await GetQdrantPointsCountAsync(UNIFIED_COLLECTION);

            if (points_count >= pointId)
            {
                ID = points_count + 1;
            }


                // Allowlist of things we can actually read, instead of a denylist of
                // output extensions. The old denylist excluded .txt and .csv, so an
                // uploaded text file was never indexed at all; and it did not exclude
                // .jsonl, so any new output extension would slip through into the
                // extension switch below and throw.
                var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
         .Where(f => IsIndexableInput(f))
         .ToArray();




            if (!files.Any())
            {
                _logger.LogInformation("لا توجد ملفات جديدة");
                return;
            }



            //---------new code 4:03------------------

            var existingFileNames = await GetExistingFileNamesAsync(); // implement this to query DB
            var newFiles = files.Where(f => !existingFileNames.Contains(Path.GetFileName(f))).ToArray();




            if (!newFiles.Any())
            {
                _logger.LogInformation("كل الملفات قد تم فهرستها مسبقاً");
                return;
            }

            //------------------

           var allChunks = new List<Chunk_NEW>();
            //var allChunks = new List<Chunk_HY>();


            foreach (var file in newFiles)
            {
                // Every format now goes through extract_universal.py, so DOCX/XLSX/PPTX
                // get the same Arabic ligature handling, OCR of embedded images and
                // table extraction that PDFs get.
                //
                // The old C# paths this replaces were not equivalent:
                //   ExtractDocxText   called Docnet (a PDFium wrapper) on a .docx, so it
                //                     always threw and returned "" -- DOCX silently
                //                     produced no text at all.
                //   ExtractExcelText  concatenated every cell space-separated with no
                //                     row or column structure.
                // The switch also had no default arm, so any other extension threw
                // SwitchExpressionException at runtime.
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string fullText = ext switch
                {
                    // PDFs and images keep ExtractAllText, which adds the existing
                    // text-layer check and re-run fallback around the same script.
                    ".pdf" or ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff"
                        or ".bmp" or ".webp"
                        => await Extractor.ExtractAllText(file),

                    ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".xls"
                        or ".pptx" or ".pptm" or ".html" or ".htm"
                        or ".txt" or ".csv" or ".tsv" or ".rtf"
                        => await Extractor.ExtractWithYourCurrentExtractPy_Claude(file),

                    _ => string.Empty
                };

                if (string.IsNullOrWhiteSpace(fullText))
                {
                    _logger.LogWarning("تم تخطي الملف (لا يوجد نص مستخرج): {File}", Path.GetFileName(file));
                    continue;
                }

                // Stage B: use the extractor's own chunks when enabled. Falls through
                // to the existing chunker if the file is missing, so a partial or
                // failed extraction degrades instead of losing the document.
                if (_usePythonChunks)
                {
                    var pyChunks = LoadPythonChunks(file);
                    if (pyChunks != null)
                    {
                        allChunks.AddRange(pyChunks);
                        _logger.LogInformation(
                            "تم استخدام قطع بايثون لـ {File}: {Count} قطعة ({Tables} صف جدول)",
                            Path.GetFileName(file), pyChunks.Count,
                            pyChunks.Count(c => c.ChunkType == "table_row"));
                        continue;
                    }
                }

                //var chunksList = await TryAgenticChunking(fullText, Path.GetFileName(file));
                //if (chunksList == null || chunksList.Count < 3)
                //{
                //_logger.LogInformation("Agentic failed → falling back to SmartChunk for {File}", Path.GetFileName(file));

                #region OLD_USE_SMART
                //var chunksList = SmartChunk(fullText).ToList();
                #endregion

                //try cleaned Text 

                var cleanedText = ArabicStuckWordsFixer.FixStuckWords(fullText);


                #region Arabic Chunker Class Extractor
                //arabic Chunker Use 1Class arabicDocChunker
                //var chunksList = _arabicChunker.SmartChunk(cleanedText).ToList(); 
                #endregion


                #region Universal Chunker Class Extractor
                //arabic Chunker Use 1Class arabicDocChunker
                // var chunksList = _universalDocumentChunker.ChunkDocument(cleanedText).ToList(); 
                #endregion


                #region SmartChunkerNEW Chunker Class Extractor
                //arabic Chunker Use 1Class arabicDocChunker
                // var chunksList = _smartChunker.ChunkText(cleanedText).ToList();
                #endregion




                //new Hybird Chunker Use



                //var chunksList = await SemanticChunkAsync(fullText);

                //new use of semantic chunk
                // var chunksList = await _semantic.SemanticChunkAsync(fullText); // Enhanced Semantic Chunking



                //stop Semantic Cunk Unrill Modify The Code Of Python
                // var chunksList = await _semantic.SemanticChunkAsync(cleanedText); // Improved Semantic Chunking

                //Try New Advanced Semantic Chunker
                var chunksList = await _semantic.SemanticChunkAsync(cleanedText); // Improved Semantic Chunking
                                                                                   //////}
                                                                                   ///
                    ///try new Chunking Service
                    ///
                    //var chunksList =  _chunkService.CreateFixedLengthChunks(cleanedText); // Advanced Semantic Chunking


               // var chunksList =  _chunkService.ChunkText(cleanedText); // Advanced Semantic Chunking 


                #region Chunk_Hybird_Startagy

                //var chunker = new HybridChunker(new HybridChunkingConfig
                //{
                //    MaxChunkSize = 2000,
                //    OverlapSize = 100,
                //    UseSemanticBoundaries = true
                //});

                //var chunkList = chunker.ChunkText(cleanedText);


                //foreach (var chunk in chunksList)
                //{
                //    allChunks.Add(new Chunk_HY
                //    {
                //        Text = chunk.Text,
                //        CharacterCount = chunk.CharacterCount,
                //        Index = chunk.Index,
                //        TotalChunks = chunk.TotalChunks,
                //        Id = chunk.Id,
                //        Metadata = chunk.Metadata,
                //        file_name = Path.GetFileName(file)



                //    });
                //}
                #endregion

                foreach (var chunk in chunksList)
                {
                    allChunks.Add(new Chunk_NEW
                    {
                        Text = chunk.Text,
                        CharacterCount = chunk.CharacterCount,
                        file_name = Path.GetFileName(file),
                        Section = chunk.Section,
                        ChunkType = chunk.ChunkType,
                        PageNumber = chunk.PageNumber

                    });
                 }



                        #region Use Of Chunks Tuple For SmartChunk
                        // For Smart Chunk
                        //foreach (var (text, page, section, tokens) in chunksList)
                        //{
                        //    allChunks.Add((text, Path.GetFileName(file), page, section));
                        //}

                        #region Class Improvement
                        ////For Class Enhancement

                        #endregion


                        //Try new Chunk ONE Class
                        //foreach (var chunk in chunksList)
                        //{
                        //    allChunks.Add(new Chunk_ONE
                        //    {
                        //        Content = chunk.Content,
                        //        StartIndex = chunk.StartIndex,
                        //        EndIndex = chunk.EndIndex,
                        //        ChunkType = chunk.ChunkType
                        //    });
                        //}



                        #region Arabic Chunker Class
                        ////Arabic Chunker Class
                        //foreach (var chunk in chunksList)
                        //{
                        //    allChunks.Add((chunk.Text, Path.GetFileName(file), chunk.PageNumber, chunk.Section));
                        //}
                        #endregion


                        #region Universal Arabic Chunker Class
                        //Arabic Chunker Class
                        //foreach (var chunk in chunksList)
                        //{
                        //    allChunks.Add((chunk.Content, Path.GetFileName(file), chunk.ChunkIndex, chunk.SectionTitle));
                        //} 
                        #endregion




                        #region SmartChunker Arabic Chunker Class
                        ////Arabic Chunker Class
                        //foreach (var chunk in chunksList)
                        //{
                        //    allChunks.Add((chunk.Text, Path.GetFileName(file), chunk., ""));
                        //}
                        #endregion



                        #endregion


                        #endregion

                        #region NEW_CHUNK_USE
                        //NEW_USE_CHUNK_SAMRTT

                        //// استخدم السطر الجديد ده:
                        //var chunksList = _chunker.SmartChunk_Pro(fullText, Path.GetFileName(file))
                        //                    .Select(c => (c.Text, c.PageNumber, c.Section))
                        //                    .ToList();

                        //// والباقي يفضل زي ما هو
                        //foreach (var (text, page, section) in chunksList)
                        //{
                        //    allChunks.Add((text, Path.GetFileName(file), page, section));
                        //}


                        //------------------- 
                        #endregion

                        // Replace this whole region:
                        // #region NEW_CHUNK_USE_2_TURBO ... #endregion

                        // With this:


                    }

            var texts = allChunks.Select(c => c.Text).ToArray();
            var embeddings = await GenerateEmbeddingsBatchAsync_OLD_ONE(texts , isQuery: false);

            var points = new List<object>();
            for (int i = 0; i < allChunks.Count; i++)
            {
                //var (text, fileName , page , section) = allChunks[i];

                var newChunk = allChunks[i];
                points.Add(new
                {
                    //id = pointId++,
                    id = ID++,
                    vector = embeddings[i],
                    vector_name = "smart",
                    payload = new
                    {
                        newChunk.Text,
                        newChunk.PageNumber,
                        newChunk.file_name,
                        newChunk.ChunkType,
                        upload_date = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                        source = "uploaded_file",
                        source_type = "file",
                    }

                    //for hybird chunker
                    //payload = new
                    //{
                    //    newChunk.Text,
                    //   newChunk.TotalChunks,
                    //      newChunk.Index,
                    //     newChunk.Metadata,
                    //     newChunk.file_name,
                    //     upload_date = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    //    source = "uploaded_file",
                    //    source_type = "file",
                    //}
                });
            }

            await UpsertBatchAsync(points);
            _logger.LogInformation("تم فهرسة {Count} قطعة من الملفات بنجاح", points.Count);
        }
        




        #region Put Files Data in Qdrant
        private async Task UpsertBatchAsync(List<object> points)
        {
            var formattedPoints = points.Select(p => new
            {
                id = ((dynamic)p).id,
                vector = new Dictionary<string, float[]> { { "smart", ((dynamic)p).vector } },
                payload = ((dynamic)p).payload
            }).ToList();

            var payload = new { points = formattedPoints };
            var json = JsonSerializer.Serialize(payload);
            _logger.LogInformation("Upsert → {Json}", json);

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _qdrantClient.PutAsync($"collections/{UNIFIED_COLLECTION}/points", content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Qdrant Error: {Error}", error);
                throw new Exception(error);
            }

            _logger.LogInformation("تم إضافة {Count} نقطة بنجاح", formattedPoints.Count);
        }
        #endregion

        #region NEW_CHUNK_SMART_CHUNK_VAILD_ONE_BEFORE_SEMANTIC
        //-------------------------------------------------------
        private IEnumerable<(string Text, int PageNumber, string Section, int ApproxTokens)> SmartChunk(
    string fullText,
    int startPage = 1)
        {
            var chunks = new List<(string, int, string, int)>();
            if (string.IsNullOrWhiteSpace(fullText)) return chunks;

            // CRITICAL FIX: Clean JSON-like quoted lines
            var rawLines = fullText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var lines = rawLines
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line =>
                {
                    // Remove surrounding quotes if present
                    if ((line.StartsWith("\"") && line.EndsWith("\"")) ||
                        (line.StartsWith("'") && line.EndsWith("'")))
                    {
                        line = line.Substring(1, line.Length - 2);
                    }
                    return line.Trim();
                })
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            var buffer = new StringBuilder();
            int currentPage = startPage;
            string currentSection = "مقدمة";
            int lineCount = 0;
            const int TARGET_LINES = 42;
            const int OVERLAP = 8;

            int EstimateTokens(string s) => (s.Length / 4) + 50;

            void Flush()
            {
                var text = buffer.ToString().Trim();
                if (text.Length < 250) return; // too small

                chunks.Add((text, currentPage, currentSection, EstimateTokens(text)));

                // Keep overlap
                var lastLines = text.Split('\n').TakeLast(OVERLAP).ToArray();
                buffer.Clear();
                if (lastLines.Any())
                    buffer.Append(string.Join("\n", lastLines) + "\n");
                lineCount = lastLines.Length;
            }

            foreach (var line in lines)
            {
                // PAGE DETECTION — now works with "<<PAGE: 5>>", "<<PAGE:5>>", etc.
                if (line.Contains("<<PAGE:"))
                {
                    if (lineCount > 10) Flush();

                    var match = Regex.Match(line, @"\d+");
                    if (match.Success && int.TryParse(match.Value, out int p))
                        currentPage = p;

                    continue;
                }

                // Skip headers/footers
                if (line.Length < 120 && (
                    line.Contains("وزارة التجارة") ||
                    line.Contains("دولة الكويت") ||
                    line.Contains("State of Kuwait") ||
                    Regex.IsMatch(line, @"صفحة\s*\d+", RegexOptions.IgnoreCase)))
                    continue;

                // Section title detection (relaxed for real Arabic)
                if (line.Length > 15 && line.Length < 180 &&
                    Regex.IsMatch(line, @"^[أ-ي٠-٩\s\.\-:()،؛]+$") &&
                    !line.StartsWith("•") && !line.StartsWith("*") && !line.Contains("▪"))
                {
                    if (lineCount > 15) Flush();
                    currentSection = line;
                }

                buffer.AppendLine(line);
                lineCount++;

                if (lineCount >= TARGET_LINES)
                    Flush();
            }

            // Final flush
            if (lineCount > 10)
            {
                var final = buffer.ToString().Trim();
                if (final.Length >= 250)
                    chunks.Add((final, currentPage, currentSection, EstimateTokens(final)));
            }

            // SAFETY NET: if still empty but we have content
            if (chunks.Count == 0 && lines.Any(l => l.Contains("التأسيس") || l.Contains("السجل التجاري")))
            {
                var fallback = string.Join("\n", lines.Where(l => l.Length > 20));
                chunks.Add((fallback, 2, "كامل الوثيقة", EstimateTokens(fallback)));
            }

            return chunks;
        }
        private IEnumerable<(string Text, int PageNumber, string Section, int ApproxTokens)> SmartChunk_OLD_13_12_2025(
    string fullText,
    int startPage = 1)
        {
            var chunks = new List<(string, int, string, int)>();
            if (string.IsNullOrWhiteSpace(fullText)) return chunks;

            // CRITICAL FIX: Clean JSON-like quoted lines
            var rawLines = fullText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var lines = rawLines
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line =>
                {
                    // Remove surrounding quotes if present
                    if ((line.StartsWith("\"") && line.EndsWith("\"")) ||
                        (line.StartsWith("'") && line.EndsWith("'")))
                    {
                        line = line.Substring(1, line.Length - 2);
                    }
                    return line.Trim();
                })
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            var buffer = new StringBuilder();
            int currentPage = startPage;
            string currentSection = "مقدمة";
            int lineCount = 0;
            const int TARGET_LINES = 42;
            const int OVERLAP = 8;

            int EstimateTokens(string s) => (s.Length / 4) + 50;

            void Flush()
            {
                var text = buffer.ToString().Trim();
                if (text.Length < 250) return; // too small

                chunks.Add((text, currentPage, currentSection, EstimateTokens(text)));

                // Keep overlap
                var lastLines = text.Split('\n').TakeLast(OVERLAP).ToArray();
                buffer.Clear();
                if (lastLines.Any())
                    buffer.Append(string.Join("\n", lastLines) + "\n");
                lineCount = lastLines.Length;
            }

            foreach (var line in lines)
            {
                // PAGE DETECTION — now works with "<<PAGE: 5>>", "<<PAGE:5>>", etc.
                if (line.Contains("<<PAGE:"))
                {
                    if (lineCount > 10) Flush();

                    var match = Regex.Match(line, @"\d+");
                    if (match.Success && int.TryParse(match.Value, out int p))
                        currentPage = p;

                    continue;
                }

                // Skip headers/footers
                if (line.Length < 120 && (
                    line.Contains("وزارة التجارة") ||
                    line.Contains("دولة الكويت") ||
                    line.Contains("State of Kuwait") ||
                    Regex.IsMatch(line, @"صفحة\s*\d+", RegexOptions.IgnoreCase)))
                    continue;

                // Section title detection (relaxed for real Arabic)
                if (line.Length > 15 && line.Length < 180 &&
                    Regex.IsMatch(line, @"^[أ-ي٠-٩\s\.\-:()،؛]+$") &&
                    !line.StartsWith("•") && !line.StartsWith("*") && !line.Contains("▪"))
                {
                    if (lineCount > 15) Flush();
                    currentSection = line;
                }

                buffer.AppendLine(line);
                lineCount++;

                if (lineCount >= TARGET_LINES)
                    Flush();
            }

            // Final flush
            if (lineCount > 10)
            {
                var final = buffer.ToString().Trim();
                if (final.Length >= 250)
                    chunks.Add((final, currentPage, currentSection, EstimateTokens(final)));
            }

            // SAFETY NET: if still empty but we have content
            if (chunks.Count == 0 && lines.Any(l => l.Contains("التأسيس") || l.Contains("السجل التجاري")))
            {
                var fallback = string.Join("\n", lines.Where(l => l.Length > 20));
                chunks.Add((fallback, 2, "كامل الوثيقة", EstimateTokens(fallback)));
            }

            return chunks;
        }


        //--------------------------------------------------

        private IEnumerable<(string Text, int PageNumber, string Section, int ApproxTokens)> SmartChunk_OLD_TWO(
        string fullText,
        int startPage = 1,
        int linesPerChunk = 38,
        int overlapLines = 6)
        {
            var chunks = new List<(string, int, string, int)>();
            if (string.IsNullOrWhiteSpace(fullText)) return chunks;

            var lines = fullText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var buffer = new StringBuilder();
            int currentPage = startPage;
            string currentSection = "مقدمة";
            int nonEmptyLines = 0;

            int EstimateTokens(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return 0;
                int words = 1;
                for (int i = 0; i < text.Length; i++)
                    if (text[i] == ' ' || text[i] == '\n' || text[i] == '\t') words++;
                return (int)(words * 1.35) + 10;
            }

            void Flush()
            {
                if (nonEmptyLines > 10)
                {
                    var chunkText = buffer.ToString().TrimEnd();
                    chunks.Add((chunkText, currentPage, currentSection, EstimateTokens(chunkText)));

                    // Preserve context: keep last N non-empty lines
                    var lastLines = buffer.ToString()
                                          .Split('\n')
                                          .Where(l => !string.IsNullOrWhiteSpace(l))
                                          .TakeLast(overlapLines)
                                          .ToArray();
                    buffer.Clear();
                    if (lastLines.Any())
                        buffer.Append(string.Join("\n", lastLines)).Append("\n");

                    nonEmptyLines = lastLines.Length;
                }
            }

            foreach (var lineRaw in lines)
            {
                var line = Regex.Replace(lineRaw.Trim(), @"\s+", " ");
                if (string.IsNullOrEmpty(line)) continue;

                // Skip headers/footers
                var lower = line.ToLowerInvariant();
                if (lower.Contains("وزارة التجارة") || lower.Contains("state of kuwait") ||
                    lower.Contains("دولة الكويت") || (lower.Contains("page") && line.Length < 100) ||
                    (lower.Contains("صفحة") && line.Length < 100))
                    continue;

                // Detect page number
                if (line.StartsWith("<<PAGE:") || Regex.IsMatch(line, @"صفحة\s*\d+"))
                {
                    if (nonEmptyLines > 10) Flush();
                    var match = Regex.Match(line, @"\d+");
                    if (match.Success && int.TryParse(match.Value, out int p)) currentPage = p;
                    continue; // don't add page line to buffer
                }

                // Detect section title (Arabic heuristic)
                if (Regex.IsMatch(line, @"^(?:[أ-ي]+\s*){2,18}$") && line.Length > 12 && line.Length < 140 &&
                    !line.Contains("•") && !line.Contains("-") && !line.Contains(":"))
                {
                    if (nonEmptyLines > 10) Flush();
                    currentSection = line.Trim();
                }

                buffer.AppendLine(line);
                nonEmptyLines++;

                if (nonEmptyLines >= linesPerChunk)
                    Flush();
            }

            if (nonEmptyLines > 10)
                Flush();

            return chunks;
        }



        //------------------------------------------------ 
        #endregion

        #region Last_OLD_Smart_Chunk
        private IEnumerable<(string text, int page, string section)> SmartChunk_OLD(string fullText, int startPage = 1, int linesPerChunk = 38, int overlapLines = 6)
        {
            var chunks = new List<(string text, int page, string section)>();
            if (string.IsNullOrWhiteSpace(fullText)) return chunks;

            // Preserve original OCR line breaks
            var lines = fullText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            var buffer = new StringBuilder();
            int currentPage = startPage;
            string currentSection = "مقدمة";
            int nonEmptyLines = 0;

            void Flush()
            {
                if (nonEmptyLines > 10)
                {
                    var chunkText = buffer.ToString().TrimEnd();
                    chunks.Add((chunkText, currentPage, currentSection));

                    // Overlap: keep last N non-empty lines
                    var lastLines = buffer.ToString()
                                          .Split('\n')
                                          .Where(l => !string.IsNullOrWhiteSpace(l))
                                          .TakeLast(overlapLines);
                    buffer.Clear();
                    if (lastLines.Any())
                        buffer.Append(string.Join("\n", lastLines)).Append("\n");

                    nonEmptyLines = lastLines.Count();
                }
            }

            foreach (var lineRaw in lines)
            {
                var line = Regex.Replace(lineRaw, @"[ \t]+", " "); // normalize spaces

                // Detect page
                if (line.Contains("<<PAGE:") || Regex.IsMatch(line, @"صفحة\s*\d+"))
                {
                    Flush();
                    if (int.TryParse(Regex.Match(line, @"\d+").Value, out int p))
                        currentPage = p;
                    buffer.Append(line).Append("\n");
                    continue;
                }

                // Detect section title (Arabic heuristic: centered, no bullet)
                if (line.Length > 15 && line.Length < 180 &&
                    !line.TrimStart().StartsWith("•") &&
                    !line.TrimStart().StartsWith("▪") &&
                    line.Trim().Length == line.Length) // roughly centered
                {
                    if (nonEmptyLines > 15) Flush();
                    currentSection = line.Trim();
                }

                buffer.Append(line).Append("\n");
                if (!string.IsNullOrWhiteSpace(line)) nonEmptyLines++;

                if (nonEmptyLines >= linesPerChunk)
                    Flush();
            }

            if (nonEmptyLines > 10)
                Flush();

            return chunks;
        }
        #endregion


       

        //----------------------------------
        #region Check Files Names
        private async Task<HashSet<string>> GetExistingFileNamesAsync()
        {
            var fileNames = new HashSet<string>();

            int batchSize = 1000;
            int offset = 0;
            bool hasMore = true;

            while (hasMore)
            {
                var payload = new
                {
                    limit = batchSize,
                    offset = offset,
                    filter = new { must = System.Array.Empty<object>() }
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await _qdrantClient.PostAsync(
                    $"/collections/{UNIFIED_COLLECTION}/points/scroll",
                    content
                );

                response.EnsureSuccessStatusCode();

                var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                var points = json.RootElement.GetProperty("result").GetProperty("points");
                if (points.GetArrayLength() == 0)
                    break;

                foreach (var point in points.EnumerateArray())
                {
                    if (point.TryGetProperty("payload", out var payloadProp) &&
                        payloadProp.TryGetProperty("file_name", out var fn))
                    {
                        fileNames.Add(fn.GetString() ?? string.Empty);
                    }
                }

                offset += batchSize;
                hasMore = points.GetArrayLength() == batchSize;
            }

            return fileNames;
        }
        #endregion



        //------------------------------------------------------

        #region Embedding With Python  jina For Now
        //New Embedding Service Code Here With Python
        // نفس الكود يشتغل على الـ single والـ batch
        private async Task<float[][]> EmbedTextsAsync(string[] texts, bool isQuery = false)
        {
            var cleaned = texts.Select(CleanText).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
            if (!cleaned.Any()) return System.Array.Empty<float[]>();

            var payload = new { texts = cleaned, is_query = isQuery };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("/embed", content);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Embed server error: {0}", json);
                return cleaned.Select(_ => new float[VECTOR_SIZE]).ToArray();
            }

            var result = JObject.Parse(json)["embeddings"]?.ToObject<float[][]>();
            return result ?? cleaned.Select(_ => new float[VECTOR_SIZE]).ToArray();
        }

        #region TRY_BATCH_EMBEDDING
        //new Patched One Method

        //private async Task<float[][]> EmbedTextsAsync(string[] texts, bool isQuery = false)
        //{
        //    var cleaned = texts
        //        .Select(CleanText)
        //        .Where(t => !string.IsNullOrWhiteSpace(t))
        //        .ToArray();

        //    if (cleaned.Length == 0) return Array.Empty<float[]>();

        //    const int BATCH_SIZE = 64;  // أفضل حجم لـ jina-v3 (سرعة + استقرار)
        //    var allEmbeddings = new List<float[]>(cleaned.Length);

        //    for (int i = 0; i < cleaned.Length; i += BATCH_SIZE)
        //    {
        //        var batch = cleaned.Skip(i).Take(BATCH_SIZE).ToArray();

        //        var payload = new { texts = batch, is_query = isQuery };
        //        var content = new StringContent(
        //            JsonConvert.SerializeObject(payload),
        //            Encoding.UTF8,
        //            "application/json"
        //        );

        //        HttpResponseMessage response;
        //        try
        //        {
        //            response = await _client.PostAsync("/embed", content);
        //        }
        //        catch (TaskCanceledException)
        //        {
        //            _logger.LogWarning("Timeout في batch {i}/{total} – إعادة المحاولة...", i / BATCH_SIZE + 1, (cleaned.Length + BATCH_SIZE - 1) / BATCH_SIZE);
        //            response = await _client.PostAsync("/embed", content); // retry مرة واحدة
        //        }

        //        if (!response.IsSuccessStatusCode)
        //        {
        //            var error = await response.Content.ReadAsStringAsync();
        //            _logger.LogError("فشل batch {i}: {Error}", i / BATCH_SIZE + 1, error);
        //            allEmbeddings.AddRange(Enumerable.Repeat(new float[VECTOR_SIZE], batch.Length));
        //            continue;
        //        }

        //        var json = await response.Content.ReadAsStringAsync();
        //        var batchEmbeddings = JObject.Parse(json)["embeddings"]?.ToObject<float[][]>()
        //                              ?? batch.Select(_ => new float[VECTOR_SIZE]).ToArray();

        //        allEmbeddings.AddRange(batchEmbeddings);
        //    }

        //    return allEmbeddings.ToArray();
        //} 
        #endregion


        // الدالة الأولى (single)
        #region These Two Methods For Jina EmbedText /rank endPoint

        //These Two Methods Are Updated For Jina ai Model Embeding And Reranking
        public async Task<float[]> GenerateEmbedding(string text, bool isQuery = false)
        {
            var batch = await EmbedTextsAsync(new[] { text }, isQuery);
            return batch.Length > 0 ? batch[0] : new float[VECTOR_SIZE];
        }

        // الدالة الثانية (batch)
        public async Task<float[][]> GenerateEmbeddingsBatchAsync(string[] texts, bool isQuery = false)
        {
            return await EmbedTextsAsync(texts, isQuery);
        } 
        #endregion




        #endregion



        //---------------------------
        #region Re_Rank_WITH_JINA_LOCAL_SUCESS
        //RE_RANK_WITH_JINA
        public async Task<List<UnifiedSearchResult>> RerankWithJinaV3Async_LOCAL(
    string query,
    List<UnifiedSearchResult> candidates)
        {
            if (candidates == null || !candidates.Any())
                return candidates ?? new List<UnifiedSearchResult>();

            var passages = candidates.Select(c => c.Text).ToArray();

            var payload = new
            {
                query = query,
                passages = passages,
                top_k = passages.Length  // نجيب كل النتايج عشان نرتب بنفسنا
            };

            var content = new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8,
                "application/json");

            try
            {
                var response = await _client.PostAsync("/rerank", content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Jina rerank failed ({StatusCode}), falling back to vector scores", response.StatusCode);
                    return candidates.OrderByDescending(c => c.Score).Take(5).ToList();
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);
                var resultsArray = result["results"]?.ToArray() ?? System.Array.Empty<JToken>();

                // بنعمل ماب من النص → relevance_score
                var rerankMap = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in resultsArray)
                {
                    string text = item["text"]?.ToString() ?? "";
                    // ← التعديل المهم: نستخدم relevance_score مش score
                    float score = item["relevance_score"]?.ToObject<float>() ?? 0f;
                    if (!string.IsNullOrEmpty(text))
                        rerankMap[text] = score;
                }

                // نُحدّث الـ Score في الكانديديتس
                foreach (var candidate in candidates)
                {
                    if (rerankMap.TryGetValue(candidate.Text, out float newScore))
                    {
                        candidate.Score = newScore;  // نستبدل الـ vector score بالـ rerank score
                    }
                    // لو النص مش موجود (نادر جدًا) → نسيب الـ score القديم
                }

                // نرجّع أفضل 5 فقط مرتبين حسب الـ rerank score
                return candidates
                    .OrderByDescending(c => c.Score)
                    .Take(5)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jina rerank exception – using original vector scores");
                return candidates.OrderByDescending(c => c.Score).Take(5).ToList();
            }
        }
        #endregion

        //-----------------------------


        #region RE_RANK_WITH_JINA_AI_MODEL_SUCCESS
        //RE_RANK_JINA_WITH_API
        public async Task<List<UnifiedSearchResult>> RerankWithJinaV3Async_API(
            string query,
            List<UnifiedSearchResult> candidates,
            int topK = 5)
        {
            if (candidates == null || !candidates.Any())
                return new List<UnifiedSearchResult>();

            if (candidates.Count <= topK)
                return candidates.OrderByDescending(c => c.Score).ToList();

            var documents = candidates.Select(c => c.Text).ToList();

            var payload = new
            {
                model = "jina-reranker-v3",
                query = query,
                documents = documents,
                top_n = topK * 2  // نجيب أكثر شوية عشان نضمن جودة الـ top 5
                                  // return_documents = false افتراضي → أخف وأسرع
            };

            var jsonContent = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            try
            {
                // تأكد أن الـ HttpClient فيه الـ Header ده مرة واحدة عند الإنشاء
                // _jinaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "your-jina-api-key");

                var response = await _jinaClient.PostAsync("rerank", content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Jina API failed ({StatusCode}): {Reason}",
                        response.StatusCode, response.ReasonPhrase);
                    return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<JinaRerankResponse>(responseJson);

                if (result?.Results == null || !result.Results.Any())
                {
                    _logger.LogWarning("Jina returned empty results, using vector scores");
                    return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
                }

                // بنعمل ماب من الـ index → relevance_score
                var scoreByIndex = result.Results
                    .OrderByDescending(r => r.Relevance_Score)
                    .Take(topK)
                    .ToDictionary(
                        r => r.Index,
                        r => (float)r.Relevance_Score
                    );

                // نُحدّث الـ Score في الكانديديتس الأصلية
                foreach (var candidate in candidates)
                {
                    if (scoreByIndex.TryGetValue(documents.IndexOf(candidate.Text), out float newScore))
                    {
                        candidate.Score = newScore;
                    }
                }

                // نرجّع أفضل topK مرتبة حسب درجة Jina
                return candidates
                    .Where(c => scoreByIndex.ContainsKey(documents.IndexOf(c.Text)))
                    .OrderByDescending(c => c.Score)
                    .Take(topK)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jina Reranker API exception – fallback to vector scores");
                return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
            }
        }
        #endregion


        #region RE_RANK_WITH_QWEN3_.6B _ LOCAL
        //NEW METHOD FOR QWEN3_RERANK.6B
        public async Task<List<UnifiedSearchResult>> RerankWithQwen3Async_LOCAL(
     string query,
     List<UnifiedSearchResult> candidates,
     int topK = 4)
        {
            if (candidates == null || !candidates.Any()) return new List<UnifiedSearchResult>();
            if (topK <= 0) topK = 5;

            var documents = candidates.Select(c => c.Text).ToList();

            var payload = new
            {
                query = query,
                passages = documents,
                top_k = topK
                // ما تبعثش instruction – السيرفر الجديد بيتجاهله ويستخدم الـ prefix الأصلي
            };

            var jsonContent = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            try
            {
                var response = await _localAiClient.PostAsync("rerank", content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Qwen3 rerank failed: {Status}", response.StatusCode);
                    return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<QwenRerankResponse>(json);

                if (result?.Results == null || !result.Results.Any())
                    return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();

                // الطريقة الأكثر أمانًا: نستخدم الـ index المرجع من السيرفر (الأدق والأسرع)
                foreach (var item in result.Results)
                {
                    if (item.Index >= 0 && item.Index < candidates.Count)
                    {
                        // نستخدم relevance_score أولاً، ثم score كـ fallback
                        var score = item.RelevanceScore > 0 ? item.RelevanceScore : item.Score;
                        candidates[item.Index].Score = score;
                    }
                }

                return candidates
                    .OrderByDescending(c => c.Score)
                    .Take(topK)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Qwen3 local reranker");
                return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
            }
        }

        #endregion

        // Model classes
        public class ChunkedPassage
        {
            public string ChunkText { get; set; }
            public int OriginalIndex { get; set; }
            public int ChunkNumber { get; set; }
            public int TotalChunks { get; set; }
            public bool IsComplete { get; set; }
        }

        public class ChunkingResult
        {
            public List<string> ChunkedPassages { get; set; }
            public List<ChunkedPassage> ChunkMetadata { get; set; }
        }

        // Main chunking method
        public ChunkingResult ChunkPassagesForReranker(
            List<string> passages,
            int maxCharsPerChunk = 18000,
            int overlapChars = 500)
        {
            var chunkedPassages = new List<string>();
            var chunkMetadata = new List<ChunkedPassage>();

            for (int i = 0; i < passages.Count; i++)
            {
                var passage = passages[i];

                // If passage is within limits, use it as-is
                if (passage.Length <= maxCharsPerChunk)
                {
                    chunkedPassages.Add(passage);
                    chunkMetadata.Add(new ChunkedPassage
                    {
                        ChunkText = passage,
                        OriginalIndex = i,
                        ChunkNumber = 0,
                        TotalChunks = 1,
                        IsComplete = true
                    });
                    continue;
                }

                // Passage is too long - chunk it
                var chunks = SplitTextIntoChunks(passage, maxCharsPerChunk, overlapChars);

                for (int chunkIdx = 0; chunkIdx < chunks.Count; chunkIdx++)
                {
                    chunkedPassages.Add(chunks[chunkIdx]);
                    chunkMetadata.Add(new ChunkedPassage
                    {
                        ChunkText = chunks[chunkIdx],
                        OriginalIndex = i,
                        ChunkNumber = chunkIdx,
                        TotalChunks = chunks.Count,
                        IsComplete = false
                    });
                }
            }

            return new ChunkingResult
            {
                ChunkedPassages = chunkedPassages,
                ChunkMetadata = chunkMetadata
            };
        }

        // Smart text splitting at sentence boundaries
        private List<string> SplitTextIntoChunks(
            string text,
            int maxCharsPerChunk,
            int overlapChars)
        {
            var chunks = new List<string>();

            // Sentence delimiters (English and Arabic)
            var sentenceDelimiters = new[] { '.', '!', '?', '؟', '!', '۔' };

            int startIndex = 0;

            while (startIndex < text.Length)
            {
                int endIndex = Math.Min(startIndex + maxCharsPerChunk, text.Length);

                // If this is the last chunk, take everything
                if (endIndex == text.Length)
                {
                    chunks.Add(text.Substring(startIndex));
                    break;
                }

                // Try to find a sentence boundary near the end
                int splitPoint = FindBestSplitPoint(text, startIndex, endIndex, sentenceDelimiters);

                // Extract the chunk
                string chunk = text.Substring(startIndex, splitPoint - startIndex);
                chunks.Add(chunk.Trim());

                // Move to next chunk with overlap
                startIndex = Math.Max(startIndex + 1, splitPoint - overlapChars);
            }

            return chunks;
        }

        // Find the best point to split text (at sentence boundary)
        private int FindBestSplitPoint(
            string text,
            int startIndex,
            int maxEndIndex,
            char[] sentenceDelimiters)
        {
            // Search backwards from maxEndIndex for a sentence delimiter
            for (int i = maxEndIndex - 1; i > startIndex + (maxEndIndex - startIndex) / 2; i--)
            {
                if (sentenceDelimiters.Contains(text[i]))
                {
                    // Include the delimiter and any following whitespace
                    int splitPoint = i + 1;
                    while (splitPoint < text.Length && char.IsWhiteSpace(text[splitPoint]))
                    {
                        splitPoint++;
                    }
                    return splitPoint;
                }
            }

            // No sentence boundary found, look for word boundary
            for (int i = maxEndIndex - 1; i > startIndex; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    return i + 1;
                }
            }

            // Fallback: hard cut at maxEndIndex
            return maxEndIndex;
        }

      


        #region RE_RANK_WITH_QWEN3_.6B _ SERVER
        //NEW METHOD FOR QWEN3_RERANK.6B

        public async Task<List<UnifiedSearchResult>> RerankWithQwen3Async_Server(
     string query,
     List<UnifiedSearchResult> candidates,
     int topK = 3)
        {
            if (candidates == null || !candidates.Any())
                return new List<UnifiedSearchResult>();

            if (topK <= 0 || topK > candidates.Count)
                topK = candidates.Count;

            var passages = candidates.Select(c => c.Text).ToList();



            // Payload matching Python exactly
            var payload = new
            {
                query = query,
                passages = passages,
                top_k = topK
            };

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                // Serialize with UTF-8 encoding (matching Python)
                var jsonContent = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.None
                });

                // CRITICAL FIX: Match Python's exact header format
                var content = new StringContent(
                    jsonContent,
                    Encoding.UTF8,
                    "application/json"
                );

                // Ensure the Content-Type header matches Python exactly
                //content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
                //{
                //    CharSet = "utf-8"
                //};

                // Log the request for debugging
                _logger.LogInformation("Sending rerank request: Query={Query}, Passages={Count}, TopK={TopK}",
                    query, passages.Count, topK);

                _logger.LogInformation("Request body: {Body}", jsonContent);

                var response = await client.PostAsync(QWEN_RERANKER_URL, content);

                // Read response content for better error handling
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Qwen3 Remote Reranker failed: {Status} - {Reason} - Response: {Response}",
                        response.StatusCode, response.ReasonPhrase, responseContent);

                    // Log the actual headers being sent
                    _logger.LogInformation("Request Headers: {Headers}",
                        string.Join(", ", content.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}")));

                    // Log the exact URL
                    _logger.LogInformation("Posting to URL: {Url}", QWEN_RERANKER_URL);

                    // Log response headers
                    _logger.LogInformation("Response Headers: {Headers}",
                        string.Join(", ", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}")));

                    // Fallback
                    return candidates
                        .OrderByDescending(c => c.Score)
                        .Take(topK)
                        .ToList();
                }

                _logger.LogInformation("Rerank response received: {Response}",
                    responseContent.Length > 500 ? responseContent.Substring(0, 500) + "..." : responseContent);

                var result = JsonConvert.DeserializeObject<QwenRerankResponse>(responseContent);

                if (result?.Results == null || !result.Results.Any())
                {
                    _logger.LogWarning("Qwen3 Remote Reranker returned empty results");
                    return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
                }

                // Update scores using index
                foreach (var item in result.Results)
                {
                    if (item.Index >= 0 && item.Index < candidates.Count)
                    {
                        var newScore = item.RelevanceScore > 0
                            ? item.RelevanceScore
                            : (item.Score > 0 ? item.Score : 0);

                        candidates[item.Index].Score = Math.Max(candidates[item.Index].Score, newScore);
                    }
                }

                return candidates
                    .OrderByDescending(c => c.Score)
                    .Take(topK)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Qwen3 Remote Reranker");
                return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
            }
        }
        #endregion 

        public async Task<List<UnifiedSearchResult>> RerankWithQwen3Async_Server_V2(
    string query,
    List<UnifiedSearchResult> candidates,
    int topK = 5)
        {
            if (candidates == null || !candidates.Any())
                return new List<UnifiedSearchResult>();

            if (topK <= 0 || topK > candidates.Count)
                topK = candidates.Count;

            var passages = candidates.Select(c => c.Text).ToList();

            var payload = new
            {
                query = query,
                passages = passages,
                top_k = topK
            };

            try
            {
                // Use injected HttpClient or static instance


                var jsonContent = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.None
                });

                // Simple StringContent - let it handle charset
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                _logger.LogInformation("Rerank Request - URL: {Url}, Query: {Query}, Passages: {Count}, TopK: {TopK}",
                    QWEN_RERANKER_URL, query, passages.Count, topK);

                var response = await _qwenFactory.PostAsync(QWEN_RERANKER_URL, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogInformation("Response Status: {Status}, Content Length: {Length}",
                    response.StatusCode, responseContent.Length);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Rerank Failed - Status: {Status}, Response: {Response}",
                        response.StatusCode, responseContent);

                    return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
                }

                var result = JsonConvert.DeserializeObject<QwenRerankResponse>(responseContent);

                if (result?.Results == null || !result.Results.Any())
                {
                    _logger.LogWarning("Empty rerank results");
                    return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
                }

                // Update scores
                foreach (var item in result.Results)
                {
                    if (item.Index >= 0 && item.Index < candidates.Count)
                    {
                        var newScore = item.RelevanceScore > 0 ? item.RelevanceScore : item.Score;
                        candidates[item.Index].Score = Math.Max(candidates[item.Index].Score, newScore);
                    }
                }

                return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP Request failed - Check if server is accessible at {Url}", QWEN_RERANKER_URL);
                return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Request timeout after 120 seconds");
                return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in reranker");
                return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
            }


        }


       


        //    public async Task<List<UnifiedSearchResult>> RerankWithQwen3Async_Server(
        //string query,
        //List<UnifiedSearchResult> candidates,
        //int topK = 3)
        //    {
        //        if (candidates == null || !candidates.Any())
        //            return new List<UnifiedSearchResult>();

        //        if (topK <= 0 || topK > candidates.Count)
        //            topK = candidates.Count;

        //        // Clean passages before sending
        //        var passages = candidates
        //            .Select(c => c.Text)
        //            .ToList();

        //        var payload = new
        //        {
        //            query = query,
        //            passages = passages,
        //            top_k = topK
        //        };

        //        // ... rest of your code
        //        try
        //        {
        //            // Use injected HttpClient or static instance


        //            var jsonContent = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
        //            {
        //                NullValueHandling = NullValueHandling.Ignore,
        //                Formatting = Formatting.None
        //            });

        //            // Simple StringContent - let it handle charset
        //            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        //            _logger.LogInformation("Rerank Request - URL: {Url}, Query: {Query}, Passages: {Count}, TopK: {TopK}",
        //                QWEN_RERANKER_URL, query, passages.Count, topK);

        //            var response = await _qwenFactory.PostAsync(QWEN_RERANKER_URL, content);

        //            var responseContent = await response.Content.ReadAsStringAsync();

        //            _logger.LogInformation("Response Status: {Status}, Content Length: {Length}",
        //                response.StatusCode, responseContent.Length);

        //            if (!response.IsSuccessStatusCode)
        //            {
        //                _logger.LogError("Rerank Failed - Status: {Status}, Response: {Response}",
        //                    response.StatusCode, responseContent);

        //                return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
        //            }

        //            var result = JsonConvert.DeserializeObject<QwenRerankResponse>(responseContent);

        //            if (result?.Results == null || !result.Results.Any())
        //            {
        //                _logger.LogWarning("Empty rerank results");
        //                return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
        //            }

        //            // Update scores
        //            foreach (var item in result.Results)
        //            {
        //                if (item.Index >= 0 && item.Index < candidates.Count)
        //                {
        //                    var newScore = item.RelevanceScore > 0 ? item.RelevanceScore : item.Score;
        //                    candidates[item.Index].Score = Math.Max(candidates[item.Index].Score, newScore);
        //                }
        //            }

        //            return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
        //        }
        //        catch (HttpRequestException ex)
        //        {
        //            _logger.LogError(ex, "HTTP Request failed - Check if server is accessible at {Url}", QWEN_RERANKER_URL);
        //            return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
        //        }
        //        catch (TaskCanceledException ex)
        //        {
        //            _logger.LogError(ex, "Request timeout after 120 seconds");
        //            return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Unexpected error in reranker");
        //            return candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
        //        }
        //    }

        private string CleanTextForReranker(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Remove null characters and control characters (except newlines/tabs)
            text = new string(text.Where(c =>
                c == '\n' || c == '\r' || c == '\t' || !char.IsControl(c)
            ).ToArray());

            // Normalize whitespace
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            // Remove Bengali/Indic digits and replace with standard digits
            text = text.Replace("এ", "ك").Replace("१", "1").Replace("২", "2");

            // Trim excessive whitespace
            text = text.Trim();

            // Limit length to prevent payload size issues (adjust as needed)
            if (text.Length > 5000)
            {
                text = text.Substring(0, 5000);
                _logger.LogWarning("Truncated passage to 5000 characters");
            }

            return text;
        }

    }

    #region Classes For Qwen3-rerank
    public class QwenRerankResponse
    {
        [JsonProperty("query")]
        public string Query { get; set; }

        [JsonProperty("results")]
        public List<QwenRerankItem> Results { get; set; }
    }

    public class QwenRerankItem
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("relevance_score")]
        public float RelevanceScore { get; set; }

        // للتوافق مع الإصدارات القديمة لو لسة موجودة
        [JsonProperty("score")]
        public float Score { get; set; }
    }

    #endregion


    #region Classes For Jina Model
    public class JinaRerankResponse
    {
        public string Model { get; set; }
        public JinaUsage Usage { get; set; }
        public List<JinaRerankResult> Results { get; set; }
    }

    public class JinaUsage
    {
        public int Total_Tokens { get; set; }
    }

    public class JinaRerankResult
    {
        public int Index { get; set; }
        public double Relevance_Score { get; set; }
        // ملاحظة: الـ API ما يرجع النص، فقط الـ index والـ score
    } 
    #endregion

}