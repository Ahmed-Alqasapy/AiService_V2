using System.Text;
using System.Text.RegularExpressions;

namespace AiService.Services
{
    public class UniversalDocumentChunker
    {
        /// <summary>
        /// Universal document chunker supporting multiple languages and intelligent chunking strategies
        /// </summary>
      
            private const int DEFAULT_CHUNK_SIZE = 500;
            private const int DEFAULT_OVERLAP = 150;
            private const int MIN_CHUNK_SIZE = 100;

            private readonly int _chunkSize;
            private readonly int _overlapSize;
            private readonly ChunkingStrategy _strategy;

            public enum ChunkingStrategy
            {
                Sentence,      // Split by sentences (best for most content)
                Paragraph,     // Split by paragraphs (good for structured documents)
                Semantic,      // Smart splitting by topics/sections
                Fixed          // Fixed character/token count (simple)
            }

            public UniversalDocumentChunker(
                int chunkSize = DEFAULT_CHUNK_SIZE,
                int overlapSize = DEFAULT_OVERLAP,
                ChunkingStrategy strategy = ChunkingStrategy.Sentence)
            {
                _chunkSize = chunkSize;
                _overlapSize = overlapSize;
                _strategy = strategy;
            }

            /// <summary>
            /// Main chunking method - auto-detects language and applies best strategy
            /// </summary>
            public List<ChunkInfo> ChunkDocument(
                string text,
                string documentTitle = "",
                Dictionary<string, object> metadata = null)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return new List<ChunkInfo>();

                // Clean and normalize
                text = NormalizeText(text);


                text = CleanExtractedText(text);
            // Detect language
            var language = DetectLanguage(text);

                // Choose chunking method based on strategy
                List<ChunkInfo> chunks = _strategy switch
                {
                    ChunkingStrategy.Sentence => ChunkBySentences(text, language),
                    ChunkingStrategy.Paragraph => ChunkByParagraphs(text, language),
                    ChunkingStrategy.Semantic => ChunkBySemantic(text, language),
                    ChunkingStrategy.Fixed => ChunkByFixedSize(text, language),
                    _ => ChunkBySentences(text, language)
                };

                // Add metadata
                for (int i = 0; i < chunks.Count; i++)
                {
                    chunks[i].ChunkId = $"chunk_{i + 1:D4}";
                    chunks[i].DocumentTitle = documentTitle;
                    chunks[i].Language = language;
                    chunks[i].ChunkIndex = i;
                    chunks[i].TotalChunks = chunks.Count;

                    if (metadata != null)
                        chunks[i].Metadata = new Dictionary<string, object>(metadata);
                }

                return chunks;
            }

            #region Chunking Strategies

            /// <summary>
            /// Sentence-based chunking - Most accurate for Q&A and retrieval
            /// </summary>
            private List<ChunkInfo> ChunkBySentences(string text, string language)
            {
                var chunks = new List<ChunkInfo>();
                var sentences = SplitIntoSentences(text, language);

                int index = 0;
                while (index < sentences.Count)
                {
                    var chunk = BuildChunkFromUnits(
                        sentences,
                        ref index,
                        language,
                        "sentence"
                    );

                    if (chunk != null && chunk.TokenCount >= MIN_CHUNK_SIZE)
                        chunks.Add(chunk);
                }

                return chunks;
            }

            /// <summary>
            /// Paragraph-based chunking - Good for preserving document structure
            /// </summary>
            private List<ChunkInfo> ChunkByParagraphs(string text, string language)
            {
                var chunks = new List<ChunkInfo>();
                var paragraphs = SplitIntoParagraphs(text);

                int index = 0;
                while (index < paragraphs.Count)
                {
                    var chunk = BuildChunkFromUnits(
                        paragraphs,
                        ref index,
                        language,
                        "paragraph"
                    );

                    if (chunk != null && chunk.TokenCount >= MIN_CHUNK_SIZE)
                        chunks.Add(chunk);
                }

                return chunks;
            }

