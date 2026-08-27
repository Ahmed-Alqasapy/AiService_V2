using AiService.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AiService.Models
{
    public class ChunkingOptions_NEW
    {
        // Core chunking parameters
        public double SemanticBreakThreshold { get; set; } = 0.65; //0.72
        public int MinLinesPerChunk { get; set; } = 30; //15
        public int MaxLinesPerChunk { get; set; } = 120; // 80
        public int ForceBreakEveryLines { get; set; } = 200; // 120
        public int OverlapSentences { get; set; } = 3; // Increased from 2
        public int VectorSize { get; set; } = 1024;
        public int SemanticCheckInterval { get; set; } = 40;  //20
        public int MinSemanticCheckLines { get; set; } = 50;  //25
        public int BatchSize { get; set; } = 50;
        public bool EnableDeduplication { get; set; } = false;
        public double DuplicationThreshold { get; set; } = 0.92;
        public int MinChunkLength { get; set; } = 200; // Reduced from 300


        // Structure preservation
        public bool PreserveListItems { get; set; } = true;
        public int MaxTableLines { get; set; } = 80;
        public bool KeepRelatedContent { get; set; } = true;
        public int MinTableRows { get; set; } = 1;

        // Filtering controls
        public bool RemoveFooters { get; set; } = false;
        public bool RemoveMetadata { get; set; } = false;
        public bool CleanImageReferences { get; set; } = false;
        public bool FilterShortLines { get; set; } = false;
        public int MinLineLength { get; set; } = 1;

        // NEW: Enhanced context preservation
        public bool StrictMinimums { get; set; } = false;
        public bool PreserveOrphanedContent { get; set; } = true; // NEW
        public int ContextWindowLines { get; set; } = 5; // NEW: Lines before/after for context
        public bool SmartSentenceBoundary { get; set; } = true; // NEW: Break at sentence boundaries
        public int MinOrphanLines { get; set; } = 3; // NEW: Min lines to preserve as separate chunk
        public bool MergeSmallChunks { get; set; } = true; // NEW: Merge chunks that are too small
        public double MaxChunkOverlapRatio { get; set; } = 0.15; // NEW: Max 15% overlap between chunks
    }

    public class Chunk_NEW
    {

        public string file_name { get; set; }
        public string Text { get; set; }
        public int PageNumber { get; set; }
        public string Section { get; set; }
        public int ApproxTokens { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string ChunkId { get; set; }
        public string ChunkType { get; set; }
        public string SubSection { get; set; }
        public int HierarchyLevel { get; set; }

        // NEW: Enhanced metadata for context tracking
        public string PreviousChunkId { get; set; }
        public string NextChunkId { get; set; }
        public bool HasOverlapWithPrevious { get; set; }
        public bool HasOverlapWithNext { get; set; }
        public List<string> KeyPhrases { get; set; } = new List<string>();
        public int CharacterCount { get; set; }

        public void Deconstruct(out string text, out int pageNumber, out string section, out int approxTokens)
        {
            text = Text;
            pageNumber = PageNumber;
            section = Section;
            approxTokens = ApproxTokens;
        }
    }

    public class ImprovedSemanticChunker
    {
        private readonly HttpClient _ollamaClient;
        private readonly ILogger _logger;
        private readonly ChunkingOptions_NEW _options;
        private readonly Dictionary<string, float[]> _embeddingCache;
        private readonly HashSet<string> _seenChunkHashes;
        private readonly List<string> _preservedOrphans; // NEW: Track orphaned content

        public ImprovedSemanticChunker(
            HttpClient ollamaClient,
            ILogger<ImprovedSemanticChunker> logger,
            IHttpClientFactory factory,
            ChunkingOptions_NEW options = null)
        {
            _ollamaClient = factory.CreateClient();
            _ollamaClient.BaseAddress = new Uri("http://10.21.2.143:8000/");
            _logger = logger;
            _options = options ?? new ChunkingOptions_NEW();
            _embeddingCache = new Dictionary<string, float[]>();
            _seenChunkHashes = new HashSet<string>();
            _preservedOrphans = new List<string>();
        }

        public async Task<IEnumerable<Chunk_NEW>> SemanticChunkAsync(
            string fullText,
            int startPage = 1)
        {
            List<Chunk_NEW> chunks = new List<Chunk_NEW>();
            if (string.IsNullOrWhiteSpace(fullText))
            {
                _logger.LogWarning("Empty input text provided");
                return chunks;
            }

            // Store original text for validation
            var originalText = fullText;
            var originalLength = fullText.Length;

            fullText = PreCleanText(fullText);
            var lines = PreprocessLines(fullText);

            if (!lines.Any())
            {
                _logger.LogWarning("No valid lines found after preprocessing, returning full text");
                return new List<Chunk_NEW> { CreateFallbackChunk(originalText, startPage) };
            }

            _logger.LogInformation($"Starting chunking: {lines.Length} lines, {originalLength} chars");

            var bufferLines = new List<string>();
            int currentPage = startPage;
            int currentLineNumber = 0;
            int bufferStartLine = 0;
            string currentSection = "مقدمة";
            string currentSubSection = "";
            int currentHierarchyLevel = 0;
            string currentChunkType = "text";

            bool insideTable = false;
            int tableLineCount = 0;
            bool insideList = false;
            int consecutiveListItems = 0;

            foreach (var rawLine in lines)
            {
                currentLineNumber++;
                var line = rawLine;

                // Handle page markers
                if (HandlePageMarker(line, ref currentPage, bufferLines, ref bufferStartLine,
                    currentSection, chunks, currentLineNumber, currentChunkType, currentSubSection, currentHierarchyLevel))
                    continue;

                if (_options.RemoveFooters && IsFooterOrNoise(line))
                    continue;

                // Detect sections
                var (isSection, level, cleanedTitle) = DetectSectionWithLevel(line);

                if (isSection)
                {
                    // More aggressive flushing before sections to preserve context
                    if (bufferLines.Count >= Math.Max(1, _options.MinLinesPerChunk / 2)) // was 4
                    {
                        await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                            bufferStartLine, currentLineNumber - 1, "SECTION_START", currentChunkType,
                            currentSubSection, currentHierarchyLevel);
                        bufferStartLine = currentLineNumber;
                    }

                    if (level == 1)
                    {
                        currentSection = cleanedTitle;
                        currentSubSection = "";
                    }
                    else
                    {
                        currentSubSection = cleanedTitle;
                    }

                    currentHierarchyLevel = level;
                    bufferLines.Add(line);
                    currentChunkType = "text";
                    insideTable = false;
                    insideList = false;
                    continue;
                }

                // Table detection
                if (IsTableLine(line))
                {
                    if (!insideTable)
                    {
                        if (bufferLines.Count > Math.Max(1, _options.MinLinesPerChunk / 4) && currentChunkType != "table")
                        {
                            await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                                bufferStartLine, currentLineNumber - 1, "PRE_TABLE", currentChunkType,
                                currentSubSection, currentHierarchyLevel);
                            bufferStartLine = currentLineNumber;
                        }
                        insideTable = true;
                        tableLineCount = 0;
                        currentChunkType = "table";
                    }

                    tableLineCount++;
                    bufferLines.Add(line);

                    if (tableLineCount >= _options.MaxTableLines)
                    {
                        await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                            bufferStartLine, currentLineNumber, "TABLE_MAX", "table",
                            currentSubSection, currentHierarchyLevel);
                        bufferStartLine = currentLineNumber + 1;
                        insideTable = false;
                        tableLineCount = 0;
                        currentChunkType = "text";
                    }
                    continue;
                }
                else if (insideTable)
                {
                    // Always preserve table content
                    if (bufferLines.Count >= _options.MinTableRows)
                    {
                        await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                            bufferStartLine, currentLineNumber - 1, "TABLE_END", "table",
                            currentSubSection, currentHierarchyLevel);
                        bufferStartLine = currentLineNumber;
                    }
                    insideTable = false;
                    tableLineCount = 0;
                    currentChunkType = "text";
                }

                // List handling
                if (IsListItem(line))
                {
                    if (!insideList && currentChunkType != "list")
                    {
                        if (bufferLines.Count > Math.Max(1, _options.MinLinesPerChunk / 4))
                        {
                            await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                                bufferStartLine, currentLineNumber - 1, "PRE_LIST", currentChunkType,
                                currentSubSection, currentHierarchyLevel);
                            bufferStartLine = currentLineNumber;
                        }
                        insideList = true;
                        consecutiveListItems = 0;
                        currentChunkType = "list";
                    }

                    consecutiveListItems++;
                    bufferLines.Add(line);
                    continue;
                }
                else if (insideList)
                {
                    if (IsContinuationLine(line) && bufferLines.Any())
                    {
                        bufferLines.Add(line);
                        continue;
                    }

                    // Preserve list content
                    if (consecutiveListItems >= 1 && bufferLines.Count >= Math.Max(1, _options.MinLinesPerChunk / 4))
                    {
                        await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                            bufferStartLine, currentLineNumber - 1, "LIST_END", "list",
                            currentSubSection, currentHierarchyLevel);
                        bufferStartLine = currentLineNumber;
                    }
                    insideList = false;
                    consecutiveListItems = 0;
                    currentChunkType = "text";
                }

                // Add regular text line
                bufferLines.Add(line);

                // Check for max lines
                if (bufferLines.Count >= _options.MaxLinesPerChunk)
                {
                    // Smart boundary breaking
                    int breakPoint = _options.SmartSentenceBoundary
                        ? FindSmartBreakPoint(bufferLines)
                        : bufferLines.Count;

                    await FlushBufferAsync(bufferLines.Take(breakPoint).ToList(), currentPage, currentSection, chunks,
                        bufferStartLine, currentLineNumber, "MAX_LINES", currentChunkType,
                        currentSubSection, currentHierarchyLevel);

                    // Preserve remaining lines with context
                    var remaining = bufferLines.Skip(breakPoint).ToList();
                    bufferLines.Clear();
                    bufferLines.AddRange(remaining);
                    bufferStartLine = currentLineNumber - remaining.Count + 1;
                    continue;
                }

                // Force break
                if (bufferLines.Count >= _options.ForceBreakEveryLines)
                {
                    int breakPoint = _options.SmartSentenceBoundary
                        ? FindSmartBreakPoint(bufferLines)
                        : bufferLines.Count;

                    await FlushBufferAsync(bufferLines.Take(breakPoint).ToList(), currentPage, currentSection, chunks,
                        bufferStartLine, currentLineNumber, "FORCE_BREAK", currentChunkType,
                        currentSubSection, currentHierarchyLevel);

                    var remaining = bufferLines.Skip(breakPoint).ToList();
                    bufferLines.Clear();
                    bufferLines.AddRange(remaining);
                    bufferStartLine = currentLineNumber - remaining.Count + 1;
                    continue;
                }

                // Semantic check for text chunks only
                if (currentChunkType == "text" && !insideTable && !insideList &&
                    bufferLines.Count >= _options.MinSemanticCheckLines &&
                    bufferLines.Count % _options.SemanticCheckInterval == 0)
                {
                    var shouldBreak = await CheckSemanticBreakAsync(bufferLines);
                    if (shouldBreak)
                    {
                        await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                            bufferStartLine, currentLineNumber, "SEMANTIC", "text",
                            currentSubSection, currentHierarchyLevel);
                        bufferStartLine = currentLineNumber + 1;
                    }
                }
            }

            // CRITICAL: Always preserve remaining content
            if (bufferLines.Count > 0)
            {
                _logger.LogInformation($"Flushing final buffer with {bufferLines.Count} lines");
                await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                    bufferStartLine, currentLineNumber, "FINAL", currentChunkType,
                    currentSubSection, currentHierarchyLevel);
            }

            // Add any orphaned content
            if (_preservedOrphans.Any())
            {
                _logger.LogInformation($"Processing {_preservedOrphans.Count} orphaned content pieces");
                foreach (var orphan in _preservedOrphans)
                {
                    if (!string.IsNullOrWhiteSpace(orphan))
                    {
                        chunks.Add(CreateOrphanChunk(orphan, currentPage, currentSection));
                    }
                }
            }

            _logger.LogInformation($"Initial chunking complete: {chunks.Count} chunks");

            #region Old Parts Of the Chunk Code
            //// Post-processing with context preservation
            //chunks = PostProcessChunksEnhanced(chunks);

            //// Link chunks for context continuity
            //LinkChunks(chunks); 
            #endregion


            // Post-processing with context preservation
            chunks = PostProcessChunksEnhanced(chunks);

            // NEW: Normalize chunk sizes for reranker (max 800 chars)
            //   chunks = NormalizeChunkSizesForReranker(chunks, maxSize: 6500, overlap: 100);

            chunks = NormalizeChunkSizesForReranker(chunks, maxSize: 2000, overlap: 100);


            // Link chunks for context continuity (AFTER normalization)
            LinkChunks(chunks);

            if (_options.EnableDeduplication)
            {
                var beforeDedup = chunks.Count;
                chunks = await DeduplicateChunksAsync(chunks);
                if (beforeDedup != chunks.Count)
                {
                    _logger.LogInformation($"Removed {beforeDedup - chunks.Count} duplicate chunks");
                }
            }

            // Validate no content was lost
            ValidateContentPreservation(originalText, chunks);

            _logger.LogInformation($"Final result: {chunks.Count} chunks");

            if (!chunks.Any())
            {
                _logger.LogWarning("No chunks created, returning full text");
                return new List<Chunk_NEW> { CreateFallbackChunk(originalText, startPage) };
            }

            return chunks;
        }

        // NEW: Find smart break point at sentence boundary
        private int FindSmartBreakPoint(List<string> lines)
        {
            if (lines.Count <= 5) return lines.Count;

            // Look in last 10 lines for a good break point
            int searchStart = Math.Max(0, lines.Count - 10);
            var sentenceEnders = new[] { '.', '!', '?', '۔', '؛', ';' };

            for (int i = lines.Count - 1; i >= searchStart; i--)
            {
                var line = lines[i];
                if (sentenceEnders.Any(c => line.TrimEnd().EndsWith(c)))
                {
                    return i + 1;
                }
            }

            // No good break point found, return full count
            return lines.Count;
        }

        // NEW: Enhanced post-processing that preserves all content
        private List<Chunk_NEW> PostProcessChunksEnhanced(List<Chunk_NEW> chunks)
        {
            if (!_options.MergeSmallChunks) return chunks;

            var processed = new List<Chunk_NEW>();
            Chunk_NEW pendingChunk = null;

            foreach (var chunk in chunks)
            {
                int minAcceptable = _options.StrictMinimums
                    ? _options.MinChunkLength
                    : Math.Max(50, _options.MinChunkLength / 3);

                if (chunk.Text.Length < minAcceptable)
                {
                    if (pendingChunk == null)
                    {
                        pendingChunk = chunk;
                    }
                    else
                    {
                        // Try to merge with previous chunk
                        if (CanMergeChunks(pendingChunk, chunk))
                        {
                            pendingChunk = MergeChunks(pendingChunk, chunk);

                            // If merged chunk is now big enough, add it
                            if (pendingChunk.Text.Length >= minAcceptable)
                            {
                                processed.Add(pendingChunk);
                                pendingChunk = null;
                            }
                        }
                        else
                        {
                            // Can't merge, add pending if it has minimum content
                            if (pendingChunk.Text.Length >= 20 || _options.PreserveOrphanedContent)
                            {
                                processed.Add(pendingChunk);
                            }
                            pendingChunk = chunk;
                        }
                    }
                }
                else
                {
                    // Current chunk is large enough
                    if (pendingChunk != null)
                    {
                        // Add pending chunk (preserve orphaned content)
                        if (pendingChunk.Text.Length >= 20 || _options.PreserveOrphanedContent)
                        {
                            processed.Add(pendingChunk);
                        }
                        pendingChunk = null;
                    }

                    processed.Add(chunk);
                }
            }

            // CRITICAL: Always add remaining pending chunk
            if (pendingChunk != null && (_options.PreserveOrphanedContent || pendingChunk.Text.Length >= 20))
            {
                _logger.LogInformation($"Preserving final pending chunk: {pendingChunk.Text.Length} chars");
                processed.Add(pendingChunk);
            }

            return processed;
        }

        // NEW: Check if two chunks can be merged
        private bool CanMergeChunks(Chunk_NEW chunk1, Chunk_NEW chunk2)
        {
            // Merge if same type and section
            if (chunk1.ChunkType == chunk2.ChunkType &&
                chunk1.Section == chunk2.Section &&
                Math.Abs(chunk1.PageNumber - chunk2.PageNumber) <= 1)
            {
                return true;
            }

            // Also merge if chunks are on same page and consecutive
            if (chunk1.PageNumber == chunk2.PageNumber &&
                chunk2.StartLine - chunk1.EndLine <= 3)
            {
                return true;
            }

            return false;
        }

        // NEW: Merge two chunks preserving metadata
        private Chunk_NEW MergeChunks(Chunk_NEW chunk1, Chunk_NEW chunk2)
        {
            return new Chunk_NEW
            {
                Text = chunk1.Text + "\n\n" + chunk2.Text,
                PageNumber = chunk1.PageNumber,
                Section = chunk1.Section,
                SubSection = string.IsNullOrEmpty(chunk1.SubSection) ? chunk2.SubSection : chunk1.SubSection,
                HierarchyLevel = chunk1.HierarchyLevel,
                ApproxTokens = EstimateTokens(chunk1.Text + "\n\n" + chunk2.Text),
                StartLine = chunk1.StartLine,
                EndLine = chunk2.EndLine,
                ChunkId = $"merged_{chunk1.ChunkId}_{chunk2.ChunkId}",
                ChunkType = chunk1.ChunkType,
                CharacterCount = chunk1.Text.Length + chunk2.Text.Length + 2
            };
        }

        // NEW: Link chunks for context continuity
        private void LinkChunks(List<Chunk_NEW> chunks)
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                if (i > 0)
                {
                    chunks[i].PreviousChunkId = chunks[i - 1].ChunkId;
                    chunks[i].HasOverlapWithPrevious = CheckOverlap(chunks[i - 1].Text, chunks[i].Text);
                }

                if (i < chunks.Count - 1)
                {
                    chunks[i].NextChunkId = chunks[i + 1].ChunkId;
                    chunks[i].HasOverlapWithNext = CheckOverlap(chunks[i].Text, chunks[i + 1].Text);
                }

                chunks[i].CharacterCount = chunks[i].Text.Length;
                chunks[i].KeyPhrases = ExtractKeyPhrases(chunks[i].Text);
            }
        }

        // NEW: Check if there's overlap between chunks
        private bool CheckOverlap(string text1, string text2)
        {
            if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2))
                return false;

            // Check if last 50 chars of text1 appear in first 100 chars of text2
            int checkLength = Math.Min(50, text1.Length);
            string endOfFirst = text1.Substring(text1.Length - checkLength);
            string startOfSecond = text2.Substring(0, Math.Min(100, text2.Length));

            return startOfSecond.Contains(endOfFirst);
        }

        // NEW: Extract key phrases for better context understanding
        private List<string> ExtractKeyPhrases(string text)
        {
            var phrases = new List<string>();

            // Extract first sentence
            var firstSentence = text.Split(new[] { '.', '!', '?', '۔', '؛' }, 2)[0].Trim();
            if (firstSentence.Length > 10)
                phrases.Add(firstSentence);

            // Extract section markers
            var sectionMarkers = Regex.Matches(text, @"(باب |الفصل |فصل |أولا|ثانيا|ثالثا).{0,50}");
            foreach (Match match in sectionMarkers)
            {
                phrases.Add(match.Value.Trim());
            }

            return phrases.Take(3).ToList();
        }

        // NEW: Validate that no content was lost
        private void ValidateContentPreservation(string originalText, List<Chunk_NEW> chunks)
        {
            var originalWords = Regex.Matches(originalText, @"\w+").Count;
            var chunkedWords = chunks.Sum(c => Regex.Matches(c.Text, @"\w+").Count);

            double preservationRatio = (double)chunkedWords / originalWords;

            _logger.LogInformation(
                $"Content preservation: {preservationRatio:P2} " +
                $"(Original: {originalWords} words, Chunked: {chunkedWords} words)");

            if (preservationRatio < 0.95)
            {
                _logger.LogWarning(
                    $"Potential content loss detected! Only {preservationRatio:P2} of content preserved. " +
                    $"Lost approximately {originalWords - chunkedWords} words.");
            }
        }

        // NEW: Create orphan chunk for preserved content
        private Chunk_NEW CreateOrphanChunk(string text, int page, string section)
        {
            return new Chunk_NEW
            {
                Text = text.Trim(),
                PageNumber = page,
                Section = section,
                SubSection = "محتوى إضافي",
                HierarchyLevel = 0,
                ApproxTokens = EstimateTokens(text),
                StartLine = 0,
                EndLine = 0,
                ChunkId = $"orphan_{Guid.NewGuid():N}"[..24],
                ChunkType = "orphan",
                CharacterCount = text.Length
            };
        }

        private (bool isSection, int level, string cleanedTitle) DetectSectionWithLevel(string line)
        {
            if (line.Length <= 12 || line.Length >= 250)
                return (false, 0, "");

            var hashMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)");
            if (hashMatch.Success)
            {
                int level = hashMatch.Groups[1].Value.Length;
                string title = hashMatch.Groups[2].Value.Trim();
                return (true, level, title);
            }

            var arabicChars = line.Count(c => c >= '\u0600' && c <= '\u06FF');
            if (arabicChars < line.Length * 0.5)
                return (false, 0, "");

            if (IsListItem(line))
                return (false, 0, "");

            var specialChars = line.Count(c => char.IsDigit(c) || c == '|' || c == '-');
            if (specialChars > line.Length * 0.3)
                return (false, 0, "");

            var excludeKeywords = new[]
            {
                "استعلام", "اختيار", "إضافة", "تقديم", "ادخال",
                "الكترونيا", "د.ك", "يدوي", "الرسوم", "نوع الخدمة"
            };

            if (excludeKeywords.Any(k => line.Contains(k)))
                return (false, 0, "");

            if (line.StartsWith("باب ") || line.Contains("الباب"))
                return (true, 1, CleanSectionTitle(line));

            if (line.StartsWith("الفصل ") || line.StartsWith("فصل "))
                return (true, 2, CleanSectionTitle(line));

            if (Regex.IsMatch(line, @"^(أولا|ثانيا|ثالثا|رابعا|خامسا|سادسا|سابعا|ثامنا|تاسعا)[:\s]", RegexOptions.IgnoreCase))
                return (true, 2, CleanSectionTitle(line));

            return (false, 0, "");
        }

        private bool IsContinuationLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (line.Length > 0 && char.IsLower(line[0])) return true;
            if (line.StartsWith("و") || line.StartsWith("أو") || line.StartsWith("من"))
                return true;
            return false;
        }

        private string RemoveMetadataHeader(string text)
        {
            if (!_options.RemoveMetadata)
                return text;

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var cleanLines = new List<string>();
            bool inMetadataBlock = false;
            bool metadataBlockEnded = false;
            int lineIndex = 0;

            foreach (var line in lines)
            {
                lineIndex++;

                if (lineIndex < 10 && string.IsNullOrWhiteSpace(line))
                    continue;

                if (!metadataBlockEnded &&
                    (line.Contains("![](_page_") ||
                     line.StartsWith("**Source:**") ||
                     line.StartsWith("# ![](")))
                {
                    inMetadataBlock = true;
                    continue;
                }

                if (inMetadataBlock)
                {
                    if (line.StartsWith("**Source:**") ||
                        line.StartsWith("**Language:**") ||
                        line.StartsWith("**Pages:**") ||
                        line.StartsWith("**Extracted:**") ||
                        line.Trim() == "---" ||
                        line.Trim().StartsWith("===") ||
                        string.IsNullOrWhiteSpace(line))
                    {
                        if (line.Trim() == "---" || line.Trim().StartsWith("==="))
                        {
                            inMetadataBlock = false;
                            metadataBlockEnded = true;
                        }
                        continue;
                    }
                    else
                    {
                        inMetadataBlock = false;
                        metadataBlockEnded = true;
                    }
                }

                if (line.Trim() == "## Introduction" || line.Trim() == "## مقدمة")
                {
                    if (lineIndex < lines.Length - 1 && lines[lineIndex].Contains("![](_page_"))
                        continue;
                }

                if (_options.CleanImageReferences &&
                    (line.Contains("![](_page_") || line.Contains("Picture_")))
                    continue;

                if (metadataBlockEnded || lineIndex > 15)
                {
                    cleanLines.Add(line);
                }
            }

            return string.Join("\n", cleanLines);
        }

        private string PreCleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            text = RemoveMetadataHeader(text);

            if (_options.CleanImageReferences)
            {
                text = Regex.Replace(text, @"!\[\]\([^)]*\)", "", RegexOptions.Multiline);
                text = Regex.Replace(text, @"^!\[\].*?$", "", RegexOptions.Multiline);
            }

            // Clean LaTeX/math artifacts
            text = Regex.Replace(text, @"ightarrow", "→", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\$\s*\\rightarrow\s*\$\s*ace\s*1\s*±\s*±\s*±\s*±", "");
            text = Regex.Replace(text, @"\$.*?\$", "");
            text = Regex.Replace(text, @"\\\[.*?\\\]", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\\[a-zA-Z]+(\{[^\}]*\})?", "");

            // Clean stray arrows and symbols
            text = Regex.Replace(text, @"[➔➜➤➢➡➙➛➝➞➟➠➢➣➤➥➦➧➨➩➪➫➬➭➮➯➱➲]+", "→");

            // Basic structural cleanup
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            if (_options.CleanImageReferences)
            {
                text = Regex.Replace(text, @"^\s*[►◄▪•→←➔➜➤➢➡➙➛➝➞➟➠➢➣➤➥➦➧➨➩➪➫➬➭➮➯➱➲]\s*$", "", RegexOptions.Multiline);
                text = Regex.Replace(text, @"^[-]{3,}$", "", RegexOptions.Multiline);
            }

            if (HasArabicSpacingIssue(text))
            {
                _logger.LogWarning("Detected Arabic spacing issues, applying fixes");
                text = FixArabicSpacing(text);
            }

            return text.Trim();
        }

        private bool HasArabicSpacingIssue(string text)
        {
            var arabicChars = text.Count(c => c >= '\u0600' && c <= '\u06FF');
            var spaces = text.Count(c => c == ' ');

            if (arabicChars < 100) return false;

            return (spaces / (double)arabicChars) < 0.05;
        }

        private string FixArabicSpacing(string text)
        {
            text = Regex.Replace(text, @"([\u0600-\u06FF]{2,})(ال[\u0600-\u06FF])", "$1 $2");
            text = Regex.Replace(text, @"([\u0600-\u06FF]{2,})(و[\u0600-\u06FF]{2,})", "$1 $2");
            text = Regex.Replace(text, @"([\u0600-\u06FF]{2,})(ب[\u0600-\u06FF]{2,})", "$1 $2");
            text = Regex.Replace(text, @"([\u0600-\u06FF]{2,})(ل[\u0600-\u06FF]{2,})", "$1 $2");
            text = Regex.Replace(text, @"([\u0600-\u06FF]{2,})(ف[\u0600-\u06FF]{2,})", "$1 $2");
            text = Regex.Replace(text, @"([.،؛:])([^\s\n])", "$1 $2");

            return text;
        }

        private string[] PreprocessLines(string fullText)
        {
            var lines = fullText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Select(line =>
                {
                    if ((line.StartsWith("\"") && line.EndsWith("\"")) ||
                        (line.StartsWith("'") && line.EndsWith("'")))
                        line = line[1..^1];

                    if (_options.CleanImageReferences)
                    {
                        line = Regex.Replace(line, @"!\[\]\([^)]+\)", "").Trim();
                    }

                    return line.Trim();
                })
                .Where(l => !string.IsNullOrWhiteSpace(l));

            if (_options.FilterShortLines)
            {
                lines = lines.Where(l => l.Length >= _options.MinLineLength);
            }

            return lines.ToArray();
        }

        private bool HandlePageMarker(string line, ref int currentPage, List<string> bufferLines,
            ref int bufferStartLine, string currentSection, List<Chunk_NEW> chunks,
            int currentLineNumber, string chunkType, string subSection, int hierarchyLevel)
        {
            var isPageMarker = line.Contains("<<PAGE:") ||
                               line.Contains("--- Page ") ||
                               line.Contains("--- صفحة ");

            if (!isPageMarker) return false;

            if (bufferLines.Count > 0)
            {
                FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                    bufferStartLine, currentLineNumber - 1, "PAGE_BREAK", chunkType,
                    subSection, hierarchyLevel).Wait();
                bufferStartLine = currentLineNumber;
            }

            var match = Regex.Match(line, @"\d+");
            if (match.Success && int.TryParse(match.Value, out int pageNum))
            {
                currentPage = pageNum;
                _logger.LogDebug($"Page marker detected: Page {pageNum}");
            }

            return true;
        }

        private bool IsFooterOrNoise(string line)
        {
            if (!_options.RemoveFooters)
                return false;

            if (line.Length >= 200) return false;

            var simpleMatches = new[]
            {
                "وزارة التجارة",
                "دولة الكويت",
                "State of Kuwait",
                "Ministry of Commerce",
                "Picture_"
            };

            if (simpleMatches.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (line.Contains("![](_page_"))
                return true;

            var regexPatterns = new[]
            {
                @"صفحة\s*\d+",
                @"Page\s*\d+",
                @"^\d+$"
            };

            return regexPatterns.Any(pattern =>
                Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase));
        }

        private bool IsTableLine(string line)
        {
            var pipeCount = line.Count(c => c == '|');
            var dashCount = line.Count(c => c == '-');

            if (pipeCount >= 3) return true;
            if (dashCount >= 10 && line.Length < 100) return true;
            if (Regex.IsMatch(line, @"^[\|\-\s]+$")) return true;

            return false;
        }

        private bool IsListItem(string line)
        {
            if (line.Length > 200) return false;

            return line.TrimStart().StartsWith("•") ||
                   line.TrimStart().StartsWith("*") ||
                   line.TrimStart().StartsWith("-") ||
                   line.TrimStart().StartsWith("▪") ||
                   line.TrimStart().StartsWith("◄") ||
                   line.TrimStart().StartsWith("►") ||
                   line.TrimStart().StartsWith("→") ||
                   Regex.IsMatch(line, @"^\d+[\.\-\)]\s") ||
                   Regex.IsMatch(line, @"^[أ-ي][\.\-\)]\s");
        }

        private bool IsSectionTitle(string line)
        {
            var (isSection, _, _) = DetectSectionWithLevel(line);
            return isSection;
        }

        private string CleanSectionTitle(string title)
        {
            title = Regex.Replace(title, @"^#+\s*", "");
            title = Regex.Replace(title, @"[:\-]+$", "");
            return title.Trim();
        }

        private async Task FlushBufferAsync(
            List<string> bufferLines,
            int currentPage,
            string currentSection,
            List<Chunk_NEW> chunks,
            int startLine,
            int endLine,
            string reason,
            string chunkType,
            string subSection = "",
            int hierarchyLevel = 0)
        {
            // NEVER discard content - always preserve
            if (bufferLines.Count == 0) return;

            var text = string.Join("\n", bufferLines).Trim();

            if (string.IsNullOrWhiteSpace(text)) return;

            // For very small content, add to orphans instead of discarding
            int absoluteMinimum = 10; // Absolute minimum - anything less is noise
            if (text.Length < absoluteMinimum)
            {
                _logger.LogDebug($"Content too small ({text.Length} chars), adding to orphans");
                if (_options.PreserveOrphanedContent)
                {
                    _preservedOrphans.Add(text);
                }
                bufferLines.Clear();
                return;
            }

            var hash = ComputeSimpleHash(text);
            if (_seenChunkHashes.Contains(hash))
            {
                _logger.LogDebug("Duplicate chunk detected (hash match), skipping");
                bufferLines.Clear();
                return;
            }

            var chunk = new Chunk_NEW
            {
                Text = text,
                PageNumber = currentPage,
                Section = currentSection,
                SubSection = subSection,
                HierarchyLevel = hierarchyLevel,
                ApproxTokens = EstimateTokens(text),
                StartLine = startLine,
                EndLine = endLine,
                ChunkId = $"chunk_{chunks.Count + 1}_{Guid.NewGuid():N}"[..32],
                ChunkType = chunkType,
                CharacterCount = text.Length
            };

            chunks.Add(chunk);
            _seenChunkHashes.Add(hash);

            var sectionPreview = currentSection.Length > 35
                ? currentSection[..35] + "..."
                : currentSection;

            _logger.LogInformation(
                $"Chunk #{chunks.Count} [{chunkType}] ({reason}): {bufferLines.Count} lines, " +
                $"page {currentPage}, section: {sectionPreview}, {chunk.ApproxTokens} tokens, {text.Length} chars");

            var overlapLines = CreateEnhancedOverlap(text);
            bufferLines.Clear();
            if (overlapLines.Any() && chunkType == "text")
                bufferLines.AddRange(overlapLines);
        }

        private string ComputeSimpleHash(string text)
        {
            var normalized = Regex.Replace(text, @"\s+", " ").Trim().ToLower();
            var prefix = normalized.Length > 50 ? normalized.Substring(0, 50) : normalized;
            var suffix = normalized.Length > 100 ? normalized.Substring(normalized.Length - 50) : "";

            return $"{normalized.Length}_{prefix.GetHashCode()}_{suffix.GetHashCode()}";
        }

        // ENHANCED: Better overlap with more context
        private List<string> CreateEnhancedOverlap(string text)
        {
            // Get last 3-5 sentences for better context
            var sentences = text
                .Split(new[] { '.', '!', '?', '۔', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 15)
                .TakeLast(_options.OverlapSentences)
                .ToList();

            if (!sentences.Any()) return new List<string>();

            var overlap = string.Join(". ", sentences);

            // Ensure minimum character overlap
            int minOverlapChars = 150;
            if (overlap.Length < minOverlapChars && text.Length >= minOverlapChars)
            {
                // Take last N characters instead
                overlap = text.Substring(text.Length - minOverlapChars);
            }

            // Cap maximum overlap
            if (overlap.Length > 300)
            {
                var words = overlap.Split(' ');
                overlap = string.Join(" ", words.TakeLast(40));
            }

            _logger.LogDebug($"Created overlap: {overlap.Length} chars");

            return new List<string> { overlap };
        }

        private int EstimateTokens(string text)
        {
            var arabicChars = text.Count(c => c >= '\u0600' && c <= '\u06FF');
            var englishChars = text.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
            var digits = text.Count(char.IsDigit);

            var tokens = (arabicChars / 2.5) + (englishChars / 4.0) + (digits / 1.5);

            return Math.Max(50, (int)tokens + 30);
        }

        private async Task<List<Chunk_NEW>> DeduplicateChunksAsync(List<Chunk_NEW> chunks)
        {
            if (chunks.Count < 2 || !_options.EnableDeduplication)
                return chunks;

            var deduplicated = new List<Chunk_NEW>();
            var seenEmbeddings = new List<(float[] embedding, Chunk_NEW chunk)>();

            _logger.LogInformation("Running semantic deduplication...");

            foreach (var chunk in chunks)
            {
                var embeddings = await GetEmbeddingsBatchAsync(new[] { chunk.Text });
                if (embeddings == null || embeddings.Length == 0)
                {
                    deduplicated.Add(chunk);
                    continue;
                }

                var currentEmbedding = embeddings[0];
                bool isDuplicate = false;

                foreach (var (seenEmbedding, seenChunk) in seenEmbeddings)
                {
                    var similarity = CosineSimilarity(currentEmbedding, seenEmbedding);

                    if (similarity > _options.DuplicationThreshold)
                    {
                        _logger.LogDebug(
                            $"Duplicate: page {chunk.PageNumber} similar to page {seenChunk.PageNumber} " +
                            $"(similarity: {similarity:F3})");
                        isDuplicate = true;
                        break;
                    }
                }

                if (!isDuplicate)
                {
                    deduplicated.Add(chunk);
                    seenEmbeddings.Add((currentEmbedding, chunk));
                }
            }

            return deduplicated;
        }

        private async Task<bool> CheckSemanticBreakAsync(List<string> bufferLines)
        {
            try
            {
                if (bufferLines.Count < _options.MinSemanticCheckLines)
                    return false;

                var embeddings = await GetEmbeddingsBatchAsync(bufferLines);
                if (embeddings == null || embeddings.Length < 10) return false;

                var recent = embeddings.TakeLast(5).ToArray();
                var previous = embeddings.SkipLast(5).TakeLast(5).ToArray();

                if (previous.Length < 5 || recent.Length < 5) return false;

                var recentAvg = ComputeAverage(recent);
                var previousAvg = ComputeAverage(previous);

                float similarity = CosineSimilarity(recentAvg, previousAvg);

                _logger.LogDebug($"Semantic check: {similarity:F3} (threshold: {_options.SemanticBreakThreshold})");

                return similarity < _options.SemanticBreakThreshold;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Semantic check failed: {ex.Message}");
                return false;
            }
        }

        private async Task<float[][]> GetEmbeddingsBatchAsync(IEnumerable<string> texts)
        {
            var textArray = texts.ToArray();
            var results = new List<float[]>();
            var uncachedTexts = new List<(string text, int index)>();

            for (int i = 0; i < textArray.Length; i++)
            {
                var hash = GetTextHash(textArray[i]);
                if (_embeddingCache.TryGetValue(hash, out var cached))
                {
                    results.Add(cached);
                }
                else
                {
                    uncachedTexts.Add((textArray[i], i));
                }
            }

            if (uncachedTexts.Any())
            {
                try
                {
                    var prefixed = uncachedTexts.Select(t => "passage: " + t.text.Trim()).ToArray();
                    var payload = new { inputs = prefixed };
                    var content = new StringContent(
                        JsonConvert.SerializeObject(payload),
                        Encoding.UTF8,
                        "application/json");

                    var response = await _ollamaClient.PostAsync("bulk/embed", content);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning($"Embedding API error: {response.StatusCode}");
                        return CreateZeroEmbeddings(textArray.Length);
                    }

                    var json = JObject.Parse(body);
                    var embeddings = json["embeddings"]!
                        .Select(t => t.Select(v => (float)v).ToArray())
                        .ToArray();

                    for (int i = 0; i < uncachedTexts.Count; i++)
                    {
                        var hash = GetTextHash(uncachedTexts[i].text);
                        _embeddingCache[hash] = embeddings[i];
                        results.Add(embeddings[i]);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Embedding error: {ex.Message}");
                    return CreateZeroEmbeddings(textArray.Length);
                }
            }

            return results.ToArray();
        }

        private float[][] CreateZeroEmbeddings(int count)
        {
            return Enumerable.Range(0, count)
                .Select(_ => new float[_options.VectorSize])
                .ToArray();
        }

        private string GetTextHash(string text)
        {
            var normalized = Regex.Replace(text, @"\s+", " ").Trim();
            var prefix = normalized.Length > 80 ? normalized.Substring(0, 80) : normalized;
            return $"{normalized.Length}_{prefix.GetHashCode()}";
        }

        private float[] ComputeAverage(float[][] vectors)
        {
            if (vectors.Length == 0) return new float[_options.VectorSize];

            var avg = new float[_options.VectorSize];
            foreach (var vec in vectors)
            {
                for (int i = 0; i < Math.Min(vec.Length, avg.Length); i++)
                    avg[i] += vec[i];
            }

            for (int i = 0; i < avg.Length; i++)
                avg[i] /= vectors.Length;

            return avg;
        }

        private float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;

            double dotProduct = 0, magnitudeA = 0, magnitudeB = 0;

            for (int i = 0; i < a.Length; i++)
            {
                dotProduct += a[i] * b[i];
                magnitudeA += a[i] * a[i];
                magnitudeB += b[i] * b[i];
            }

            magnitudeA = Math.Sqrt(magnitudeA);
            magnitudeB = Math.Sqrt(magnitudeB);

            if (magnitudeA == 0 || magnitudeB == 0) return 0f;

            return (float)(dotProduct / (magnitudeA * magnitudeB));
        }

        private Chunk_NEW CreateFallbackChunk(string text, int startPage)
        {
            return new Chunk_NEW
            {
                Text = text.Trim(),
                PageNumber = startPage,
                Section = "كامل الوثيقة",
                SubSection = "",
                HierarchyLevel = 0,
                ApproxTokens = EstimateTokens(text),
                StartLine = 1,
                EndLine = text.Split('\n').Length,
                ChunkId = "fallback_" + Guid.NewGuid().ToString("N")[..16],
                ChunkType = "fallback",
                CharacterCount = text.Length
            };
        }

        public void ClearCache()
        {
            _embeddingCache.Clear();
            _seenChunkHashes.Clear();
            _preservedOrphans.Clear();
            _logger.LogInformation("All caches cleared");
        }

        //new Methods To Be Added
        // Add this method to ImprovedSemanticChunker class
        /// <summary>
        /// Normalizes chunk sizes to be within acceptable limits for reranker
        /// Tables get different treatment due to tokenization issues
        /// </summary>
        private List<Chunk_NEW> NormalizeChunkSizesForReranker(List<Chunk_NEW> chunks, int maxSize = 3000, int overlap = 100)
        {
            var normalized = new List<Chunk_NEW>();

            foreach (var chunk in chunks)
            {
                // Use SMALLER limit for tables (they tokenize poorly)
                int effectiveMaxSize = (chunk.ChunkType == "table" || chunk.ChunkType == "list")
                    ? (maxSize / 2)  // Tables: use HALF the size (1500 chars)
                    : maxSize;        // Text: use full size (3000 chars)

                // If chunk is within size limit, keep it as-is
                if (chunk.Text.Length <= effectiveMaxSize)
                {
                    normalized.Add(chunk);
                    continue;
                }

                // Chunk is too large - split it while preserving context
                _logger.LogInformation($"Splitting large {chunk.ChunkType} chunk {chunk.ChunkId}: {chunk.Text.Length} chars into sub-chunks (max: {effectiveMaxSize})");

                var subChunks = SplitLargeChunk(chunk, effectiveMaxSize, overlap);
                normalized.AddRange(subChunks);
            }

            return normalized;
        }

        /// <summary>
        /// Splits a large chunk into smaller sub-chunks at sentence boundaries
        /// UPDATED: Protects tables and lists from being split
        /// </summary>
        private List<Chunk_NEW> SplitLargeChunk(Chunk_NEW originalChunk, int maxSize, int overlap)
        {
            // إذا كان الجدول صغيرًا (أقل من 2500 حرف)، احتفظ به كاملاً
            if ((originalChunk.ChunkType == "table" || originalChunk.ChunkType == "list")
                && originalChunk.Text.Length <= 2500)
            {
                return new List<Chunk_NEW> { originalChunk };
            }

            // خلاف ذلك، اقسم حتى الجداول الكبيرة
            _logger.LogInformation($"Splitting large {originalChunk.ChunkType} chunk: {originalChunk.Text.Length} chars");

            // باقي الكود التقسيم كما هو...
            var subChunks = new List<Chunk_NEW>();
            string text = originalChunk.Text;
            int subChunkIndex = 0;
            int step = maxSize - overlap;

            for (int i = 0; i < text.Length; i += step)
            {
                int end = Math.Min(i + maxSize, text.Length);

                // ابحث عن فاصل سطر طبيعي (نهاية صف في الجدول)
                if (end < text.Length)
                {
                    int lastLineBreak = text.LastIndexOf('\n', end - 1);
                    if (lastLineBreak > i + maxSize / 2)
                        end = lastLineBreak + 1;
                }

                string subChunkText = text.Substring(i, end - i).Trim();
                if (string.IsNullOrWhiteSpace(subChunkText)) continue;

                var subChunk = new Chunk_NEW
                {
                    // ... نفس الخصائص
                    Text = subChunkText,
                    ChunkType = originalChunk.ChunkType, // يبقى "table"
                    ChunkId = $"{originalChunk.ChunkId}_part_{subChunkIndex}",
                    // ...
                };
                subChunks.Add(subChunk);
                subChunkIndex++;
            }

            return subChunks;
        }
    }
}