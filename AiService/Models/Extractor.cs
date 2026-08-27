using Docnet.Core;
using Docnet.Core.Models;
using DocumentFormat.OpenXml.Packaging;
using EvoPdf.PdfToText;
using NPOI.SS.Formula.Functions;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace AiService.Models
{
    public static class Extractor
    {
        public static string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = Regex.Replace(text, @"\s+", " ").Trim();
            text = Regex.Replace(text, @"[^\u0600-\u06FFa-zA-Z0-9\s\.\-\/]", "");
            return text;
        }
        static bool  HasTextLayer(string path)
        {
            using var doc = PdfDocument.Open(path);
            foreach (var page in doc.GetPages())
            {
                if (page.GetWords().Any(w => w.Text.Length > 1))
                    return true;
            }
            return false;
        }

        //----------------------------------------
        public static string ExtractTextWithEvoPdf(string pdfPath)
        {
            var sb = new StringBuilder();
            try
            {
                // === 1. Initialize Evo PDF to Text Converter ===
                var converter = new PdfToTextConverter();

                // === 3. Extract text ===
                string rawText = converter.ConvertToText(pdfPath);

                //rawText = Bidi.BidiText(rawText).Text;

                sb.Append(rawText);
            }

            catch (Exception ex)
            {
                //_logger?.LogError(ex, "EVO PDF extraction failed: {PdfPath}", pdfPath);
                return "[فشل في استخراج النص عبر EVO PDF]";
            }

            return sb.ToString();
        }

        //---------------------------------------------
        public static string ExtractDocxText(string path)
        {
            try
            {
                var dimensions = new PageDimensions(1024, 1024); // أو new PageDimensions { Width = 1024, Height = 1024 }
                using var doc = DocLib.Instance.GetDocReader(path, dimensions);
                var sb = new StringBuilder();
                for (int i = 0; i < doc.GetPageCount(); i++)
                    sb.Append(doc.GetPageReader(i)).Append(" ");

                var clean = Regex.Replace(sb.ToString(), @"(?s)<[^>]*>", " "); // remove HTML-like artifacts
                return CleanText(clean);

            }
            catch (Exception ex)
            {
                //_logger.LogError(ex, "فشل قراءة DOCX: {Path}", path);
                return "";
            }
        }

        //--------------------------------
        public static string ExtractExcelText(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var workbook = new XSSFWorkbook(fs);
                var sb = new StringBuilder();

                for (int i = 0; i < workbook.NumberOfSheets; i++)
                {
                    var sheet = workbook.GetSheetAt(i);
                    if (sheet == null) continue;

                    for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
                    {
                        var row = sheet.GetRow(r);
                        if (row == null) continue;

                        foreach (var cell in row.Cells)
                        {
                            var value = cell.CellType switch
                            {
                                CellType.String => cell.StringCellValue,
                                CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                                    ? cell.DateCellValue.ToString()
                                    : cell.NumericCellValue.ToString(),
                                CellType.Boolean => cell.BooleanCellValue.ToString(),
                                _ => cell.ToString()
                            };
                            sb.Append(value).Append(" ");
                        }
                    }
                }

                return CleanText(sb.ToString());
            }
            catch (Exception ex)
            {
                //_logger.LogError(ex, "فشل قراءة Excel: {Path}", path);
                return "";
            }
        }
        //-------------------------------
        public static string ExtractPptxTextWithPages(string path)
        {
            var sb = new StringBuilder();
            try
            {
                using var document = PresentationDocument.Open(path, false);
                var presentationPart = document.PresentationPart;
                if (presentationPart == null || presentationPart.SlideParts == null)
                    return "";

                var slideParts = presentationPart.SlideParts.ToList();
                //int slideNumber = 1;

                foreach (var slidePart in slideParts)
                {
                    //sb.AppendLine($"<<PAGE:{slideNumber}>>");

                    var slideText = new StringBuilder();

                    // === Extract text from all shapes in the slide ===
                    foreach (var paragraph in slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>())
                    {
                        var runTexts = paragraph.Descendants<DocumentFormat.OpenXml.Drawing.Run>()
                            .Select(r => r.Text?.Text)
                            .Where(t => !string.IsNullOrWhiteSpace(t));

                        foreach (var runText in runTexts)
                        {
                            slideText.Append(runText.Trim()).Append(" ");
                        }

                        // Add line break after each paragraph
                        if (runTexts.Any())
                            slideText.AppendLine();
                    }

                    string rawText = slideText.ToString();
                    //string cleanedText = ArabicTextCleaner.FixArabicText(rawText);

                    // === Optional: LLM fix if text looks like OCR ===


                    if (rawText.Length > 15)
                    {
                        sb.AppendLine(rawText);
                        sb.AppendLine(); // blank line between paragraphs
                    }

                    //slideNumber++;
                }
            }
            catch (Exception ex)
            {
                //_logger.LogError(ex, "Failed to extract text from PPTX: {Path}", path);
                return "";
            }

            return sb.ToString();
        }

        //------------------------------------
        public static async Task<string> ExtractAllText(string path)
        {
            var sb = new StringBuilder();

            // 1. TRY TEXT LAYER FIRST
            //var textLayer = ExtractPdfTextWithPages(path);
            //if (!string.IsNullOrWhiteSpace(textLayer))
            //{
            //    sb.AppendLine(textLayer);
            //}

            //// 2. ALWAYS RUN OCR (CAPTURES LOGOS, CALLIGRAPHY, SHIP)
            //var ocrText = ExtractWithOcrFromPdf(path);
            //if (!string.IsNullOrWhiteSpace(ocrText))
            //{
            //    if (ocrText.Length > textLayer.Length + 200)
            //    {
            //        sb.AppendLine("\n[نص إضافي من الصور والخطوط الزخرفية]");
            //        sb.AppendLine(ocrText);
            //    }
            //}
            string textLayer = null;
            try
            {
                if (HasTextLayer(path))
                {
                    //textLayer = ExtractPdfTextWithPages(path);
                    //textLayer = PdfPigTableExtractor.ExtractTextAndTables(path);
                    //textLayer = ExtractPdfWithLineByLineFix(path);

                    //ok to EvoPdfToText
                    //textLayer = ExtractTextWithEvoPdf(path);
                    //textLayer = ExtractTextWithEvoPdf(path);

                    #region Extract With Evo Pdf
                    //This is Old Extract With EvoPdf
                  //  textLayer = ExtractTextWithEvoPdf(path);
                    //--------------- 
                    #endregion



                    //Stop_Claude_FOr_now
                   textLayer = await ExtractWithYourCurrentExtractPy_Claude(path);

                    //TryEvoWithOut Rerank
                    //textLayer =  ExtractTextWithEvoPdf(path);

                    //syncfusion
                    // textLayer = ExtractArabicTextUniversal(path);
                    //textLayer = ExtractTextWithiTextSharp(path);
                    //textLayer = ExtractTextWithGhostscript(path);

                    //if (IsGoodArabicText(textLayer))
                    //{
                    sb.AppendLine(textLayer);
                    //_logger.LogInformation("Used Syncfusion text layer: {Chars} chars", textLayer.Length);
                    //}
                    //else
                    //{

                    //    _logger.LogWarning("Syncfusion returned poor Arabic → falling back to OCR");
                    //    textLayer = null;
                    //}
                }

            }
            catch (Exception ex)
            {
                //_logger.LogWarning(ex, "Syncfusion failed → fallback to OCR");
            }

            // === 2. OCR Fallback (Only if needed) ===
            if (sb.Length < 300 || textLayer == null) // < 300 chars = likely bad
            {


                //string ocrText = ExtractWithOcrFromPdf(path);
                //string ocrText = ExtractUsingIronPdf_BestArabic(path);
                //We Will Try _Claude For Now
                //string ocrText = await ExtractWithYourCurrentExtractPy(path);

                //commentForNow
                string ocrText = await ExtractWithYourCurrentExtractPy_Claude(path);
                if (!string.IsNullOrWhiteSpace(ocrText) && ocrText.Length > 100)
                {
                    // Avoid duplication
                    if (textLayer == null || StringSimilarity(textLayer, ocrText) < 0.7)
                    {
                        if (sb.Length > 0) sb.AppendLine("\n[نص إضافي من الصور والمسح الضوئي]");
                        sb.AppendLine(ocrText);
                    }
                    //_logger.LogInformation("OCR added {Chars} chars", ocrText.Length);
                }
            }





            var CleanedText = sb.ToString();

            //CleanedText = Regex.Replace(CleanedText, @"(\n\s*){2,}", "\n");
            //CleanedText = Regex.Replace(CleanedText, @"-{2,}", "-");
            //CleanedText = Regex.Replace(CleanedText, @"[^\u0600-\u06FF0-9\s\.,؛:()\[\]«»]", "");


            return CleanedText;
        }


        //----------------------------------------------------
        public static double StringSimilarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return 0;

            var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var intersect = setA.Intersect(setB).Count();
            return (2.0 * intersect) / (setA.Count + setB.Count);
        }


        ///-----------------------------------------
        public static async Task<string> ExtractWithYourCurrentExtractPy(string filePath , string resultPath = "Results" )
        {
            await RunYourExtractPy(filePath);

            var files = Directory.GetFiles(resultPath, "*.*", SearchOption.AllDirectories)
                              .Where(f => !f.EndsWith(".tmp"))
                              .ToArray();



            string fullText = string.Empty;
            foreach (var file in files)
            {
                try
                {
                    fullText = File.ReadAllText(file);
                    if (!string.IsNullOrWhiteSpace(fullText) && fullText.Length > 50)
                    {
                        //_logger.LogInformation("استخراج ناجح من extract.py: {File} → {Chars} chars", Path.GetFileName(file), fullText.Length);
                        return fullText;
                    }
                }
                catch (Exception ex)
                {
                    //_logger.LogError(ex, "فشل قراءة ملف النص من extract.py: {File}", Path.GetFileName(file));
                }
            }

            return fullText;


        }

        //---------------------------

        #region Extract With Py Claude
        public static async Task<string> ExtractWithYourCurrentExtractPy_Claude(string filePath , string resultPath = "Extracted")
        {
            try
            {
                // لازم مسار مطلق عشان يتطابق بالظبط مع المسار اللي RunYourExtractPy_Claude
                // بيستخدمه لبناء مجلد الإخراج (اللي بيتبنى جوه بروسس بايثون بـ WorkingDirectory مختلف)
                filePath = Path.GetFullPath(filePath);

                // Step 1: Run Python extraction
               await RunYourExtractPy_Claude(filePath);






              

                #region Extracted_OLD_MD
                // Step 2: Define paths
                // ملاحظة: بايثون بيستخدم Path(...).stem اللي بيشيل امتداد واحد بس،
                // فلازم هنا نطابقه بنفس الطريقة (مش نشيل الامتداد مرتين)
                string baseName = Path.GetFileNameWithoutExtension(filePath); // e.g., Report.v2.pdf -> Report.v2

                string uploadsDir = Path.GetDirectoryName(filePath)!;
                // مجلد منفصل لكل ملف (بنفس اسمه) عشان ما تختلطش نتايج ملفات مختلفة ببعض
                string extractedFolder = Path.Combine(uploadsDir, "Extracted", baseName);

                if (!Directory.Exists(extractedFolder))
                {
                    Console.WriteLine($"Extracted folder not found: {extractedFolder}");
                    return string.Empty;
                }

                string expectedtxtPath = Path.Combine(extractedFolder, $"{baseName}_clean.txt");
                #endregion

                string extractedText = string.Empty;

                if (File.Exists(expectedtxtPath))
                {
                    extractedText = await File.ReadAllTextAsync(expectedtxtPath);
                    if (!string.IsNullOrWhiteSpace(extractedText) && extractedText.Length > 50)
                    {
                        Debug.WriteLine($"Extracted {extractedText.Length} chars from {Path.GetFileName(expectedtxtPath)}");
                        return extractedText.Trim();
                    }
                }

                // Fallback: بحث داخل مجلد هذا الملف فقط (معزول) — مش هيرجع نص ملف تاني بالغلط
                var cleanFilesForThisDoc = Directory.GetFiles(extractedFolder, "*_clean.txt");
                if (cleanFilesForThisDoc.Length > 0)
                {
                    var fallbackFile = cleanFilesForThisDoc[0];
                    Debug.WriteLine($"Expected file not found ({Path.GetFileName(expectedtxtPath)}), using: {Path.GetFileName(fallbackFile)}");
                    extractedText = await File.ReadAllTextAsync(fallbackFile);
                    if (!string.IsNullOrWhiteSpace(extractedText) && extractedText.Length > 50)
                    {
                        return extractedText.Trim();
                    }
                }

                Debug.WriteLine($"No valid extracted text found in Extracted folder: {extractedFolder}");
                return string.Empty;
            }
            catch (System.Text.Json.JsonException jsonEx)
            {
                Console.WriteLine($"✗ JSON parsing error: {jsonEx.Message}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error: {ex.Message}");
                return string.Empty;
            }
        }
        #endregion

        //----------------------------------
        //MAKE_NEW_ONE_METHOD_FOR_EXTRACTION_.md||.json
        public static async Task<string> ExtractWithYourCurrentExtractPy_Claude_OLD(string filePath)
        {
            try
            {
                // Step 1: Run Python extraction
                await RunYourExtractPy_Claude(filePath);

                // Step 2: Read the output JSON file
                string outputJsonPath = Path.ChangeExtension(filePath, ".json");


                // Wait a bit for file to be written
                await Task.Delay(1000);

                // Check if output exists
                if (!File.Exists(outputJsonPath))
                {
                    Console.WriteLine($"Error: Output JSON not found at {outputJsonPath}");
                    return string.Empty;
                }

                // Step 3: Parse JSON and extract text
                string jsonContent = await File.ReadAllTextAsync(outputJsonPath);


                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    Console.WriteLine("Error: JSON file is empty");
                    return string.Empty;
                }

                // Parse JSON using System.Text.Json
                using var doc = System.Text.Json.JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                // Extract markdown from: extraction.markdown or extraction.text
                if (root.TryGetProperty("extraction", out var extraction))
                {
                    // Try markdown first
                    if (extraction.TryGetProperty("markdown", out var markdown))
                    {
                        string extractedText = markdown.GetString();

                        if (!string.IsNullOrWhiteSpace(extractedText) && extractedText.Length > 50)
                        {
                            Console.WriteLine($"✓ Extracted {extractedText.Length} characters (markdown)");
                            return extractedText;
                        }
                    }

                    // Fallback to text field
                    if (extraction.TryGetProperty("text", out var text))
                    {
                        string extractedText = text.GetString();

                        if (!string.IsNullOrWhiteSpace(extractedText) && extractedText.Length > 50)
                        {
                            Console.WriteLine($"✓ Extracted {extractedText.Length} characters (text)");
                            return extractedText;
                        }
                    }
                }

                Console.WriteLine("⚠ Warning: No valid text found in extraction result");
                return string.Empty;
            }
            catch (System.Text.Json.JsonException jsonEx)
            {
                Console.WriteLine($"✗ JSON parsing error: {jsonEx.Message}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error: {ex.Message}");
                return string.Empty;
            }
        }



        //---------------------------
        private static async Task RunYourExtractPy_OLD()
        {
            try
            {
                // المسار الكامل لملف extract.py بتاعك (كما هو عندك)
                string scriptPath = @"C:\D\Hybird_Module\Combined\Hybird_API_Extract_Trial_login\Hybird_LOGIN\AiService\extract.py";

                // تأكد إن الملف موجود
                if (!File.Exists(scriptPath))
                {
                    //_logger?.LogError("extract.py غير موجود في المسار: {Path}", scriptPath);
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "python",                                    // أو المسار الكامل لـ python.exe
                    Arguments = $"\"{scriptPath}\"",                         // تشغيل الملف
                    WorkingDirectory = Path.GetDirectoryName(scriptPath),          // يشتغل من نفس المجلد
                    UseShellExecute = false,                                       // يشتغل لوحده بدون انتظار
                    CreateNoWindow = false,                                      // تشوف النافذة السودا (غيّر لـ true لو مش عايزها)
                    WindowStyle = ProcessWindowStyle.Normal
                };


                using var process = new System.Diagnostics.Process { StartInfo = startInfo };
                process.Start();
                //_logger?.LogInformation("تم تشغيل extract.py بنجاح");

                await process.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                //_logger?.LogError(ex, "فشل تشغيل extract.py");
            }

        }

        //--------------------------
        #region Clude Code Python
        //private static async Task RunYourExtractPy_Claude(string uploadedFilePath)
        //{
        //    try
        //    {
        //        //string scriptPath = @"C:\D\Hybird_Module\Combined\Hybird_API_Extract_Trial_login\Hybird_LOGIN\AiService\extract.py";
        //        //string scriptPath = @"C:\D\Hybird_Module\Combined\Hybird_API_Extract_Trial_login\Hybird_LOGIN\AiService\extract_one.py";
        //        string scriptPath = @"C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB_SEM_CHUNK\Hybird_LOGIN_JINA_S_CHUNK\AiService\extract_claude.py";

        //        if (!File.Exists(scriptPath))
        //        {
        //            //_logger.LogError("extract.py غير موجود!");
        //            return;
        //        }

        //        var startInfo = new ProcessStartInfo
        //        {
        //            FileName = "python",
        //            Arguments = $"\"{scriptPath}\" \"{uploadedFilePath}\"",  // ← هنا المفتاح: نمرر الملف الجديد بس
        //            WorkingDirectory = Path.GetDirectoryName(scriptPath),          // يشتغل من نفس المجلد
        //            UseShellExecute = true,                                       // يشتغل لوحده بدون انتظار
        //            CreateNoWindow = false,                                      // تشوف النافذة السودا (غيّر لـ true لو مش عايزها)
        //            WindowStyle = ProcessWindowStyle.Normal
        //        };


        //        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        //        process.Start();
        //        //_logger?.LogInformation("تم تشغيل extract.py بنجاح");

        //        await process.WaitForExitAsync();

        //        if (process.ExitCode == 0)
        //        {
        //            //_logger.LogInformation("تم استخراج النص بنجاح: {File}\n{Output}", Path.GetFileName(uploadedFilePath), output);
        //        }
        //        else
        //        {
        //            //_logger.LogError("فشل EasyOCR: {Error}", error);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        //_logger.LogError(ex, "خطأ في تشغيل extract.py");
        //    }

        //} 

        //------------------------

        private static async Task RunYourExtractPy_Claude(string uploadedFilePath)
        {
            try
            {
                //string scriptPath = @"C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB_SEM_CHUNK\Hybird_LOGIN_JINA_S_CHUNK\AiService\extract_claude_one.py";
                //string scriptPath = @"C:\D\Hybird_Module\Combined\Working_improvements\HY_TRY_CHUNK_IMPROVE\Hybird_CHUNK_IMPROVE\AiService\extract_claude_two.py";
                //string scriptPath = @"C:\D\Hybird_Module\Combined\Working_improvements\HY_TRY_CHUNK_IMPROVE\Hybird_CHUNK_IMPROVE\AiService\extract_claude_up.py";

                // extract_universal.py replaces extract_claude_up.py. It still writes
                // <baseName>_clean.txt into the same folder, so nothing downstream of
                // here changes; it additionally writes _chunks.json / _tables.json for
                // the next stage. Resolved next to this project rather than hardcoded
                // to the other working folder, so edits here actually take effect.
                string scriptPath = Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "extract_universal.py");
                scriptPath = Path.GetFullPath(scriptPath);

                if (!File.Exists(scriptPath))
                {
                    // fall back to the absolute path if the app is published elsewhere
                    scriptPath = @"C:\D\Hybird_Module\Combined\Working_improvements\MOST_SUCCESS_AI_PROJECTS\HY_TRY_CHUNK_COMP_MODEL\Hybird_CHUNK_IMPROVE_COMP_MODEL\AiService\extract_universal.py";
                }

                // Validate script exists
                if (!File.Exists(scriptPath))
                {
                    Console.WriteLine($"Error: Python script not found at {scriptPath}");
                    return;
                }

                // Validate input file exists
                if (!File.Exists(uploadedFilePath))
                {
                    Console.WriteLine($"Error: PDF file not found at {uploadedFilePath}");
                    return;
                }

                // لازم مسار مطلق (Absolute) — لأن WorkingDirectory للـ python process بنحطه
                // على مجلد السكريبت مش مجلد التطبيق، فأي مسار نسبي هيتفسر غلط جوه بايثون
                uploadedFilePath = Path.GetFullPath(uploadedFilePath);

                // مسار كامل لـ python.exe بدل الاعتماد على PATH — عشان لو التطبيق شغال
                // تحت IIS/Service/Visual Studio ومعندوش نفس الـ PATH بتاع الترمينال العادي
                // (فيه أكتر من نسخة بايثون على الجهاز، والمكتبات المطلوبة مثبتة في C:\Python313 بس)
                string pythonExePath = @"C:\Python313\python.exe";

                // Validate python.exe exists
                if (!File.Exists(pythonExePath))
                {
                    Console.WriteLine($"Error: python.exe not found at {pythonExePath}");
                    return;
                }

                // في C# — أضمن حل
                // مجلد منفصل لكل ملف (بنفس اسمه) عشان ما تختلطش نتايج ملفات مختلفة ببعض
                string baseName = Path.GetFileNameWithoutExtension(uploadedFilePath);
                string extractedFolder = Path.Combine(Path.GetDirectoryName(uploadedFilePath)!, "Extracted", baseName);
                Directory.CreateDirectory(extractedFolder);

                string outputFilePath = extractedFolder;

                // FIX: Add BOTH input_path AND output_path arguments
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExePath,
                    WorkingDirectory = Path.GetDirectoryName(scriptPath),          // يشتغل من نفس المجلد
                    UseShellExecute = false,                                      // لازم false عشان نقدر نمسك stdout/stderr
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                // ArgumentList يخلي .NET يعمل الـ escaping صح للمسارات العربية/فيها مسافات
                startInfo.ArgumentList.Add(scriptPath);
                startInfo.ArgumentList.Add(uploadedFilePath);
                startInfo.ArgumentList.Add(outputFilePath);

                using var process = new System.Diagnostics.Process { StartInfo = startInfo };

                var stdOut = new StringBuilder();
                var stdErr = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) stdOut.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) stdErr.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    Console.WriteLine($"✓ extract_universal.py finished successfully:\n{stdOut}");
                }
                else
                {
                    Console.WriteLine($"✗ extract_universal.py failed (exit code {process.ExitCode}):\n{stdErr}\n{stdOut}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error running extraction: {ex.Message}");
            }
        }
        //----------------------------
        #endregion


        //OLD_CLAUDE_MAKE_NEW_ONE_FOR_EXTTACT


        //----------------------------
        private static async Task RunYourExtractPy(string uploadedFilePath)
        {
            try
            {
                //string scriptPath = @"C:\D\Hybird_Module\Combined\Hybird_API_Extract_Trial_login\Hybird_LOGIN\AiService\extract.py";
                //string scriptPath = @"C:\D\Hybird_Module\Combined\Hybird_API_Extract_Trial_login\Hybird_LOGIN\AiService\extract_one.py";
                string scriptPath = @"C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB_SEM_CHUNK\Hybird_LOGIN_JINA_S_CHUNK\AiService\extract_one.py";

                if (!File.Exists(scriptPath))
                {
                    //_logger.LogError("extract.py غير موجود!");
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\" \"{uploadedFilePath}\"",  // ← هنا المفتاح: نمرر الملف الجديد بس
                    WorkingDirectory = Path.GetDirectoryName(scriptPath),          // يشتغل من نفس المجلد
                    UseShellExecute = true,                                       // يشتغل لوحده بدون انتظار
                    CreateNoWindow = false,                                      // تشوف النافذة السودا (غيّر لـ true لو مش عايزها)
                    WindowStyle = ProcessWindowStyle.Normal
                };


                using var process = new System.Diagnostics.Process { StartInfo = startInfo };
                process.Start();
                //_logger?.LogInformation("تم تشغيل extract.py بنجاح");

                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    //_logger.LogInformation("تم استخراج النص بنجاح: {File}\n{Output}", Path.GetFileName(uploadedFilePath), output);
                }
                else
                {
                    //_logger.LogError("فشل EasyOCR: {Error}", error);
                }
            }
            catch (Exception ex)
            {
                //_logger.LogError(ex, "خطأ في تشغيل extract.py");
            }

        }
    }
}