            /// <summary>
            /// Semantic chunking - Tries to keep related content together
            /// </summary>
            private List<ChunkInfo> ChunkBySemantic(string text, string language)
            {
                var chunks = new List<ChunkInfo>();
                var sections = DetectSemanticSections(text, language);

                foreach (var section in sections)
                {
                    var sentences = SplitIntoSentences(section.Content, language);
                    int index = 0;

                    while (index < sentences.Count)
                    {
                        var chunk = BuildChunkFromUnits(
                            sentences,
                            ref index,
                            language,
                            "semantic"
                        );

                        if (chunk != null)
                        {
                            chunk.SectionTitle = section.Title;
                            chunk.SectionLevel = section.Level;
                            if (chunk.TokenCount >= MIN_CHUNK_SIZE)
                                chunks.Add(chunk);
                        }
                    }
                }

                return chunks.Any() ? chunks : ChunkBySentences(text, language);
            }

            /// <summary>
            /// Fixed-size chunking - Simple and predictable
            /// </summary>
            private List<ChunkInfo> ChunkByFixedSize(string text, string language)
            {
                var chunks = new List<ChunkInfo>();
                var words = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                int wordsPerChunk = _chunkSize / 2; // Rough estimate
                int overlapWords = _overlapSize / 2;

                for (int i = 0; i < words.Length; i += (wordsPerChunk - overlapWords))
                {
                    var chunkWords = words.Skip(i).Take(wordsPerChunk).ToArray();
                    var content = string.Join(" ", chunkWords);

                    var chunk = new ChunkInfo
                    {
                        Content = content,
                        TokenCount = EstimateTokens(content, language),
                        Type = "fixed_size"
                    };

                    if (chunk.TokenCount >= MIN_CHUNK_SIZE)
                        chunks.Add(chunk);
                }

                return chunks;
            }

            #endregion

            #region Helper Methods

            /// <summary>
            /// Build a chunk from a list of text units (sentences/paragraphs)
            /// </summary>
            private ChunkInfo BuildChunkFromUnits(
                List<string> units,
                ref int index,
                string language,
                string type)
            {
                var buffer = new StringBuilder();
                int currentTokens = 0;
                int startIndex = index;
                var includedUnits = new List<string>();

                // Add units until we reach target size
                while (index < units.Count)
                {
                    string unit = units[index];
                    int unitTokens = EstimateTokens(unit, language);

                    // Stop if adding would significantly exceed limit
                    if (currentTokens > 0 && currentTokens + unitTokens > _chunkSize * 1.3)
                        break;

                    buffer.AppendLine(unit);
                    includedUnits.Add(unit);
                    currentTokens += unitTokens;
                    index++;

                    // If we've reached a good size, check if we should continue
                    if (currentTokens >= _chunkSize * 0.8)
                    {
                        // Look ahead - if next unit is small, include it
                        if (index < units.Count)
                        {
                            int nextTokens = EstimateTokens(units[index], language);
                            if (currentTokens + nextTokens > _chunkSize * 1.3)
                                break;
                        }
                    }
                }

                if (buffer.Length == 0)
                    return null;

                // Create overlap by backtracking
                int overlapTokens = 0;
                int backtrack = 0;

                while (backtrack < includedUnits.Count && overlapTokens < _overlapSize)
                {
                    backtrack++;
                    int backIndex = includedUnits.Count - backtrack;
                    if (backIndex >= 0)
                        overlapTokens += EstimateTokens(includedUnits[backIndex], language);
                }

                // Adjust index for overlap
                if (backtrack > 1 && index < units.Count)
                    index = Math.Max(startIndex + 1, index - (backtrack - 1));

                return new ChunkInfo
                {
                    Content = buffer.ToString().Trim(),
                    TokenCount = currentTokens,
                    Type = type
                };
            }

            /// <summary>
            /// Split text into sentences (multi-language support)
            /// </summary>
            private List<string> SplitIntoSentences(string text, string language)
            {
                var sentences = new List<string>();

                // Choose delimiters based on language
                string pattern = language switch
                {
                    "ar" => @"(?<=[\.،؛؟!؟\n])\s*",           // Arabic
                    "zh" => @"(?<=[。！？\n])\s*",              // Chinese
                    "ja" => @"(?<=[。！？\n])\s*",              // Japanese
                    _ => @"(?<=[.!?\n])\s+"                     // English/Latin
                };

                var parts = Regex.Split(text, pattern);

                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;

                    // Further split by newlines if needed
                    if (trimmed.Contains('\n') && trimmed.Length > 200)
                    {
                        var lines = trimmed.Split('\n');
                        sentences.AddRange(lines
                            .Select(l => l.Trim())
                            .Where(l => !string.IsNullOrWhiteSpace(l)));
                    }
                    else
                    {
                        sentences.Add(trimmed);
                    }
                }

