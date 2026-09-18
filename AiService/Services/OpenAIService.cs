using AiService.Interfaces;
using AiService.Models;
using Azure;
using Azure.AI.Inference;
using Azure.Core;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;


namespace AiService.Services
{
    public class OpenAIService
    {
        private readonly HttpClient _httpClient;

        private readonly HttpClient _aihttp;
        private readonly ILogger<OpenAIService> _logger;
        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly string _model;

        private readonly ChatCompletionsClient  _Client_AZURE ;
        private readonly AiService.Models.ChatTrace _trace;




        public OpenAIService(HttpClient httpClient, IConfiguration configuration, ILogger<OpenAIService> logger,
            AiService.Models.ChatTrace trace)
        {
            _httpClient = httpClient;
            _logger = logger;
            _trace = trace;
            _apiKey = configuration["OpenAI:ApiKey"] ?? throw new ArgumentNullException("OpenAI API key is missing.");
            _modelName = "llama3.1:8b-instruct-q8_0";
            //_modelName = "qwen3:8b";
            _model = "gpt4o-mini-dev";

            //API KEY
            _apiKey = configuration["OpenAI:ApiKey"]
            ?? throw new ArgumentNullException("OpenAI:ApiKey is missing in appsettings.json");

            // _model = configuration["OpenAI:Model"] ?? "gpt-4o-mini";

            _model = configuration["OpenAI:Model"] ?? "gpt4o-mini-dev";


            _aihttp = new HttpClient();
            _aihttp.BaseAddress = new Uri("https://moci-openai-dev.cognitiveservices.azure.com/openai/deployments/gpt4o-mini-dev");
            _aihttp.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);

            //----------------------------

            var endpoint = new Uri("https://moci-openai-dev.cognitiveservices.azure.com/openai/deployments/gpt4o-mini-dev");
            var credential = new AzureKeyCredential(_apiKey);

            _Client_AZURE = new ChatCompletionsClient(
                endpoint,
                credential

            );
            
            //---------------------------

        }

        private static readonly RateLimiter _rateLimiter = new FixedWindowRateLimiter(
      new FixedWindowRateLimiterOptions
      {
          Window = TimeSpan.FromMinutes(1),
          PermitLimit = 50, // 50 requests per minute (adjust to your tier)
          QueueLimit = 100  // Queue up to 100 waiting requests
      }
  );

        //For_Qwen
        public async Task<string> GenerateResponseAsync_OLD_QWEN(
        string finalPrompt,
        object llmOptions = null)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();

                var qwen3BestOptions = new
                {
                    temperature = 0.0,
                    top_p = 1.0,
                    top_k = 1,
                    num_ctx = 8192,
                    num_predict = 4096,
                    repeat_penalty = 1.05,
                    stop = new string[] { "<|eot_id|>", "<|end_of_text|>", "<|im_end|>" }
                    // No enable_thinking here—handled at top level
                };

                var mergedOptions =  qwen3BestOptions;

                // Append /no_think to prompt for double-safety (Qwen3-specific override)
                // FORCE Qwen3 to obey blindly – the only way that works 100%
                var forceObeyPrompt = finalPrompt.Trim() + "\n\n[OUTPUT_STARTS_HERE_DO_NOT_WRITE_ANYTHING_BEFORE_THIS_LINE]";
                var body = new
                {
                    model = _modelName, // e.g., "qwen3:8b"
                    messages = new[]
                    {
                new { role = "user", content = forceObeyPrompt }
            },
                    // KEY FIX: Explicitly disable thinking at top level
                    think = false,
                    stream = false,
                    options = mergedOptions
                };

