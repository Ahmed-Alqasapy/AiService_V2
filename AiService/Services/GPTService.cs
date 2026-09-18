using AiService.Models;
using DocumentFormat.OpenXml.Office.SpreadSheetML.Y2023.MsForms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static AiService.Services.EmbeddingService;

namespace AiService.Services
{
    public class GPTService
    {
        private readonly OpenAIService _aiProvider;
        private readonly MyDbContext _db;
        private readonly ILogger<GPTService> _logger;
        private readonly IMemoryCache _cache;
        private readonly EmbeddingService _embeddingService;
        private readonly HttpClient _ollama;
        private readonly ContextOptimizer _compressor = new ContextOptimizer();
        private readonly ChatTrace _trace;


        public record ContextPart(string Content, string SourceType, UnifiedSearchResult? Result = null);
        private static readonly string[] NoInfoPhrases =
            { "لا توجد معلومات", "لم يتم العثور", "لا تتوفر معلومات", "غير متوفر في الوثائق", "I don't have information" };

        public GPTService(

            OpenAIService aiProvider,
            MyDbContext db,
            ILogger<GPTService> logger,
            IMemoryCache cache,
            EmbeddingService embeddingService, IHttpClientFactory httpClientFactory, ChatTrace trace,
            IConfiguration configuration)
        {
            _aiProvider = aiProvider;
            _db = db;
            _logger = logger;
            _cache = cache;
            _embeddingService = embeddingService;
            _ollama = httpClientFactory.CreateClient();
            _ollama.BaseAddress = new Uri("http://10.21.2.143:11434/api/");
            _trace = trace;
            _lowConfidenceThreshold = configuration.GetValue<float?>("Admin:LowConfidenceThreshold") ?? 0.30f;
        }
        private readonly float _lowConfidenceThreshold;


        #region For ChatHUB_SIGNALR
        //public async IAsyncEnumerable<string> StreamResponseAsync(string userMessage)
        //{
        //    if (string.IsNullOrWhiteSpace(userMessage))
        //    {
        //        yield return "Please provide a valid question.";
        //        yield break;
        //    }


        //    var (intent, parameters) = await ParseQuery(userMessage);

        //    if (intent == "greeting")
        //    {
        //        yield return "أهلاً بك! كيف يمكنني مساعدتك اليوم؟";
        //        yield break;
        //    }

        //    string context = await RetrieveContext(userMessage, intent, parameters);

        //    string systemMessage = """
        //    You are a helpful assistant with access to detailed company, license, 
        //    and ownership data from a questionnaire database. Use the provided context 
        //    to answer the user's question accurately in Arabic or English.
        //    If the context is insufficient, say so. Respond concisely and professionally.

        //    Context:
        //    """+"\n"+ context;

        //    var payload = new
        //    {
        //        model = "llama3.1:8b-instruct-q8_0",
        //        prompt = $"{systemMessage}\n\nUser: {userMessage}\nAssistant:",
        //        stream = true,
        //        options = new
        //        {
        //            temperature = 0.3,
        //            top_p = .9,
        //            num_ctx = 8192,
        //            num_predict = 300,
        //            repeat_penalty = 1.1
        //        }
        //    };

        //    var request = new HttpRequestMessage(HttpMethod.Post, "generate")
        //    {
        //        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        //    };

        //    var response = await _ollama.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        //    response.EnsureSuccessStatusCode();

        //    using var stream = await response.Content.ReadAsStreamAsync();
        //    using var reader = new StreamReader(stream);

        //    string line;
        //    while ((line = await reader.ReadLineAsync()) != null)
        //    {
        //        if (string.IsNullOrWhiteSpace(line)) continue;
        //        var json = JsonDocument.Parse(line).RootElement;
        //        if (json.TryGetProperty("response", out var token))
        //            yield return token.GetString()!;
        //        if (json.TryGetProperty("done", out var done) && done.GetBoolean())
        //            break;
        //    }
        //}

        #endregion



        //------------------------
        private string PostProcessResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return "لم يتم العثور على إجابة.";

            // Remove any remaining citation artifacts
            response = System.Text.RegularExpressions.Regex.Replace(
                response,
                @"\(\[ملف:[^\)]+\]\)",
                ""
            );

            // Clean up excessive newlines
            response = System.Text.RegularExpressions.Regex.Replace(
                response,
                @"\n{3,}",
                "\n\n"
            );

            // Trim
            response = response.Trim();

            return response;
        }


        //-------------------------