                return sentences;
            }

            /// <summary>
            /// Split text into paragraphs
            /// </summary>
            private List<string> SplitIntoParagraphs(string text)
            {
                return text
                    .Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();
            }

            /// <summary>
            /// Detect semantic sections in text
            /// </summary>
            private List<Section> DetectSemanticSections(string text, string language)
            {
                var sections = new List<Section>();
                var lines = text.Split('\n');

                Section currentSection = null;
                var contentBuffer = new List<string>();

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;

                    var (isHeading, level) = DetectHeading(trimmed, language);

                    if (isHeading)
                    {
                        // Save previous section
                        if (currentSection != null && contentBuffer.Any())
                        {
                            currentSection.Content = string.Join("\n", contentBuffer);
                            sections.Add(currentSection);
                        }

                        // Start new section
                        currentSection = new Section
                        {
                            Title = trimmed,
                            Level = level
                        };
                        contentBuffer = new List<string>();
                    }
                    else
                    {
                        contentBuffer.Add(trimmed);
                    }
                }

                // Add final section
                if (currentSection != null && contentBuffer.Any())
                {
                    currentSection.Content = string.Join("\n", contentBuffer);
                    sections.Add(currentSection);
                }

                // If no sections found, treat entire text as one section
                if (!sections.Any())
                {
                    sections.Add(new Section
                    {
                        Title = "Document Content",
                        Level = 1,
                        Content = text
                    });
                }

                return sections;
            }

            /// <summary>
            /// Detect if a line is a heading
            /// </summary>
            private (bool IsHeading, int Level) DetectHeading(string line, string language)
            {
                // Too long or too short
                if (line.Length > 200 || line.Length < 5)
                    return (false, 0);

                // Markdown headers
                if (Regex.IsMatch(line, @"^#{1,6}\s"))
                {
                    int level = line.TakeWhile(c => c == '#').Count();
                    return (true, level);
                }

                // Numbered sections (1. 2. etc or 1) 2) etc)
                if (Regex.IsMatch(line, @"^\d+[\.\)]\s"))
                    return (true, 2);

                // All caps (English)
                if (language == "en" && line == line.ToUpper() && line.Length < 100)
                    return (true, 1);

                // Arabic headings
                if (language == "ar")
                {
                    var arabicHeadings = new[] {
                    "الباب", "الفصل", "المادة", "القسم", "الجزء",
                    "المقدمة", "الخاتمة", "أولاً", "ثانياً", "ثالثاً",
                    "قوائم", "قائمة", "نبذة"
                };

                    if (arabicHeadings.Any(h => line.StartsWith(h)))
                        return (true, 2);

                    if (line.EndsWith(":") && line.Length < 100)
                        return (true, 2);
                }

                // Ends with colon (common heading pattern)
                if (line.EndsWith(":") && line.Length < 100 && !line.Contains("http"))
                    return (true, 2);

                return (false, 0);
            }

            /// <summary>
            /// Detect primary language of text
            /// </summary>
            private string DetectLanguage(string text)
            {
                var sample = text.Length > 1000 ? text.Substring(0, 1000) : text;

                var arabicCount = Regex.Matches(sample, @"[\u0600-\u06FF]").Count;
                var chineseCount = Regex.Matches(sample, @"[\u4E00-\u9FFF]").Count;
                var japaneseCount = Regex.Matches(sample, @"[\u3040-\u309F\u30A0-\u30FF]").Count;

                var total = sample.Length;

                if (arabicCount > total * 0.3) return "ar";
                if (chineseCount > total * 0.3) return "zh";
                if (japaneseCount > total * 0.3) return "ja";

                return "en"; // Default to English
            }

            /// <summary>
            /// Normalize and clean text
            /// </summary>
            private string NormalizeText(string text)
            {
                // Remove control characters except newlines and tabs
                text = Regex.Replace(text, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");

                // Normalize whitespace
                text = Regex.Replace(text, @"[ \t]+", " ");
                text = Regex.Replace(text, @"\n{3,}", "\n\n");

                // Remove common extraction artifacts
                text = Regex.Replace(text, @"<<PAGE:\s*\d+>>", "", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"_page_\d+_", "", RegexOptions.IgnoreCase);

                return text.Trim();
            }
        private string CleanExtractedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            // 1. Remove JSON quotes if present
            text = Regex.Replace(text, @"^[""]|[""]$", "", RegexOptions.Multiline);

            // 2. Remove page markers
            text = Regex.Replace(text, @"<<PAGE:\s*\d+>>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"_page_\d+_", "", RegexOptions.IgnoreCase);

            // 3. Clean headers/footers (common in Kuwaiti PDFs)
            //text = Regex.Replace(text, @"(?m)^.*?(وزارة التجارة والصناعة|Ministry of Commerce & Industry|دولـــة الك ويــــ|State of Kuwait).*$\r?\n?", "", RegexOptions.IgnoreCase);

            // 4. Remove excessive decorative lines/dashes
            text = Regex.Replace(text, @"[-ـ_~]{10,}", ""); // Long dash lines
            text = Regex.Replace(text, @"^\s*[-ـ_~]+\s*$", "", RegexOptions.Multiline);

            // 5. Clean arrows and bullet symbols → standardize to "- "
            text = Regex.Replace(text, @"[◄►◆▼▲★▪▫•→←➔➜➤➢➡➔➙➛➝➞➟➠➢➣➤➥➦➧➨➩➪➫➬➭➮➯➱➲]+", "-");
            text = Regex.Replace(text, @"ightarrow|\$\\rightarrow\$|\\rightarrow|→", "→", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[-•➔➜➤➢➡➙➛➝➞➟➠➢➣➤➥➦➧➨➩➪➫➬➭➮➯➱➲→]+\s*", "- "); // Standardize bullets

            //// 6. Fix common OCR broken Arabic words (spaces inside words)
            //text = Regex.Replace(text, @"(\p{IsArabic})\s+(\p{IsArabic})", "$1$2"); // Join any two Arabic letters separated by space
            //text = Regex.Replace(text, @"(\p{IsArabic})\s+([اأإآؤئبتثجحخدذرزسشصضطظعغفقكلمنهويةى])\s+(\p{IsArabic})", "$1$2$3"); // More aggressive



            // 8. Clean LaTeX/math garbage
            text = Regex.Replace(text, @"\$\s*\\rightarrow\s*\$\s*ace\s*1\s*±\s*±\s*±\s*±", ""); // From your text
            text = Regex.Replace(text, @"\$.*?\$", ""); // Remove any $...$ math
            text = Regex.Replace(text, @"\\[a-zA-Z]+", ""); // Remove \commands

            // 9. Clean emojis and irrelevant icons
            text = Regex.Replace(text, @"[🍙🗣🕲]", ""); // Specific from your text

            //// 10. Normalize multiple spaces and tabs
            //text = Regex.Replace(text, @"[ \t]+", " ");

            // 11. Normalize newlines: max 2 consecutive, remove empty lines
            text = Regex.Replace(text, @"\r\n", "\n"); // Unify line endings
            text = Regex.Replace(text, @"\n{3,}", "\n\n"); // Max 2 newlines
            text = Regex.Replace(text, @"^\s*$\n", "", RegexOptions.Multiline); // Remove empty lines

            // 12. Final trim
            return text.Trim();
        }

        /// <summary>
        /// Estimate token count (multi-language aware)
        /// </summary>
        private int EstimateTokens(string text, string language)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return 0;

                double charsPerToken = language switch
                {
                    "ar" => 3.5,  // Arabic
                    "zh" => 1.5,  // Chinese
                    "ja" => 2.0,  // Japanese
                    "ko" => 2.5,  // Korean
                    _ => 4.0      // English/Latin scripts
                };

                return (int)Math.Ceiling(text.Length / charsPerToken);
            }

            #endregion

            #region Supporting Classes

            private class Section
            {
                public string Title { get; set; }
                public int Level { get; set; }
                public string Content { get; set; }
            }

            public class ChunkInfo
            {
                public string ChunkId { get; set; }
                public string Content { get; set; }
                public int TokenCount { get; set; }
                public string Type { get; set; }
                public string DocumentTitle { get; set; }
                public string Language { get; set; }
                public string SectionTitle { get; set; }
                public int SectionLevel { get; set; }
                public int ChunkIndex { get; set; }
                public int TotalChunks { get; set; }
                public Dictionary<string, object> Metadata { get; set; }

                public override string ToString()
                {
                    return $"[{ChunkId}] {Type} - {TokenCount} tokens - {Language}";
                }
            }

            #endregion
        }
    }