                var json = JsonConvert.SerializeObject(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("http://10.21.2.143:11434/api/chat", content);
                response.EnsureSuccessStatusCode();

                stopwatch.Stop();
                _logger?.LogInformation("Qwen3 Clean Response (No Thinking): {Ms}ms", stopwatch.ElapsedMilliseconds);

                var responseText = await response.Content.ReadAsStringAsync();
                var jsonResponse = JObject.Parse(responseText);
                string answer = jsonResponse["message"]?["content"]?.ToString() ?? "";

                // الحل السحري النهائي – يعصا موسى لـ Qwen3
                var marker = "[OUTPUT_STARTS_HERE_DO_NOT_WRITE_ANYTHING_BEFORE_THIS_LINE]";
                if (answer.Contains(marker))
                {
                    answer = answer.Split(new[] { marker }, StringSplitOptions.None)[1];
                }

                // الق rule الأخير الذي يضمن عدم وجود "" أبداً
                answer = answer.Trim();

                // إذا طلع فاضي → معناه النموذج نسخ النص قبل العلامة → نأخذ أول → موجود
                if (string.IsNullOrWhiteSpace(answer))
                {
                    var lines = answer.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Trim().StartsWith("→") || line.Contains("حساب") && line.Contains("جديد"))
                        {
                            answer = line.Trim();
                            // نأخذ السطر + السطرين اللي بعده عشان نضمن كامل الفقرة
                            int index = Array.IndexOf(lines, line);
                            if (index >= 0 && index < lines.Length - 2)
                                answer = string.Join("\n", lines.Skip(index).Take(8)).Trim();
                            break;
                        }
                    }
                }

                // آخر حماية مطلقة
                if (string.IsNullOrWhiteSpace(answer))
                    answer = "عذرًا، حدث خطأ في استخراج النص."; // لن يحدث أبداً بعد التعديل

                return answer;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to call Qwen3");
                return "عذرًا، حدث خطأ مؤقت. جاري العمل على إصلاحه.";
            }
        }



        //------------------------------------

        public async Task<string> GenerateResponseAsync_OLD_STREAM(
    string finalPrompt,
    object llmOptions = null,
    IProgress<string> progress = null)  // Report chunks as they arrive
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var requestUri = "http://10.21.2.143:11434/api/generate";

                var body = new
                {
                    model = _modelName,
                    prompt = finalPrompt,
                    stream = true,  // ✅ Enable streaming
                    options = llmOptions
                };

                var jsonContent = JsonConvert.SerializeObject(body);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
                {
                    Content = content
                };

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead);

                response.EnsureSuccessStatusCode();

                var fullResponse = new StringBuilder();
                var stream = await response.Content.ReadAsStreamAsync();

                using var reader = new StreamReader(stream, Encoding.UTF8);

                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var chunk = JsonConvert.DeserializeObject<JObject>(line);
                        var chunkText = chunk?["response"]?.Value<string>();

                        if (!string.IsNullOrEmpty(chunkText))
                        {
                            fullResponse.Append(chunkText);

                            // ✅ Report progress to UI (feels much faster!)
                            progress?.Report(chunkText);
                        }

                        if (chunk?["done"]?.Value<bool>() == true)
                            break;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        continue;
                    }
                }

                stopwatch.Stop();
                _logger.LogInformation("Streaming completed in {Ms}ms", stopwatch.ElapsedMilliseconds);

                return fullResponse.ToString().Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Streaming error");
                throw;
            }
        }
        //------------------------------------
        #region New_GEN_RESPONSE // 30_11_2025:6:26
        public async Task<string> GenerateResponseAsync(
                string finalPrompt,           // ← الـ Prompt الكامل (system + user + context)
                object llmOptions = null)     // ← الـ Options الديناميكية (temperature, max_tokens, إلخ)
        {
            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                var requestUri = "http://10.21.2.143:11434/api/generate";

                // الـ Options الافتراضية (آمنة وممتازة للعربية)


                var body = new
                {
                    model = _modelName,
                    prompt = finalPrompt,
                    stream = false,
                    options = llmOptions
                };

                //var body = new
                //{
                //    model = _modelName,
                //    messages = new[]
                //            {
                //                new { role = "system", content = "أنت مساعد ذكي متخصص في الوثائق الحكومية الكويتية اجب فقط من الوثائق التالية" },
                //                new { role = "user", content = finalPrompt }  // Your full prompt goes as user message
                //            },
                //    stream = false,
                //    options = llmOptions
                //};

                var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
                {
                                Content = new StringContent(
                    JsonConvert.SerializeObject(body),
                    Encoding.UTF8,
                    "application/json")
                };
                 // var jsonContent = JsonConvert.SerializeObject(body, Formatting.None);
                //var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                //  var response = await _httpClient.PostAsync(requestUri, content);

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                stopwatch.Stop();
                Debug.WriteLine($"Response Time is: {stopwatch.ElapsedMilliseconds} MS");

                var responseText = await response.Content.ReadAsStringAsync();
                var json = JsonConvert.DeserializeObject<JObject>(responseText);

                // ✅ Chat endpoint structure: response is in message.content

                string? answer;

                // ✅ For /api/generate endpoint
                if (json?["response"] != null)
                {
                    answer = json["response"]?.Value<string>()?.Trim();
                }

                else if (json?["message"]?["content"] != null)
                {
                    answer = json["message"]?["content"]?.Value<string>()?.Trim();
                }

                else
                {
                    throw new Exception("Unknown response format");
                }

                if (string.IsNullOrWhiteSpace(answer))
                {
                    throw new Exception("Model returned empty response.");
                }

                return answer;


            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Failed to connect to Ollama: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error generating response");
                throw new Exception("Failed to generate response from model", ex);

            }
        }

        // دالة مساعدة لدمج الـ Options

        public async Task<string> GenerateResponseAsync_OLD_LLama1B(
         string finalPrompt,
         object llmOptions = null)
            {
                try
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();

                    string scriptPath = @"C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB_SEM_CHUNK\Hybird_LOGIN_JINA_S_CHUNK\AiService\load_llama.py";

                    if (!File.Exists(scriptPath))
                        throw new FileNotFoundException($"Python script not found: {scriptPath}");

                    // Serialize llmOptions to JSON string
                    string optionsJson = llmOptions == null
                        ? "{}"
                        : JsonConvert.SerializeObject(llmOptions);

                    // Escape quotes for command line
                    string escapedPrompt = finalPrompt.Replace("\"", "\\\"");
                    string escapedOptions = optionsJson.Replace("\"", "\\\"");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{scriptPath}\" \"{escapedPrompt}\" \"{escapedOptions}\"",
                        WorkingDirectory = Path.GetDirectoryName(scriptPath),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,    // ← First
                        RedirectStandardError = true,     // ← First
                        CreateNoWindow = true,

                        StandardOutputEncoding = Encoding.UTF8,  // ← Then
                        StandardErrorEncoding = Encoding.UTF8   // ← Then
                    };

                    using var process = new Process { StartInfo = startInfo };
                    process.Start();

                    // Read output and error concurrently
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    await process.WaitForExitAsync();

                    string output = await outputTask;
                    string error = await errorTask;

                    stopwatch.Stop();
                    Debug.WriteLine($"Local Python Inference Time: {stopwatch.ElapsedMilliseconds} ms");

                    if (process.ExitCode != 0)
                    {
                        //_logger?.LogError($"Python script failed: {error}");
                        //throw new Exception($"Python script error: {error}");
                    }

                    if (string.IsNullOrWhiteSpace(output))
                    {
                        throw new Exception("Model returned empty response.");
                    }

                    return output.Trim();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error running local Python LLM");
                    throw new Exception("Failed to generate response from local model", ex);
                }
            }
        private static object MergeOptions(object defaultOpts, object customOpts)
        {
            var defaultJson = JsonConvert.SerializeObject(defaultOpts);
            var customJson = JsonConvert.SerializeObject(customOpts);

            var defaultJ = JObject.Parse(defaultJson);
            var customJ = JObject.Parse(customJson);

            defaultJ.Merge(customJ, new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Replace
            });

            return defaultJ;
        }
        #endregion


        // Generate Answer For OpenAI API KEY
        #region Generate_For_Comliation_Model
        public async Task<string> GenerateAnswer_OLD(string finalPrompt, string llmOptions = null)
        {


            var payload = new
            {
                model = _model,
                messages = new[]
                {
                new { role = "system", content = "أنت نظام استخراج نص حرفي فقط. لا تفسر، لا تلخص، لا تضيف." },
                new { role = "user", content = finalPrompt }
            },
                temperature = 0.5,
                top_p = 1.0,
                frequency_penalty = 0.0,
                presence_penalty = 0.0,
                max_tokens = 512,
                //stop = new[] { "الإجابة:" }
                stop = new string[] { }
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                var response = await _aihttp.PostAsync("chat/completions", content);
                //var response = await _aihttp.PostAsync("responses", content);
                stopwatch.Stop();

                Debug.WriteLine($"Time is {stopwatch.ElapsedMilliseconds} MS ");
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    return $"خطأ في OpenAI: {response.StatusCode} - {error}";
                }

                var json = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
                return json
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()
                    ?.Trim() ?? "لا يوجد رد.";

            }
            catch (Exception ex)
            {
                return $"خطأ في الاتصال: {ex.Message}";
            }
        }

        #endregion


        #region NEW Generate Method Generate Answer handle OpenAI Response WithOut Knowing Tokens
        public async Task<string> GenerateAnswer_OLD_ONE(string finalPrompt, string llmOptions = null)
        {
            var payload = new
            {
                model = _model,
                messages = new[]
                {
            new { role = "system", content = "أنت نظام استخراج نص حرفي فقط. لا تفسر، لا تلخص، لا تضيف." },
            new { role = "user", content = finalPrompt }
        },
                temperature = 0.5,
                top_p = 1.0,
                frequency_penalty = 0.0,
                presence_penalty = 0.0,
                max_tokens = 512,
                stop = new string[] { }
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                var response = await _aihttp.PostAsync("chat/completions", content);
                stopwatch.Stop();
                Debug.WriteLine($"Time is {stopwatch.ElapsedMilliseconds} MS ");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();

                    // Check if it's a rate limit error (429 status code)
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        try
                        {
                            var errorJson = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(errorContent);

                            // Check if the error contains rate limit information
                            if (errorJson.TryGetProperty("error", out var errorObj) &&
                                errorObj.TryGetProperty("code", out var code) &&
                                code.GetString() == "rate_limit_exceeded")
                            {
                                // Return user-friendly Arabic message
                                return "عذراً، تم تجاوز الحد المسموح من الطلبات. يرجى المحاولة مرة أخرى بعد قليل.";
                            }
                        }
                        catch
                        {
                            // If JSON parsing fails, return generic rate limit message
                            return "عذراً، تم تجاوز الحد المسموح من الطلبات. يرجى المحاولة مرة أخرى لاحقاً.";
                        }
                    }

                    // For other errors, log the technical details (for debugging) but show friendly message
                    Debug.WriteLine($"OpenAI Error: {response.StatusCode} - {errorContent}");
                    return "عذراً، حدث خطأ في معالجة طلبك. يرجى المحاولة مرة أخرى.";
                }

                var json = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(
                    await response.Content.ReadAsStringAsync()
                );

                return json
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()
                    ?.Trim() ?? "لا يوجد رد.";
            }
            catch (Exception ex)
            {
                // Log the technical error for debugging
                Debug.WriteLine($"Exception in GenerateAnswer: {ex.Message}");

                // Return user-friendly message
                return "عذراً، حدث خطأ في الاتصال بالخدمة. يرجى المحاولة مرة أخرى.";
            }
        }
        #endregion




        #region For_Token_Estimation_OPEN API KEY 25/12/2025 With Knowing Tokens Used
        public async Task<string> GenerateAnswer_OLDAPIKEY(string finalPrompt, string llmOptions = null)
        {

            using var lease = await _rateLimiter.AcquireAsync(permitCount: 1);

            if (!lease.IsAcquired)
            {
                return "عذراً، النظام مشغول حالياً. يرجى المحاولة بعد قليل.";
            }

            var payload = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = "أنت نظام استخراج نص حرفي فقط. لا تفسر، لا تلخص، لا تضيف." },
                    //new { role = "system", content = " استخراج نص حرفي فقط. لا تفسر، لا تلخص، لا تضيف." },
                    new { role = "user", content = finalPrompt }
                },
                temperature = 0.1,
                //top_p = 1.0,
                //frequency_penalty = 0.0,
                //presence_penalty = 0.0,
                max_tokens = 512,
                //stop = new string[] { }
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                var response = await _aihttp.PostAsync("chat/completions", content);
                stopwatch.Stop();
                Debug.WriteLine($"Time is {stopwatch.ElapsedMilliseconds} MS ");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        return "عذراً، تم تجاوز الحد المسموح من الطلبات. يرجى المحاولة مرة أخرى بعد قليل.";
                    }

                    Debug.WriteLine($"OpenAI Error: {response.StatusCode} - {errorContent}");
                    return "عذراً، حدث خطأ في معالجة طلبك. يرجى المحاولة مرة أخرى.";
                }

                var json = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(
                    await response.Content.ReadAsStringAsync()
                );

                // 🔹 Read token usage
                int inputTokens = 0;
                int outputTokens = 0;
                int totalTokens = 0;

                if (json.TryGetProperty("usage", out var usage))
                {
                    inputTokens = usage.GetProperty("prompt_tokens").GetInt32();
                    outputTokens = usage.GetProperty("completion_tokens").GetInt32();
                    totalTokens = usage.GetProperty("total_tokens").GetInt32();
                }

                // 🔹 Log tokens (or save to DB)
                Debug.WriteLine($"Input Tokens: {inputTokens}");
                Debug.WriteLine($"Output Tokens: {outputTokens}");
                Debug.WriteLine($"Total Tokens: {totalTokens}");

                // 🔹 Get model response
                return json
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()
                    ?.Trim() ?? "لا يوجد رد.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in GenerateAnswer: {ex.Message}");
                return "عذراً، حدث خطأ في الاتصال بالخدمة. يرجى المحاولة مرة أخرى.";
            }
        }




        #endregion


        public async Task<string> GenerateAnswer(string finalPrompt, string llmOptions = null)
        {
         //   OpenAIService _aiService  = new OpenAIService(_httpClient, null, _logger);


            using var lease = await _rateLimiter.AcquireAsync(permitCount: 1);
            if (!lease.IsAcquired)
            {
                _trace.Status = "RateLimited";
                return "عذراً، النظام مشغول حالياً. يرجى المحاولة بعد قليل.";
            }

            // Build the request (strongly typed)
            //var options = new ChatCompletionsOptions
            //{
            //    Model = _model,   // "gpt4o-mini-dev"
            //    Temperature = 0.1f,
            //    MaxTokens = 512,
            //    // TopP             = 1.0f,    // uncomment if needed
            //    // FrequencyPenalty = 0.0f,
            //    // PresencePenalty  = 0.0f,
            //};

            var requestOptions = new ChatCompletionsOptions()
            {
                Messages =
    {
        new ChatRequestUserMessage(finalPrompt)
    },
                MaxTokens = 4096,
                Temperature = 1.0f,
                NucleusSamplingFactor = 1.0f,
                Model = _model
            };
            // System + User message
            //options.Messages.Add(new ChatRequestSystemMessage(
            //    "أنت نظام استخراج نص حرفي فقط. لا تفسر، لا تلخص، لا تضيف."
            //));
            //options.Messages.Add(new ChatRequestUserMessage(finalPrompt));

            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                // Non-streaming call → gets complete response at once
               Response<ChatCompletions> response = _Client_AZURE.Complete(requestOptions);

                stopwatch.Stop();
                Debug.WriteLine($"Time is {stopwatch.ElapsedMilliseconds} MS");

                ChatCompletions result = response.Value;

                // Token usage (very similar structure)
                int inputTokens = result.Usage?.PromptTokens ?? 0;
                int outputTokens = result.Usage?.CompletionTokens ?? 0;
                int totalTokens = result.Usage?.TotalTokens ?? 0;

                Debug.WriteLine($"Input Tokens : {inputTokens}");
                Debug.WriteLine($"Output Tokens: {outputTokens}");
                Debug.WriteLine($"Total Tokens : {totalTokens}");

                _trace.Model = _model;
                _trace.InputTokens += inputTokens;
                _trace.OutputTokens += outputTokens;
                _trace.LlmMs += stopwatch.ElapsedMilliseconds;

                string answer = result.Choices.FirstOrDefault()?.Message?.Content?.Trim();

                return string.IsNullOrWhiteSpace(answer)
                    ? "لا يوجد رد."
                    : answer;
            }
            catch (RequestFailedException ex) when (ex.Status == 429)
            {
                _trace.Status = "RateLimited";
                return "عذراً، تم تجاوز الحد المسموح من الطلبات. يرجى المحاولة مرة أخرى بعد قليل.";
            }
            catch (RequestFailedException ex)
            {
                _trace.Status = "Error";
                _trace.Error = $"{ex.Status}: {ex.Message}";
                Debug.WriteLine($"Azure OpenAI Error: {ex.Status} - {ex.Message}");
                return "عذراً، حدث خطأ في معالجة طلبك. يرجى المحاولة مرة أخرى.";
            }
            catch (Exception ex)
            {
                _trace.Status = "Error";
                _trace.Error = ex.Message;
                Debug.WriteLine($"Exception in GenerateAnswer: {ex.Message}");
                return "عذراً، حدث خطأ في الاتصال بالخدمة. يرجى المحاولة مرة أخرى.";
            }
        }


        public async Task<string> GenerateAnswer_With_After_before(string finalPrompt, string llmOptions = null, int maxRetries = 3)
        {
            var payload = new
            {
                model = _model,
                messages = new[]
                {
            new { role = "system", content = "أنت نظام استخراج نص حرفي فقط. لا تفسر، لا تلخص، لا تضيف." },
            new { role = "user", content = finalPrompt }
        },
                temperature = 0.1,
                max_tokens = 512,
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            int retryCount = 0;
            int baseDelayMs = 1000; // Start with 1 second

            while (retryCount <= maxRetries)
            {
                try
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    var response = await _aihttp.PostAsync("chat/completions", content);
                    stopwatch.Stop();
                    Debug.WriteLine($"Time is {stopwatch.ElapsedMilliseconds} MS ");

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();

                        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            // 🔹 Check for Retry-After header
                            int waitTimeSeconds = 0;

                            if (response.Headers.TryGetValues("Retry-After", out var retryAfterValues))
                            {
                                var retryAfterValue = retryAfterValues.FirstOrDefault();
                                if (int.TryParse(retryAfterValue, out waitTimeSeconds))
                                {
                                    Debug.WriteLine($"Retry-After header found: {waitTimeSeconds} seconds");
                                }
                            }

                            // If no Retry-After header, use exponential backoff
                            if (waitTimeSeconds == 0)
                            {
                                waitTimeSeconds = (baseDelayMs * (int)Math.Pow(2, retryCount)) / 1000;
                                Debug.WriteLine($"Using exponential backoff: {waitTimeSeconds} seconds");
                            }

                            if (retryCount < maxRetries)
                            {
                                Debug.WriteLine($"Rate limited. Waiting {waitTimeSeconds} seconds before retry {retryCount + 1}/{maxRetries}");
                                await Task.Delay(waitTimeSeconds);
                                retryCount++;
                                continue; // Retry the request
                            }
                            else
                            {
                                return "عذراً، تم تجاوز الحد المسموح من الطلبات. يرجى المحاولة مرة أخرى بعد قليل.";
                            }
                        }

                        Debug.WriteLine($"OpenAI Error: {response.StatusCode} - {errorContent}");
                        return "عذراً، حدث خطأ في معالجة طلبك. يرجى المحاولة مرة أخرى.";
                    }

                    // ✅ Success - process response
                    var json = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(
                        await response.Content.ReadAsStringAsync()
                    );

                    // 🔹 Read token usage
                    int inputTokens = 0;
                    int outputTokens = 0;
                    int totalTokens = 0;

                    if (json.TryGetProperty("usage", out var usage))
                    {
                        inputTokens = usage.GetProperty("prompt_tokens").GetInt32();
                        outputTokens = usage.GetProperty("completion_tokens").GetInt32();
                        totalTokens = usage.GetProperty("total_tokens").GetInt32();
                    }

                    // 🔹 Log tokens
                    Debug.WriteLine($"Input Tokens: {inputTokens}");
                    Debug.WriteLine($"Output Tokens: {outputTokens}");
                    Debug.WriteLine($"Total Tokens: {totalTokens}");

                    // 🔹 Get model response
                    return json
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString()
                        ?.Trim() ?? "لا يوجد رد.";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Exception in GenerateAnswer: {ex.Message}");

                    // Retry on network errors
                    if (retryCount < maxRetries)
                    {
                        int waitTime = baseDelayMs * (int)Math.Pow(2, retryCount);
                        Debug.WriteLine($"Network error. Waiting {waitTime}ms before retry {retryCount + 1}/{maxRetries}");
                        await Task.Delay(waitTime);
                        retryCount++;
                        continue;
                    }

                    return "عذراً، حدث خطأ في الاتصال بالخدمة. يرجى المحاولة مرة أخرى.";
                }
            }

            return "عذراً، فشلت جميع محاولات الاتصال. يرجى المحاولة مرة أخرى لاحقاً.";
        }




        #region Old_GEN_RESPONSE
        //public async Task<string> GenerateResponseAsync(string userPrompt, string systemMessage = null)
        //{
        //    try
        //    {
        //        //var requestUri = "http://localhost:11434/api/generate";


        //        Stopwatch stopwatch = Stopwatch.StartNew();

        //        var requestUri = "http://10.21.2.143:11434/api/generate";



        //        string prompt = systemMessage != null
        //            ? $"{systemMessage}\n\nUser: {userPrompt}\nAssistant:"
        //            : $"User: {userPrompt}\nAssistant:";


        //        var body = new
        //        {
        //            model = _modelName,
        //            prompt = prompt,
        //            stream = false,

        //            options = new
        //            {
        //                temperature = 0.1,
        //                num_ctx = 8192,        // سياق طويل للعربية
        //                num_predict = 300,     // أطول من 265
        //                repeat_penalty = 1.1
        //            }
        //        };
        //        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        //        {
        //            Content = new StringContent(
        //        JsonConvert.SerializeObject(body),
        //        Encoding.UTF8,
        //        "application/json")
        //        };

        //        var response = await _httpClient.SendAsync(request);
        //        response.EnsureSuccessStatusCode();

        //        stopwatch.Stop();
        //        Debug.WriteLine($"Response Time is: {stopwatch.ElapsedMilliseconds} MS");

        //        var responseText = await response.Content.ReadAsStringAsync();
        //        var json = JsonConvert.DeserializeObject<JObject>(responseText);

        //        // ✅ Chat endpoint structure: response is in message.content


        //        string? answer;

        //        // ✅ For /api/generate endpoint
        //        if (json?["response"] != null)
        //        {
        //            answer = json["response"]?.Value<string>()?.Trim();
        //        }




        //        else if (json?["message"]?["content"] != null)
        //        {
        //            answer = json["message"]?["content"]?.Value<string>()?.Trim();
        //        }

        //        else
        //        {
        //            throw new Exception("Unknown response format");
        //        }

        //        if (string.IsNullOrWhiteSpace(answer))
        //        {
        //            throw new Exception("Model returned empty response.");
        //        }




        //        return answer;


        //    }
        //    catch (HttpRequestException ex)
        //    {
        //        throw new Exception($"Failed to connect to Ollama: {ex.Message}", ex);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger?.LogError(ex, "Error generating response");
        //        throw new Exception("Failed to generate response from model", ex);

        //    }
        //} 
        #endregion








    }
}