        public async Task<string> ProcessMessage(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return "الرجاء إدخال سؤال صحيح.";

            try
            {
                _logger.LogInformation("Processing: {Message}", userMessage);

                var (intent, parameters) = await ParseQuery(userMessage);
                _trace.Intent = intent;

                if (intent == "greeting")
                {
                    _trace.Status = "Greeting";
                    return "مرحبًا بكم! يمكنكم الاستفسار عن التراخيص، الشركات، المُلّاك، أو الالتزام بالأنظمة.";
                }

                var swSearch = System.Diagnostics.Stopwatch.StartNew();
                var contextParts = await RetrieveUnifiedContextAsync(userMessage, intent, parameters);
                swSearch.Stop();
                _trace.SearchMs = swSearch.ElapsedMilliseconds;

                // Separate contexts by type
                var fileContexts = contextParts.Where(p => p.SourceType == "file").ToList();
                var dbContexts = contextParts.Where(p => p.SourceType == "db").ToList();

                _trace.FileHits = fileContexts.Count;
                _trace.DbHits = dbContexts.Count;
                _trace.TopScore = contextParts.Where(p => p.Result != null)
                    .Select(p => (float?)p.Result!.Score)
                    .Max();
                foreach (var part in contextParts.Where(p => p.Result != null).Take(10))
                    _trace.Sources.Add(new { part.SourceType, part.Result!.FileName, part.Result.Page, part.Result.EntityId, part.Result.Score });

                // RetrieveUnifiedContextAsync always returns at least one "none" part when
                // nothing relevant was found, so `!contextParts.Any()` never fires. Detect
                // "no context" by the real file/db hit counts instead.
                if (fileContexts.Count + dbContexts.Count == 0)
                {
                    _trace.Status = "NoContext";
                    return "لا توجد معلومات كافية للإجابة على هذا السؤال.";
                }
                if (_trace.TopScore.HasValue && _trace.TopScore.Value < _lowConfidenceThreshold)
                {
                    _trace.Status = "LowConfidence";
                }


                #region Determine Priority of DB Or Files
                // Determine priority
                //bool hasFile = fileContexts.Any();
                //var dominantSource = hasFile ? "file" : (dbContexts.Any() ? "db" : "none");
                #endregion


                //-----------------------------------------

                var dominantSource = (dbContexts.Count > fileContexts.Count) ? "db" : "file";


                // Build unified context
                var fullContextBuilder = new StringBuilder();

                if (fileContexts.Any())
                {
                    fullContextBuilder.AppendLine("=== وثائق رسمية ===\n");
                    foreach (var part in fileContexts)
                    {
                        // Clean up the content
                        var cleanContent = part.Content
                            .Replace("→ ", "")  // Remove arrow markers
                            .Trim();
                        fullContextBuilder.AppendLine(cleanContent);
                        fullContextBuilder.AppendLine(); // Add spacing
                    }
                }

                if (dbContexts.Any())
                {
                    fullContextBuilder.AppendLine("\n=== بيانات السجل التجاري ===\n");
                    foreach (var part in dbContexts)
                    {
                        fullContextBuilder.AppendLine(part.Content.Trim());
                        fullContextBuilder.AppendLine();
                    }
                }

                var fullContext = fullContextBuilder.ToString();


                

                // Log for debugging
                _logger.LogDebug("Context length: {Length} chars", fullContext.Length);
                _logger.LogDebug("Dominant source: {Source}", dominantSource);

                // Build prompt based on source type
                var (finalPrompt, llmOptions) = dominantSource switch
                {
                    "file" => BuildFileCentricPrompt(userMessage, fullContext, fileContexts),
                    "db" => BuildDbCentricPrompt(userMessage, fullContext, dbContexts),
                    _ => (
                        $"السؤال: {userMessage}\n\nالمعلومات:\n{fullContext}\n\nالإجابة:",
                        new { temperature = 0.3, max_tokens = 2000 }
                    )
                };


                // Generate response
                //   string response = await _aiProvider.GenerateResponseAsync(finalPrompt, llmOptions);


                string response = "";
                if(dominantSource=="db" || dominantSource=="file")
                {

                    //Another Time We will try GenrateAnswer. Method   and let it all for the model Gpt-4o-mini
                     //response = await _aiProvider.GenerateResponseAsync(finalPrompt,llmOptions);
                     response = await _aiProvider.GenerateAnswer(finalPrompt);
                }


                //else if(dominantSource=="file")
                //{
                //     response = await _aiProvider.GenerateAnswer(finalPrompt);

                //}
                //try openAI Key

                // Post-process response

                response = PostProcessResponse(response);

                if (_trace.Status == "Answered" && NoInfoPhrases.Any(p => response.Contains(p)))
                    _trace.Status = "ModelNoInfo";

                return response;


            }
            catch (Exception ex)
            {
                _trace.Status = "Error";
                _trace.Error = ex.Message;
                _logger.LogError(ex, "Error in ProcessMessage");
                return "عذراً، حدث خطأ أثناء معالجة السؤال. الرجاء المحاولة مرة أخرى.";
            }
        }
        public async Task<string> ProcessMessage_OLD(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return "Please provide a valid question.";

            //string cacheKey = $"gpt_{userMessage.ToLowerInvariant().GetHashCode()}";
            //if (_cache.TryGetValue(cacheKey, out string cached))
            //    return cached;

            try
            {
                _logger.LogInformation("Processing: {Message}", userMessage);

                var (intent, parameters) = await ParseQuery(userMessage);



                if (intent == "greeting")
                {
                    //string msg = "Hello! Ask about licenses, companies, owners, or compliance.";
                    string msg = "مرحبًا بكم! يمكنكم الاستفسار عن التراخيص، الشركات، المُلّاك، أو الالتزام بالأنظمة.";
                    //_cache.Set(cacheKey, msg, TimeSpan.FromMinutes(30));
                    return msg;
                }

                var contextParts = await RetrieveUnifiedContextAsync(userMessage, intent, parameters);

                if (!contextParts.Any())
                {
                    return "لا توجد معلومات كافية للإجابة.";
                }

                // فصل الـ contexts حسب النوع
                var fileContexts = contextParts.Where(p => p.SourceType == "file").ToList();
                var dbContexts = contextParts.Where(p => p.SourceType == "db").ToList();


                // تحديد الأولوية: إذا في file → نعطي أولوية للوثائق (أدق)
                // وإلا نستخدم db
                bool hasFile = fileContexts.Any();
                var dominantSource = hasFile ? "file" : (dbContexts.Any() ? "db" : "none");

                // بناء الـ Context المجمّع(مع الفواصل الواضحة)
                var fullContextBuilder = new StringBuilder();


                //fullContextBuilder.AppendLine("=== المعلومات المتاحة من مصادر رسمية ===");

                if (fileContexts.Any())
                {
                    fullContextBuilder.AppendLine("\n--- [وثائق رسمية مرفقة] ---");
                    foreach (var part in fileContexts)
                        fullContextBuilder.AppendLine(part.Content.Trim());
                }

                if (dbContexts.Any())
                {
                    fullContextBuilder.AppendLine("\n--- [بيانات رسمية من قاعدة السجل التجاري] ---");
                    foreach (var part in dbContexts)
                        fullContextBuilder.AppendLine(part.Content.Trim());
                }

                var fullContext = fullContextBuilder.ToString();


                // بناء الـ Prompt والـ Options حسب النوع المهيمن
                var (finalPrompt, llmOptions) = dominantSource switch
                {
                    "file" => BuildFileCentricPrompt(userMessage, fullContext, fileContexts),
                    "db" => BuildDbCentricPrompt(userMessage, fullContext, dbContexts),
                    _ => (
                        $"السؤال: {userMessage}\n\nالمعلومات المتاحة:\n{fullContext}\n\nأجب بأفضل شكل ممكن.",
                        new { temperature = 0.3, max_tokens = 1024 }
                    )
                };

                #region SYSTEM_MESSAGE_OLD
                //     string systemMessage = """
                // You are a helpful assistant with access to detailed company, license, 
                // and ownership data from a questionnaire database. Use the provided context 
                // to answer the user's question accurately in Arabic or English.
                // If the context is insufficient, say so. Respond concisely and professionally.

                // Context:
                //""" + "\n" + context;
                //string systemMessage = $"""
                //    أنت مساعد ذكي في إدارة الشؤون القانونية - وزارة التجارة والصناعة - دولة الكويت.
                //    لديك مصدران للمعلومات:

                //    1. [بيانات منظمة] ← من قاعدة البيانات الرسمية (Questionnaire)
                //       → هذه معلومات موثوقة 100%، استخدمها للإجابات الرسمية (أسماء الملاك، أرقام التراخيص، النسب، التواريخ...).

                //    2. [ملفات مرفوعة] ← عقود، قرارات، مراسلات (PDF)
                //       → إذا طُلب نص من وثيقة (مثل: "ماذا كتب في العقد؟" أو "ما هو البند 5؟")
                //         → انسخ النص حرفيًا كما هو بدون أي تغيير أو إعادة صياغة.
                //         → احتفظ بالمسافات، الرموز • ▪ ◆، الأرقام، التنسيق.
                //         → إذا لم يكن موجودًا بالضبط → قل: "غير موجود في الوثائق المرفوعة".

                //    قواعد عامة صارمة:
                //    - إذا وجدت الإجابة في [بيانات منظمة] → استخدمها فورًا (لأنها أدق).
                //    - إذا كان السؤال عن نص وثيقة → استخدم فقط [ملفات مرفوعة] وانسخ حرفيًا.
                //    - إذا كان السؤال يحتاج دمج (مثل: نسبة الملكية في العقد vs في النظام) → اذكر كلا المصدرين بوضوح.
                //    - إذا كان هناك تعارض → قل: "توجد اختلافات بين النظام والوثيقة المرفوعة" ثم اذكر الاثنين.
                //    - إذا لم تكن متأكد → قل: "لا توجد معلومات كافية".
                //    - الرد دائمًا بالعربية ما عدا إذا طلب المستخدم الإنجليزية.
                //    - كن موجزًا واحترافيًا.

                //    المعلومات المتاحة:
                //        {context}

                //    الآن أجب على السؤال بدقة تامة:
                //    """;

                #endregion



                string response = await _aiProvider.GenerateResponseAsync(finalPrompt, llmOptions);
                //string response = await _aiProvider.GenerateAnswer(userMessage,fullContext);
                //_cache.Set(cacheKey, response, TimeSpan.FromMinutes(10));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GPT error");
                return "Sorry, an error occurred.";
            }
        }

