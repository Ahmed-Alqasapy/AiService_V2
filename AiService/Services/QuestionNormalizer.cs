using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AiService.Services
{
    // Arabic users write the same question many ways (أ/إ/ا, ة/ه, ى/ي, diacritics,
    // punctuation). Normalize before hashing so repeats group under one hash for the
    // "most common unanswered questions" report.
    public static class QuestionNormalizer
    {
        public static string Normalize(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return "";
            q = q.Trim().ToLowerInvariant();
            q = Regex.Replace(q, @"[ً-ٰٟـ]", "");   // diacritics + tatweel
            q = Regex.Replace(q, "[أإآٱ]", "ا");
            q = q.Replace('ة', 'ه').Replace('ى', 'ي').Replace('ؤ', 'و').Replace('ئ', 'ي');
            q = Regex.Replace(q, @"[٠-٩]", d => ((char)('0' + (d.Value[0] - '٠'))).ToString());
            q = Regex.Replace(q, @"[^\p{L}\p{N}\s/]", " ");
            q = Regex.Replace(q, @"\s+", " ").Trim();
            return q;
        }

        public static string Hash(string norm) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm ?? "")));
    }
}
