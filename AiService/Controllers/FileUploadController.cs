// Controllers/FileUploadController.cs
using AiService.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class FileUploadController : ControllerBase
    {
        private readonly EmbeddingService _embeddingService;
        private readonly ILogger<FileUploadController> _logger;
        private readonly string _uploadFolder = "Uploads";

        public FileUploadController(EmbeddingService embeddingService, ILogger<FileUploadController> logger)
        {
            _embeddingService = embeddingService;
            _logger = logger;
            Directory.CreateDirectory(_uploadFolder); // ينشئ المجلد لو ما كان موجود
        }

        // هذا الـ endpoint اللي تبي تستخدمه لرفع الملفات
        [HttpPost("upload")]
        public async Task<IActionResult> Upload(List<IFormFile> files)
        {
            if (files == null || !files.Any())
                return BadRequest(new { error = "لا توجد ملفات للرفع" });

            var savedFiles = new List<string>();

            foreach (var file in files)
            {
                if (file.Length == 0) continue;

                // اسم فريد + اسم الملف الأصلي
                var fileName = SanitizeFileName(file.FileName);
                if (fileName == null)
                    return BadRequest(new { error = $"اسم الملف غير صالح: {file.FileName}" });

                var filePath = Path.Combine(_uploadFolder, fileName);

                await using var stream = System.IO.File.Create(filePath);
                await file.CopyToAsync(stream);

                savedFiles.Add(fileName);
                _logger.LogInformation("تم حفظ الملف: {FilePath}", filePath);
            }

            // **الخطوة المهمة: فهرسة الملفات فورًا في Qdrant**
            await _embeddingService.IndexUploadedFilesAsync(_uploadFolder);

            return Ok(new
            {
                message = $"تم رفع وفهرسة {savedFiles.Count} ملف بنجاح",
                files = savedFiles,
                folder = _uploadFolder
            });
        }

        /// <summary>
        /// Windows silently strips trailing spaces and dots from folder names. The extractor
        /// writes to Uploads/Extracted/&lt;name without extension&gt;/, so "دار الرعاية .txt" got a
        /// folder "دار الرعاية" while every path still said "دار الرعاية " and extraction failed
        /// with "No such file or directory". Trimming here keeps the saved file, the Extracted
        /// folder, _clean.txt and file_name in Qdrant all on the same name.
        /// Returns null when nothing usable is left.
        /// </summary>
        private static string? SanitizeFileName(string rawName)
        {
            var name = Path.GetFileName(rawName ?? "");
            var stem = Path.GetFileNameWithoutExtension(name).Trim().TrimEnd('.', ' ');
            var ext = Path.GetExtension(name).Trim();
            return stem.Length == 0 ? null : stem + ext;
        }
    }
}