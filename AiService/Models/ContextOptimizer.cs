using System.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;


namespace AiService.Models
{

    public class ContextOptimizer
    {
        private const int MAX_TOKENS = 700;

        /// <summary>
        /// Optimizes context string to reduce token usage
        /// </summary>
        public string Optimize(string fullContext, int maxTokens = MAX_TOKENS)
        {
            if (string.IsNullOrWhiteSpace(fullContext))
                return string.Empty;

            // Step 1: Basic cleanup
            //var cleaned = BasicCleanup(fullContext);

            // Step 2: Remove duplicates
            var deduplicated = RemoveDuplicateLines(fullContext);

            // Step 3: Compress tables
            var compressed = CompressTables(deduplicated);

            // Step 4: Ensure token limit
            var final = TruncateToTokenLimit(compressed, maxTokens);

            return final;
        }

        /// <summary>
        /// Optimize with stats (returns optimized text and token count)
        /// </summary>
        public (string OptimizedText, int EstimatedTokens) OptimizeWithStats(string fullContext, int maxTokens = MAX_TOKENS)
        {
            var optimized = Optimize(fullContext, maxTokens);
            var tokens = EstimateTokens(optimized);
            return (optimized, tokens);
        }

        #region Private Methods

       

        private string RemoveDuplicateLines(string text)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>();
            var result = new StringBuilder();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && seen.Add(trimmed))
                {
                    result.AppendLine(trimmed);
                }
            }

            return result.ToString();
        }

        private string CompressTables(string text)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var result = new StringBuilder();

            foreach (var line in lines)
            {
                // If it's a table row, compress it
                if (line.Contains("|"))
                {
                    var compressed = CompressTableRow(line);
                    if (!string.IsNullOrWhiteSpace(compressed))
                    {
                        result.AppendLine(compressed);
                    }
                }
                else
                {
                    // Regular line - remove common filler words
                   // result.AppendLine(result);
                }
            }

            return result.ToString();
        }

        private string CompressTableRow(string line)
        {
            // Skip header rows
            if (IsTableHeader(line))
                return string.Empty;

            // Extract cells
            var cells = line.Split('|')
                           .Select(c => c.Trim())
                           .Where(c => !string.IsNullOrWhiteSpace(c))
                           .ToArray();

            if (cells.Length == 0)
                return string.Empty;

            // Remove filler words from cells
           

            // Join with dash (more compact than pipes)
            return string.Join(" - ", cells);
        }

        
        private string TruncateToTokenLimit(string text, int maxTokens)
        {
            int currentTokens = EstimateTokens(text);

            if (currentTokens <= maxTokens)
                return text;

            // Calculate truncation ratio
            double ratio = (double)maxTokens / currentTokens;
            int targetLength = (int)(text.Length * ratio * 0.95); // 95% safety margin

            // Truncate and try to end at a line break
            var truncated = text.Substring(0, Math.Min(targetLength, text.Length));
            int lastNewLine = truncated.LastIndexOf('\n');

            if (lastNewLine > 0)
                truncated = truncated.Substring(0, lastNewLine);

            return truncated;
        }

        private bool IsSeparatorLine(string line)
        {
            return Regex.IsMatch(line, @"^[\s\|\-\=\_\*]+$");
        }

        private bool IsTableHeader(string line)
        {
            var headers = new[] { "الرسوم", "نوع الخدمة", "اسم الخدمة", "المسلسل", "السلسل" };
            return headers.Any(h => line.Contains(h));
        }

        private int EstimateTokens(string text)
        {
            // Arabic: approximately 1 token per 2.5 characters
            return (int)Math.Ceiling(text.Length / 2.5);
        }

        #endregion
    }
}
