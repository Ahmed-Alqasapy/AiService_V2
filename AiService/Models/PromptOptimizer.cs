using System.Text.RegularExpressions;

namespace AiService.Models
{
    public static class PromptOptimizer
    {
        public static string CleanMetadata(string text)
        {
            // Replace: [المصدر: dlyl_lmwzf_ljwhr_lsbh__jdyd__2.pdf - صفحة 0]
            // With: [dlyl_lmwzf_ljwhr_lsbh__jdyd__2.pdf]
            text = Regex.Replace(
                text,
                @"\[المصدر:\s*([^\s\-]+)[^\]]+\]",
                "[$1]"
            );

            // Remove header separators
            text = Regex.Replace(text, @"===\s*[^=]+\s*===", "");

            return text;
        }

        // Normalize whitespace
        public static string NormalizeWhitespace(string text)
        {
            // Replace multiple spaces with single space
            text = Regex.Replace(text, @" {2,}", " ");

            // Replace 3+ newlines with 2
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            // Remove spaces before/after newlines
            text = Regex.Replace(text, @" *\n *", "\n");

            return text;
        }
    }
}