        private (string Prompt, object Options) BuildDbCentricPrompt_OLD(string query, string fullContext, List<ContextPart> dbParts)
        {
                                var prompt = $"""
                    <|begin_of_text|><|start_header_id|>system<|end_header_id|>

                    أنت **موظف رسمي في إدارة السجل التجاري والتراخيص – وزارة التجارة والصناعة – دولة الكويت**.

                    لديك صلاحية الوصول المباشر إلى قاعدة البيانات الرسمية المحدثة يوميًا والتي تحتوي على:
                    • بيانات الشركات والمؤسسات
                    • التراخيص التجارية وصلاحيتها
                    • ملكية الشركاء والمساهمين
                    • بيانات الاستبيانات الرسمية (Questionnaire)

                    قواعد إجابة صارمة يجب اتباعها دون استثناء:
                    1. أجب فقط بناءً على البيانات المرفقة أدناه – ممنوع الافتراض أو التقريب.
                    2. إذا كانت المعلومة موجودة → اذكرها بدقة تامة مع التواريخ والأرقام كما هي.
                    3. استخدم جداول Markdown عند عرض أكثر من عنصر (مثل الشركاء، التراخيص، النسب).
                    4. كن موجزًا واحترافيًا، لا تُطوّل إلا إذا طلب المستخدم التفصيل.
                    5. الرد دائمًا باللغة العربية الفصحى ما عدا إذا طلب المستخدم الإنجليزية صراحة.

                    === بيانات قاعدة السجل التجاري الرسمية ===

                    {fullContext}

                    === نهاية البيانات ===

                    الآن أجب على السؤال بدقة تامة:

                    <|eot_id|><|start_header_id|>user<|end_header_id|>
                    {query}
                    <|eot_id|><|start_header_id|>assistant<|end_header_id|>
                    """;

            var options = new
            {
                temperature = 0.3,
                            num_ctx = 8192,
                            num_predict = 300,
                            repeat_penalty = 1.1
            };

            return (prompt, options);
        }
        private (string Prompt, object Options) BuildDbCentricPrompt(string query, string fullContext, List<ContextPart> dbParts)
        {
                                var prompt = $"""
                    You are a helpful assistant with access to detailed company, license, 
                     and ownership data from a questionnaire database. Use the provided context 
                     to answer the user's question accurately in Arabic only.
                     If the context is insufficient, say so. Respond concisely and professionally.

                    === بيانات قاعدة السجل التجاري الرسمية ===

                    {fullContext}

                    === نهاية البيانات ===

                    الآن أجب على السؤال بدقة تامة:

                    {query}
                    """;

            var options = new
            {
                temperature = 0.2,
                            num_ctx = 8192,
                            num_predict = 300,
                            repeat_penalty = 1.1
            };

            return (prompt, options);
        }

        //--------------------------------------------------------------


        // Helper method to detect Arabic text
        private bool ContainsArabic(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Check if text contains Arabic Unicode characters (U+0600 to U+06FF)
            return text.Any(c => c >= 0x0600 && c <= 0x06FF);
        }

        //--------------------------------------------------------------

        private string CleanContextForPrompt(string fullContext)
        {
            var lines = fullContext.Split('\n');
            var cleanedLines = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Skip empty metadata lines
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    cleanedLines.Add(""); // Keep structure
                    continue;
                }

                // Keep document headers but simplify them
                if (trimmed.StartsWith("--- [من وثيقة رسمية"))
                {
                    // Extract just filename and page
                    var match = System.Text.RegularExpressions.Regex.Match(
                        trimmed,
                        @"الملف: ([^\s]+)\s.*الصفحة: (\d+)"
                    );
                    if (match.Success)
                    {
                        cleanedLines.Add($"[المصدر: {match.Groups[1].Value} - صفحة {match.Groups[2].Value}]");
                        continue;
                    }
                }

                // Remove citation metadata like ([ملف: ... | صفحة ... | القسم: ... | التاريخ: ...])
                if (trimmed.StartsWith("([ملف:") && trimmed.EndsWith(")"))
                {
                    continue; // Skip this line entirely
                }

                // Keep everything else
                cleanedLines.Add(line);
            }

            return string.Join("\n", cleanedLines);
        }


        //-----------------------------
        // ============================================================================

        private (string Prompt, object Options) BuildFileCentricPrompt(
     string query,
     string fullContext,
     List<ContextPart> fileParts)
        {
            var cleanedContext = CleanContextForPrompt(fullContext);



            // Instead, build a clean user message

            #region ًWorking Prompot Original
            //var prompt = $$"""
            //أنت موظف قانوني متخصص في وزارة التجارة والصناعة - دولة الكويت.
            //مهمتك: الإجابة على أسئلة المستخدمين بناءً على الوثائق الرسمية المرفقة فقط.

            //## قواعد الإجابة:
            //1. استخدم فقط المعلومات الموجودة في الوثائق المرفقة
            //2. انسخ النصوص المهمة حرفياً من الوثائق (مع الحفاظ على التنسيق: •، ▪، الترقيم، المسافات)
            //3. إذا كانت الإجابة منتشرة في عدة أقسام، اجمعها بترتيب منطقي
            //4. احتفظ بالعناوين والأقسام كما هي (مثل: #، ##، -)
            //5. إذا لم تجد إجابة واضحة، قل: "المعلومات غير متوفرة في الوثائق المرفقة"
            //6. لا تضيف معلومات من خارج الوثائق

            //الوثائق:
            //{{cleanedContext}}

            //السؤال: {{query}}

            //إذا وجدت الإجابة في أكثر من مكان، اختر الأكثر اكتمالاً فقط مع الحفاظ على التنسيق.
            //الإجابة:
            //"""; 
            #endregion



            #region Second Modified Prompot

            //var prompt = $$"""
            //أنت مساعد قانوني متخصص في قوانين وإجراءات وزارة التجارة والصناعة بدولة الكويت.
            //مهمتك الوحيدة: الإجابة على سؤال المستخدم بدقة تامة باستخدام الوثائق المرفقة فقط.

            //### قواعد إجابة صارمة جدًا (اتبعها حرفيًا دون أي استثناء):
            //1. استخدم المعلومات الموجودة في الوثائق فقط. لا تختلق أي شيء أبدًا.
            //2. أجب بنص عادي تمامًا (plain text) بدون أي markdown أو تنسيق إضافي (لا #، لا ##، لا **، لا |، لا ---، لا جداول markdown).
            //3. لا تستخدم نقاط • أو ▪ أو - إلا إذا كانت موجودة أصلًا في النص الذي تنسخه حرفيًا من الوثيقة.
            //4. لا تنشئ جداول أو رموز markdown أبدًا، مهما كان السبب.
            //5. إذا وجدت الإجابة داخل جدول في الوثيقة، استخرج فقط القيمة أو النص المتعلق بالسؤال مباشرة دون ذكر اسم العمود:
            //   - إذا سُئل عن "نوع الخدمة" → أجب فقط بالقيمة (مثل: إلكترونية).
            //   - إذا سُئل عن "رسوم" أو "تكلفة" → أجب فقط بالمبلغ (مثل: 20 د.ك).
            //   - إذا سُئل عن "اسم الخدمة" → أجب فقط باسم الخدمة.
            //   - لا تكتب أبدًا "نوع الخدمة:" أو "الرسوم:" أو أي تسمية عمود إلا إذا طلب المستخدم ذلك صراحة.
            //6. إذا كانت الإجابة نصًا عاديًا أو نقاطًا في الوثيقة، انسخ الجزء المتعلق فقط حرفيًا مع الحفاظ على تنسيقه الأصلي.
            //7. أجب باختصار شديد جدًا: مباشرة بالإجابة المطلوبة فقط، بدون أي مقدمات أو تفسير أو خاتمة.
            //8. إذا لم تجد إجابة واضحة ومباشرة للسؤال المطروح، أجب بجملة واحدة فقط:
            //   "المعلومات غير متوفرة في الوثائق المرفقة."

            //الوثائق:
            //{{cleanedContext}}

            //السؤال: {{query}}

            //إذا وجدت الإجابة في أكثر من مكان، اختر الأكثر اكتمالاً فقط مع الحفاظ على التنسيق.

            //الإجابة:
            //""";
            #endregion

            #region Last Used Prompot Sucessfully
            var prompt = $$"""
            أنت مساعد قانوني متخصص في قوانين وإجراءات وزارة التجارة والصناعة بدولة الكويت.
            مهمتك الوحيدة: الإجابة على سؤال المستخدم بدقة باستخدام الوثائق المرفقة فقط.

            قواعد صارمة:
            1. استخدم معلومات الوثائق فقط، لا تختلق شيئًا.
            2. أجب بنص عادي تمامًا بدون أي markdown أو تنسيق (لا #، لا **، لا جداول، لا |).
            3. لا تستخدم نقاط مرقمة أو • أو - إلا إذا كانت موجودة أصلًا في النص المقتبس حرفيًا.
            4. إذا كانت الإجابة في جدول، استخرج القيمة فقط دون ذكر اسم العمود (مثل: إلكترونية، 20 د.ك).
            5. لا تكتب تسميات مثل "نوع الخدمة:" أو "الرسوم:" إلا إذا طلب المستخدم صراحة.
            6. انسخ النصوص أو النقاط من الوثيقة حرفيًا مع الحفاظ على تنسيقها الأصلي.
            7. أجب باختصار شديد: الإجابة المباشرة فقط، بدون مقدمات أو خاتمة.
            8. إذا لم تجد إجابة واضحة، أجب فقط: "المعلومات غير متوفرة في الوثائق المرفقة."

            الوثائق:
            {{cleanedContext}}

            السؤال: {{query}}

            الإجابة:
            """;
            #endregion



            //var prompt = $$"""
            //مساعد قانوني - وزارة التجارة والصناعة الكويت.

            //قواعد:
            //1. استخدم الوثائق فقط
            //2. نص عادي بلا markdown (لا #، **، |، جداول)
            //3. لا نقاط إلا من النص الأصلي
            //4. من جدول: اذكر القيمة فقط (مثل: إلكترونية، 20 د.ك)
            //5. لا تسميات عمود إلا بطلب صريح
            //6. انسخ حرفياً مع التنسيق الأصلي
            //7. إجابة مباشرة مختصرة فقط
            //8. لا إجابة؟ "المعلومات غير متوفرة"

            //الوثائق:
            //{{cleanedContext}}

            //السؤال: {{query}}

            //الإجابة:
            //""";




            //new Prompot_Optimizer//
            prompt = PromptOptimizer.CleanMetadata(prompt);
            prompt = PromptOptimizer.NormalizeWhitespace(prompt);




            var options = new
            {
                temperature = 0.1,        // Slight randomness = faster sampling than 0.0
                top_p = 0.95,             // Nucleus sampling = faster + more natural
                top_k = 40,               // Default, good balance
                              // Light penalty to avoid repetition
               // Early stop triggers
                //num_thread = 8,           // Adjust based on your CPU cores
                //num_gpu = 1               // If you have GPU layers loaded
            };

            return (prompt, options);
        }
        //================

        private (string Prompt, object Options) BuildFileCentricPrompt_OLD_LATEST(
    string query,
    string fullContext,
    List<ContextPart> fileParts)
        {
            // Clean the context - remove excessive metadata
            var cleanedContext = CleanContextForPrompt(fullContext);

            var prompt = $"""
أنت موظف قانوني متخصص في وزارة التجارة والصناعة - دولة الكويت.
مهمتك: الإجابة على أسئلة المستخدمين بناءً على الوثائق الرسمية المرفقة فقط.

## قواعد الإجابة:
1. استخدم فقط المعلومات الموجودة في الوثائق المرفقة
2. انسخ النصوص المهمة حرفياً من الوثائق (مع الحفاظ على التنسيق: •، ▪، الترقيم، المسافات)
3. إذا كانت الإجابة منتشرة في عدة أقسام، اجمعها بترتيب منطقي
4. احتفظ بالعناوين والأقسام كما هي (مثل: #، ##، -)
5. إذا لم تجد إجابة واضحة، قل: "المعلومات غير متوفرة في الوثائق المرفقة"
6. لا تضيف معلومات من خارج الوثائق

## الوثائق الرسمية:
{cleanedContext}

## السؤال:
{query}

## الإجابة:
""";

            var options = new
            {
                temperature = 0.1,        // قليل من المرونة للتنسيق
                top_p = 0.9,
                top_k = 40,
                num_ctx = 8192,
                num_predict = 3000,       // زيادة الحد الأقصى
                repeat_penalty = 1.05,    // قليل جداً
                presence_penalty = 0.0,
                frequency_penalty = 0.0,
                stop = new string[] { "## السؤال التالي:", "---END---" }  // stop sequences واضحة
            };

            return (prompt, options);
        }

        private (string Prompt, object Options) BuildFileCentricPrompt_OLD(string query, string fullContext, List<ContextPart> fileParts)
        {
       
            var prompt = $$"""
            أنت موظف قانوني رسمي في إدارة الشؤون القانونية بوزارة التجارة والصناعة - دولة الكويت.
            مهمتك الوحيدة هي الرد بالنص الحرفي من الوثائق المرفقة فقط، بدون أي إضافة أو حذف أو إعادة صياغة أو تفسير.
            قواعد صارمة:
            1. انسخ النص كما هو تماماً مع كل المسافات والسطور الفارغة والنقاط والعناوين والترقيم.
            2. لا تجمع بين المعلومات أبداً.
            3. لا تنقل أي بند من قائمة إلى أخرى.
            4. احترم ترتيب الصفحات والأقسام كما هي في الوثيقة.
            5. إذا لم يكن الجواب موجوداً حرفياً، أختر اكثر اجابة مناسبة للسياق".
            6. لا تضيف أي كلمة غير موجودة في النص.
            7. احتفظ بكل الرموز: • ▪ ◄ ► ◆ ● وكل المسافات البادئة.
            8. ممنوع استخدام أي معرفة خارج الوثائق.
            9. يجب أن تبدأ كل إجابة بالنص الحرفي تماماً.

            الوثائق:
            {{fullContext}}

            السؤال: {{query}}

            إذا وجدت الإجابة في أكثر من مكان، اختر الأكثر اكتمالاً فقط مع الحفاظ على التنسيق.
            الإجابة:
            """;

            
            var options = new
            {
                temperature = 0.0,
                top_p = 1.0,
                top_k = 1,
                num_ctx = 8192,
                num_predict = 2000,
                repeat_penalty = 1.0,     // غيّر من 1.1 → 1.0 (القانوني بيحتاج تكرار)
                presence_penalty = 0.0,
                frequency_penalty = 0.0,
                stop = new string[] { }   // خلّيه فاضي → عشان ما يقطعش
            };
            return (prompt, options);
        }


        #region Old_Retrieve_Context
        //private async Task<string> RetrieveContext(string query, string intent, Dictionary<string, string> parameters)
        //{

        //    if (intent == "greeting") return string.Empty;


        //    var sb = new StringBuilder();
        //    var seen = new HashSet<string>();



        //    try
        //    {
        //        var results = await _embeddingService.SearchAsync(query, topK: 30);

        //        var recIds = results
        //       .Select(r =>
        //       {
        //           if (r.Payload.TryGetValue("QuestionnaireDataRecid", out var v) && v is JsonElement element)
        //           {
        //               return element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var dec) ? (decimal?)dec : null;
        //           }
        //           return null;
        //       })
        //       .Where(x => x.HasValue)
        //       .Select(x => x.Value)
        //       .ToList();


        //        if (recIds.Count == 0) return "No relevant data found.";

        //        // Allow multiple owners per QuestionnaireDataRecid
        //        var owners = await _db.QuestionnaireLicOwners
        //            .Where(o => o.QuestionnaireDataRecid.HasValue && recIds.Contains(o.QuestionnaireDataRecid.Value))
        //            .ToLookupAsync(o => o.QuestionnaireDataRecid!.Value);

        //        // Allow multiple licenses per QuestionnaireDataRecid
        //        var licns = await _db.QuestionnaireAllLicns
        //            .Where(l => l.QuestionnaireDataRecid.HasValue && recIds.Contains(l.QuestionnaireDataRecid.Value))
        //            .ToLookupAsync(l => l.QuestionnaireDataRecid!.Value);

        //        foreach (var r in results)
        //        {
        //            //var key = $"{r.EntityType}_{r.EntityId}";
        //            //if (seen.Contains(key)) continue;
        //            //seen.Add(key);

        //            var p = r.Payload;
        //            var qdrid = p.TryGetValue("QuestionnaireDataRecid", out var idObj) &&
        //                        idObj is JsonElement idEl &&
        //                        idEl.TryGetDecimal(out var idDec)
        //                ? (decimal?)idDec : null;

        //            #region Old Code
        //            //string? entityType = p.ContainsKey("EntityType") ? p["EntityType"].ToString() : null;
        //            //p.TryGetValue("LicnNo", out var licnObj);
        //            //string? licnNo = licnObj?.ToString() ?? string.Empty;
        //            //if (licnNo == string.Empty)
        //            //{
        //            //    licnNo = p.ContainsKey("LicnCommNo") ? (p["LicnCommNo"] as string) : "";
        //            //}

        //            //p.TryGetValue("CompName", out var compObj);
        //            //string? compName = compObj?.ToString() ?? string.Empty;
        //            //if (compName == string.Empty)
        //            //{
        //            //    compName = p.ContainsKey("CompanyName") ? (p["CompanyName"] as string) : "";
        //            //}

        //            //decimal? licnId = p.ContainsKey("LicnId") && decimal.TryParse(p["LicnId"].ToString(), out var lid) ? lid : (decimal?)null
        //            //    ;
        //            ////decimal? reqOwnId = p.ContainsKey("ReqOwnId") && decimal.TryParse(p["ReqOwnId"].ToString(), out var roid) ? roid : (decimal?)null;
        //            //decimal? reqOwnId = null;
        //            //if (p != null && p.TryGetValue("ReqOwnId", out var reqValue) && reqValue != null)
        //            //{
        //            //    if (decimal.TryParse(reqValue.ToString(), out var roid))
        //            //        reqOwnId = roid;
        //            //}

        //            //p.TryGetValue("LicCivilId", out var licnCivilId);
        //            //string? LicCivilId = licnCivilId?.ToString() ?? string.Empty; 
        //            #endregion



        //            //--------------------------
        //            string line = r.EntityType.ToLower() switch
        //            {
        //                "questionnairedatum" =>

        //                    $"Company: {p.Get("CompName")}, License: {p.Get("LicnNo")}, Status: {p.Get("LicnStatus")}, " +
        //                    $"Trained: {p.Get("EmployeeCount")}, Expires: {p.Get("ExpieryDate")}, " +
        //                    $"Owners: {string.Join(", ", owners[qdrid ?? 0].Select(o => o.OwnerName))} , License Civil Id: {string.Join(", ", licns[qdrid ?? 0].Select(l => l.LicCivilId))}",

        //                "questionnairealllicn" =>
        //                    $"License: {p.Get("LicnCommNo")}, Type: {p.Get("LicnTypeDesc")}, Activity: {p.Get("ActivityDesc")}, " +
        //                    $"Company: {p.Get("CompanyName")}, Owner: {string.Join(", ", owners[qdrid ?? 0].Select(o => o.OwnerName))} , License Civil Id:{p.Get("LicCivilId")} ",

        //                "questionnairelicowner" =>
        //                    $"Owners: {string.Join(", ", owners[qdrid ?? 0].Select(o => o.OwnerName))}, Nationality: {p.Get("CivilNationName")}, " +
        //                    $"License: {string.Join(", ", licns[qdrid ?? 0].Select(l => l.LicnCommNo))} , License Civil Id: {p.Get("LicCivilId")} , Activity Desc: {string.Join(", ", licns[qdrid ?? 0].Select(l => l.ActivityDesc))}",

        //                _ => ""
        //            };

        //            if (!string.IsNullOrEmpty(line) && seen.Add(line))
        //                sb.AppendLine(line);

        //            //-----------------------------

        //            #region Old Witch Case

        //            //switch (r.EntityType.ToLowerInvariant())
        //            //{
        //            //    case "questionnairedatum":
        //            //        var ownerComapny = await _db.QuestionnaireLicOwners.FirstOrDefaultAsync(o => o.QuestionnaireDataRecid == questionnaireDataRecid);

        //            //        string? LicenseCivilId = ownerComapny?.LicCivilId?.ToString() ?? "";



        //            //        if (ownerComapny != null)
        //            //        {
        //            //            sb.AppendLine($"Company: {p.Get("CompName")}, License: {p.Get("LicnNo")}, Status: {p.Get("LicnStatus")}, Trained: {p.Get("EmployeeCount")}, Expires: {p.Get("ExpieryDate")} , Owner Name: {ownerComapny.OwnerName} , ComplienceOfficerName: {p.Get("ComplienceOfficerName")}, License Civil ID:{LicenseCivilId}");
        //            //        }
        //            //        else
        //            //            sb.AppendLine($"Company: {p.Get("CompName")}, License: {p.Get("LicnNo")}, Status: {p.Get("LicnStatus")}, Trained: {p.Get("EmployeeCount")}, Expires: {p.Get("ExpieryDate")} , ComplienceOfficerName: {p.Get("ComplienceOfficerName")},Licesne Civil Id: {LicenseCivilId}");
        //            //        break;
        //            //    case "questionnairealllicn":
        //            //        var Owner = await _db.QuestionnaireLicOwners.FirstOrDefaultAsync(o => o.QuestionnaireDataRecid == questionnaireDataRecid);
        //            //        string? OwnerName = Owner?.OwnerName ?? "";

        //            //        sb.AppendLine($"License: {p.Get("LicnCommNo")}, Type: {p.Get("LicnTypeDesc")}, Activity: {p.Get("ActivityDesc")}, Company: {p.Get("CompanyName")} , License Civil id: {p.Get("LicCivilId")} , Owner Name:{OwnerName}");
        //            //        break;
        //            //    case "questionnairelicowner":
        //            //        var owner = await _db.QuestionnaireLicOwners
        //            //             .FirstOrDefaultAsync(o => o.ReqOwnId == reqOwnId);


        //            //        var licnCivilID = owner?.LicCivilId.ToString();



        //            //        var licn = await _db.QuestionnaireAllLicns
        //            //            .FirstOrDefaultAsync(l => l.LicCivilId == licnCivilID);


        //            //        string? companyName = licn?.CompanyName?.ToString() ?? "";

        //            //        string? licnType = licn?.LicnTypeDesc?.ToString() ?? "";
        //            //        string? activityDesc = licn?.ActivityDesc?.ToString() ?? "";

        //            //        if (licn == null)
        //            //        {
        //            //            companyName = "";
        //            //        }

        //            //        if (owner != null)
        //            //        {
        //            //            sb.AppendLine($"Owner: {p.Get("OwnerName")}, Nationality: {p.Get("CivilNationName")}, Civil ID: {p.Get("CivilId")}, License Civil ID: {p.Get("LicCivilId")} , Company Name:{companyName},Licn Type:{licnType} ,Activity Desc:{activityDesc}");

        //            //        }
        //            //        break;
        //            //} 
        //            #endregion



        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogWarning(ex, "Qdrant failed, using SQL fallback");
        //    }

        //    var finalResult = sb.Length > 0 ? sb.ToString() : "No relevant data found.";
        //    return finalResult;

        //}

        #endregion





        //New Retrieve Context Method
        private async Task<List<ContextPart>> RetrieveUnifiedContextAsync(string query, string intent, Dictionary<string, string> parameters)
        {
            if (intent == "greeting")
                return new List<ContextPart>();

            var results = await _embeddingService.SearchUnifiedAsync(query, topK: 30); // الدالة اللي أعطيتك إياها قبل قليل

           
            if (!results.Any())
                return new List<ContextPart> { new ContextPart("لا توجد معلومات ذات صلة.", "none") };

            // جلب كل QuestionnaireDataRecid من النتائج (لو موجود)
            var recIds = results
               .Select(r =>
               {
                   if (r.RawPayload.TryGetValue("QuestionnaireDataRecid", out var v) && v is JsonElement element)
                   {
                       return element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var dec) ? (decimal?)dec : null;
                   }
                   return null;
               })
               .Where(x => x.HasValue)
               .Select(x => x.Value)
               .ToList();

            ILookup<decimal, QuestionnaireLicOwner>? owners = null;
            ILookup<decimal, QuestionnaireAllLicn>? licns = null;

            if (recIds.Any())
            {
                 owners = await _db.QuestionnaireLicOwners
                   .Where(o => o.QuestionnaireDataRecid.HasValue && recIds.Contains(o.QuestionnaireDataRecid.Value))
                   .ToLookupAsync(o => o.QuestionnaireDataRecid!.Value);

                // Allow multiple licenses per QuestionnaireDataRecid
                 licns = await _db.QuestionnaireAllLicns
                    .Where(l => l.QuestionnaireDataRecid.HasValue && recIds.Contains(l.QuestionnaireDataRecid.Value))
                    .ToLookupAsync(l => l.QuestionnaireDataRecid!.Value);
            }



            var contextParts = new List<ContextPart>();
            //var seen = new HashSet<string>();

            foreach (var r in results)
            {
                string? rawContext = r.SourceType switch
                {
                    "file" => BuildFileContext(r),
                    "db" =>  await BuildDbContext(r, owners, licns),
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(rawContext))
                    continue;

                if (string.IsNullOrWhiteSpace(rawContext))
                    continue;

                // نضيف عنوان لكل جزء حسب نوع المصدر
                string header = r.SourceType switch
                {
                    "file" => $"\n--- [من وثيقة رسمية - الملف: {r.FileName} - الصفحة: {r.Page ?? 0}] ---\n",
                    "db" => $"\n--- [من قاعدة بيانات السجل التجاري - رقم السجل: {r.EntityId}] ---\n",
                    _ => "\n--- [مصدر غير معروف] ---\n"
                };

                string fullContext = header + rawContext.Trim();

                // تجنب التكرار
                //if (seen.Add(fullContext))
                //{
                    contextParts.Add(new ContextPart(fullContext, r.SourceType!, r));
                //}

            }
            if (!contextParts.Any())
            {
                contextParts.Add(new ContextPart("لا توجد معلومات كافية للإجابة.", "none"));
            }

            return contextParts;
        }

        // ملفات
        private string BuildFileContext(UnifiedSearchResult r)
        {
            if (string.IsNullOrWhiteSpace(r.Text) || r.Text.Length < 40) return null;

            var parts = new List<string>
    {
            $"[ملف: {r.FileName ?? "غير معروف"}",
            r.Page.HasValue ? $"صفحة {r.Page}" : null,
            r.Section != null ? $"القسم: {r.Section}" : null,
            r.UploadDate != null ? $"التاريخ: {r.UploadDate.Split('T')[0]}" : null
        }.Where(p => p != null);

            return $"→ {r.Text.Trim()}\n   ({string.Join(" | ", parts)})";
        }

        // قاعدة البيانات
        private async Task<string> BuildDbContext(UnifiedSearchResult r, ILookup<decimal, QuestionnaireLicOwner>? owners, ILookup<decimal, QuestionnaireAllLicn>? licns)
        {
            var p = r.RawPayload;
            var qdrid = p.TryGetValue("QuestionnaireDataRecid", out var idObj) && idObj is JsonElement idEl && idEl.TryGetDecimal(out var idDec)
                ? (decimal?)idDec : null;

            var licnence = _db.QuestionnaireAllLicns.Where(l=>l.QuestionnaireDataRecid==qdrid).FirstOrDefault();

           return r.EntityType?.ToLower() switch
            {
                "questionnairedatum" =>

                            $"Company: {p.Get("CompName")}, License: {p.Get("LicnNo") ??licnence!.LicnCommNo}, Status: {p.Get("LicnStatus")}, " +
                            $"Trained: {p.Get("EmployeeCount")}, Expires: {p.Get("ExpieryDate")}, " +
                            $"Owners: {string.Join(", ", owners[qdrid ?? 0].Select(o => o.OwnerName))} , License Civil Id: {string.Join(", ", licns[qdrid ?? 0].Select(l => l.LicCivilId))}",

                "questionnairealllicn" =>
                    $"License: {p.Get("LicnCommNo")}, Type: {p.Get("LicnTypeDesc")}, Activity: {p.Get("ActivityDesc")}, " +
                    $"Company: {p.Get("CompanyName")}, Owner: {string.Join(", ", owners[qdrid ?? 0].Select(o => o.OwnerName))} , License Civil Id:{p.Get("LicCivilId")} ",

                "questionnairelicowner" =>
                    $"Owners: {string.Join(", ", owners[qdrid ?? 0].Select(o => o.OwnerName))}, Nationality: {p.Get("CivilNationName")}, " +
                    $"License: {string.Join(", ", licns[qdrid ?? 0].Select(l => l.LicnCommNo)) ?? licnence!.LicnCommNo} , License Civil Id: {p.Get("LicCivilId")} , Activity Desc: {string.Join(", ", licns[qdrid ?? 0].Select(l => l.ActivityDesc))}",

                _ => ""
            };
        }




        //-------------------------------------
        private async Task<(string Intent, Dictionary<string, string> Parameters)> ParseQuery(string query)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            query = " " + query.ToLowerInvariant() + " ";

            // 1. Greeting
            if (Regex.IsMatch(query, @"\b(hi|hello|مرحبا|اهلا|صباح|مساء|good)\b"))
                return ("greeting", parameters);

            // 2. Specific License
            var licMatch = Regex.Match(query, @"lic?\s*\d+/\d+");
            if (licMatch.Success)
            {
                parameters["LicnNo"] = licMatch.Value.Trim().ToUpper();
                return ("specific_license", parameters);
            }

            // 3. Company
            var compMatch = Regex.Match(query, @"\b(شركة|company)\s+([^\s,;]{3,})");
            if (compMatch.Success)
            {
                parameters["CompName"] = compMatch.Groups[2].Value.Trim();
                return ("company", parameters);
            }

            // 4. Owner
            if (Regex.IsMatch(query, @"\b(مالك|owner|يملك)\b") && Regex.IsMatch(query, @"lic?\d+/\d+"))
                return ("owner", parameters);

            // 5. Compliance
            if (Regex.IsMatch(query, @"\b(امتثال|compliance|تدريب|training)\b"))
                return ("compliance", parameters);

            // 6. Semantic
            if (Regex.IsMatch(query, @"\b(ابحث|search|مشابه|similar)\b"))
                return ("semantic", parameters);

            return ("general", parameters);
        }
    }

        static class DictExt { public static string Get(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v?.ToString() ?? "" : ""; }
}