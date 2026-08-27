using AiService.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AiService.Models
{
    /// <summary>
    /// Advanced chunking options optimized for Arabic documents and Qwen3-Reranker compatibility
    /// </summary>
    public class AdvancedChunkingOptions
    {
        // Core size control - tightened for safe reranking
        public double SemanticBreakThreshold { get; set; } = 0.68;
        public int MinLinesPerChunk { get; set; } = 15;
        public int MaxLinesPerChunk { get; set; } = 60;
        public int ForceBreakEveryLines { get; set; } = 80;
        public int OverlapSentences { get; set; } = 3;
        public int VectorSize { get; set; } = 1024;
        public int SemanticCheckInterval { get; set; } = 25;
        public int MinSemanticCheckLines { get; set; } = 30;
        public int BatchSize { get; set; } = 50;
        public bool EnableDeduplication { get; set; } = false;
        public double DuplicationThreshold { get; set; } = 0.92;
        public int MinChunkLength { get; set; } = 150;

        // NEW: Hard character limits to prevent Qwen3-Reranker 500 errors
        public int MaxCharactersPerChunk { get; set; } = 1400;     // Critical for reranker
        public int TargetCharactersPerChunk { get; set; } = 1000;  // Ideal chunk size

        // Structure preservation
        public bool PreserveListItems { get; set; } = true;
        public int MaxTableLines { get; set; } = 60;
        public bool KeepRelatedContent { get; set; } = true;
        public int MinTableRows { get; set; } = 1;

        // Filtering
        public bool RemoveFooters { get; set; } = false;
        public bool RemoveMetadata { get; set; } = false;
        public bool CleanImageReferences { get; set; } = false;
        public bool FilterShortLines { get; set; } = false;
        public int MinLineLength { get; set; } = 1;

        // Context & preservation
        public bool StrictMinimums { get; set; } = false;
        public bool PreserveOrphanedContent { get; set; } = true;
        public int ContextWindowLines { get; set; } = 5;
        public bool SmartSentenceBoundary { get; set; } = true;
        public int MinOrphanLines { get; set; } = 3;
        public bool MergeSmallChunks { get; set; } = true;
        public double MaxChunkOverlapRatio { get; set; } = 0.20;
    }

    /// <summary>
    /// Represents a single document chunk with rich metadata
    /// </summary>
    public class DocumentChunk
    {
        public string file_name { get; set; }
        public string Text { get; set; }
        public int PageNumber { get; set; }
        public string Section { get; set; }
        public int ApproxTokens { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string ChunkId { get; set; }
        public string ChunkType { get; set; } = "text";
        public string SubSection { get; set; }
        public int HierarchyLevel { get; set; }

        // Context linking
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

    /// <summary>
    /// Production-ready semantic chunker with character limits and Qwen3-Reranker safety
    /// </summary>
    public class AdvancedSemanticChunker
    {
        private readonly HttpClient _ollamaClient;
        private readonly ILogger _logger;
        private readonly AdvancedChunkingOptions _options;
        private readonly Dictionary<string, float[]> _embeddingCache;
        private readonly HashSet<string> _seenChunkHashes;
        private readonly List<string> _preservedOrphans;

        public AdvancedSemanticChunker(
            HttpClient ollamaClient,
            ILogger<AdvancedSemanticChunker> logger,
            IHttpClientFactory factory,
            AdvancedChunkingOptions options = null)
        {
            _ollamaClient = factory.CreateClient();
            _ollamaClient.BaseAddress = new Uri("http://10.21.2.143:8000/");
            _logger = logger;
            _options = options ?? new AdvancedChunkingOptions();
            _embeddingCache = new Dictionary<string, float[]>();
            _seenChunkHashes = new HashSet<string>();
            _preservedOrphans = new List<string>();
        }

        public async Task<IEnumerable<DocumentChunk>> SemanticChunkAsync(
            string fullText,
            int startPage = 1)
        {
            var chunks = new List<DocumentChunk>();
            if (string.IsNullOrWhiteSpace(fullText))
            {
                _logger.LogWarning("Empty input text provided");
                return chunks;
            }

            var originalText = fullText;
            var originalLength = fullText.Length;
            fullText = PreCleanText(fullText);
            var lines = PreprocessLines(fullText);

            if (!lines.Any())
            {
                _logger.LogWarning("No valid lines after preprocessing");
                return new List<DocumentChunk> { CreateFallbackChunk(originalText, startPage) };
            }

            _logger.LogInformation($"Starting advanced chunking: {lines.Length} lines, {originalLength} chars");

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

                if (HandlePageMarker(line, ref currentPage, bufferLines, ref bufferStartLine,
                    currentSection, chunks, currentLineNumber, currentChunkType, currentSubSection, currentHierarchyLevel))
                    continue;

                if (_options.RemoveFooters && IsFooterOrNoise(line))
                    continue;

                var (isSection, level, cleanedTitle) = DetectSectionWithLevel(line);
                if (isSection)
                {
                    if (bufferLines.Count >= Math.Max(1, _options.MinLinesPerChunk / 2))
                    {
                        await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                            bufferStartLine, currentLineNumber - 1, "SECTION_START", currentChunkType,
                            currentSubSection, currentHierarchyLevel);
                        bufferStartLine = currentLineNumber;
                    }

                    if (level == 1) { currentSection = cleanedTitle; currentSubSection = ""; }
                    else { currentSubSection = cleanedTitle; }
                    currentHierarchyLevel = level;

                    bufferLines.Add(line);
                    currentChunkType = "text";
                    insideTable = false;
                    insideList = false;
                    continue;
                }

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

                bufferLines.Add(line);

                if (bufferLines.Count >= _options.MaxLinesPerChunk ||
                    bufferLines.Count >= _options.ForceBreakEveryLines)
                {
                    int breakPoint = _options.SmartSentenceBoundary
                        ? FindSmartBreakPoint(bufferLines)
                        : bufferLines.Count;

                    await FlushBufferAsync(bufferLines.Take(breakPoint).ToList(), currentPage, currentSection, chunks,
                        bufferStartLine, currentLineNumber,
                        bufferLines.Count >= _options.ForceBreakEveryLines ? "FORCE_BREAK" : "MAX_LINES",
                        currentChunkType, currentSubSection, currentHierarchyLevel);

                    var remaining = bufferLines.Skip(breakPoint).ToList();
                    bufferLines.Clear();
                    bufferLines.AddRange(remaining);
                    bufferStartLine = currentLineNumber - remaining.Count + 1;
                    continue;
                }

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

            if (bufferLines.Count > 0)
            {
                await FlushBufferAsync(bufferLines, currentPage, currentSection, chunks,
                    bufferStartLine, currentLineNumber, "FINAL", currentChunkType,
                    currentSubSection, currentHierarchyLevel);
            }

            if (_preservedOrphans.Any())
            {
                foreach (var orphan in _preservedOrphans)
                {
                    if (!string.IsNullOrWhiteSpace(orphan))
                        chunks.Add(CreateOrphanChunk(orphan, currentPage, currentSection));
                }
            }

            chunks = PostProcessChunksEnhanced(chunks);
            LinkChunks(chunks);

            if (_options.EnableDeduplication)
            {
                var before = chunks.Count;
                chunks = await DeduplicateChunksAsync(chunks);
                if (before != chunks.Count)
                    _logger.LogInformation($"Deduplication removed {before - chunks.Count} chunks");
            }

            ValidateContentPreservation(originalText, chunks);
            _logger.LogInformation($"Advanced chunking complete: {chunks.Count} chunks created");

            return chunks.Any() ? chunks : new List<DocumentChunk> { CreateFallbackChunk(originalText, startPage) };
        }

        // ==================== CHARACTER-LIMIT ENFORCEMENT ====================
        private async Task FlushBufferAsync(
            List<string> bufferLines,
            int currentPage,
            string currentSection,
            List<DocumentChunk> chunks,
            int startLine,
            int endLine,
            string reason,
            string chunkType,
            string subSection = "",
            int hierarchyLevel = 0)
        {
            if (bufferLines.Count == 0) return;

            var text = string.Join("\n", bufferLines).Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            // CRITICAL: Enforce max character limit
            if (text.Length > _options.MaxCharactersPerChunk)
            {
                _logger.LogInformation($"Chunk exceeds {_options.MaxCharactersPerChunk} chars ({text.Length}), splitting intelligently");

                int splitPos = FindBestCharacterSplit(text, _options.TargetCharactersPerChunk);

                string part1 = text.Substring(0, splitPos).Trim();
                string part2 = text.Substring(splitPos).Trim();

                var lines1 = bufferLines.Take(ApproximateLineCount(part1, bufferLines)).ToList();
                await CreateAndAddChunk(part1, lines1, currentPage, currentSection, chunks,
                    startLine, startLine + lines1.Count - 1, "CHAR_LIMIT_PART1", chunkType, subSection, hierarchyLevel);

                if (!string.IsNullOrWhiteSpace(part2))
                {
                    string overlap = ExtractOverlapSentences(part1, _options.OverlapSentences);
                    string finalPart2 = string.IsNullOrEmpty(overlap) ? part2 : overlap + "\n\n" + part2;

                    var lines2 = bufferLines.Skip(lines1.Count).ToList();
                    await CreateAndAddChunk(finalPart2, lines2, currentPage, currentSection, chunks,
                        startLine + lines1.Count, endLine, "CHAR_LIMIT_PART2", chunkType, subSection, hierarchyLevel);
                }

                bufferLines.Clear();
                return;
            }

            // Normal chunk creation
            await CreateAndAddChunk(text, bufferLines, currentPage, currentSection, chunks,
                startLine, endLine, reason, chunkType, subSection, hierarchyLevel);

            bufferLines.Clear();
        }

        private async Task CreateAndAddChunk(
            string text,
            List<string> originalLines,
            int page,
            string section,
            List<DocumentChunk> chunks,
            int startLine,
            int endLine,
            string reason,
            string type,
            string subSection,
            int level)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            if (text.Length < 10)
            {
                if (_options.PreserveOrphanedContent)
                    _preservedOrphans.Add(text);
                return;
            }

            var hash = ComputeSimpleHash(text);
            if (_seenChunkHashes.Contains(hash))
            {
                _logger.LogDebug("Duplicate chunk skipped");
                return;
            }

            var chunk = new DocumentChunk
            {
                Text = text,
                PageNumber = page,
                Section = section,
                SubSection = subSection,
                HierarchyLevel = level,
                ApproxTokens = EstimateTokens(text),
                StartLine = startLine,
                EndLine = endLine,
                ChunkId = $"chunk_{chunks.Count + 1}_{Guid.NewGuid():N}"[..32],
                ChunkType = type,
                CharacterCount = text.Length
            };

            chunks.Add(chunk);
            _seenChunkHashes.Add(hash);

            var sectionPreview = section.Length > 35 ? section[..35] + "..." : section;
            _logger.LogInformation(
                $"Chunk #{chunks.Count} [{type}] ({reason}): {originalLines.Count} lines, " +
                $"page {page}, section: {sectionPreview}, {chunk.ApproxTokens} tokens, {text.Length} chars");
        }

        private int FindBestCharacterSplit(string text, int target)
        {
            if (text.Length <= target) return text.Length;

            int start = Math.Max(target - 300, target / 2);
            int end = Math.Min(target + 200, text.Length);

            var breakers = new[] { '\n', '۔', '؟', '.', '!', ';' };

            for (int i = end - 1; i >= start; i--)
            {
                if (breakers.Contains(text[i]))
                    return i + 1;
            }

            int space = text.LastIndexOf(' ', end, end - start);
            return space > start ? space + 1 : target;
        }

        private string ExtractOverlapSentences(string text, int count)
        {
            var sentences = Regex.Split(text, @"(?<=[۔؟!.\n])")
                .Select(s => s.Trim())
                .Where(s => s.Length > 10)
                .TakeLast(count)
                .ToList();

            return string.Join(" ", sentences);
        }

        private int ApproximateLineCount(string part, List<string> allLines)
        {
            string joined = string.Join("\n", allLines);
            int idx = joined.IndexOf(part);
            if (idx == -1) return allLines.Count / 2;
            return joined.Take(idx).Count(c => c == '\n') + 1;
        }

        private int FindSmartBreakPoint(List<string> lines)
        {
            if (lines.Count <= 5) return lines.Count;
            int searchStart = Math.Max(0, lines.Count - 10);
            var enders = new[] { '.', '!', '?', '۔', '؛', ';' };
            for (int i = lines.Count - 1; i >= searchStart; i--)
            {
                if (enders.Any(c => lines[i].TrimEnd().EndsWith(c)))
                    return i + 1;
            }
            return lines.Count;
        }

        // All other methods remain unchanged and are included below...
        // (PostProcessChunksEnhanced, CanMergeChunks, MergeChunks, LinkChunks, CheckOverlap, ExtractKeyPhrases,
        //  ValidateContentPreservation, CreateOrphanChunk, CreateEnhancedOverlap, EstimateTokens,
        //  DetectSectionWithLevel, IsContinuationLine, PreCleanText, HasArabicSpacingIssue, FixArabicSpacing,
        //  PreprocessLines, HandlePageMarker, IsFooterOrNoise, IsTableLine, IsListItem,
        //  ComputeSimpleHash, DeduplicateChunksAsync, CheckSemanticBreakAsync, GetEmbeddingsBatchAsync, etc.)

        // ... [Include all your original helper methods here — they are identical]
        // ==================== MISSING METHODS - ADD THESE ====================

        private List<DocumentChunk> PostProcessChunksEnhanced(List<DocumentChunk> chunks)
        {
            if (!_options.MergeSmallChunks) return chunks;

            var processed = new List<DocumentChunk>();
            DocumentChunk pending = null;

            foreach (var chunk in chunks)
            {
                int min = _options.StrictMinimums ? _options.MinChunkLength : Math.Max(50, _options.MinChunkLength / 3);

                if (chunk.Text.Length < min)
                {
                    if (pending == null)
                        pending = chunk;
                    else if (CanMergeChunks(pending, chunk))
                    {
                        pending = MergeChunks(pending, chunk);
                        if (pending.Text.Length >= min)
                        {
                            processed.Add(pending);
                            pending = null;
                        }
                    }
                    else
                    {
                        if (pending.Text.Length >= 20 || _options.PreserveOrphanedContent)
                            processed.Add(pending);
                        pending = chunk;
                    }
                }
                else
                {
                    if (pending != null && (pending.Text.Length >= 20 || _options.PreserveOrphanedContent))
                        processed.Add(pending);
                    pending = null;
                    processed.Add(chunk);
                }
            }

            if (pending != null && (pending.Text.Length >= 20 || _options.PreserveOrphanedContent))
                processed.Add(pending);

            return processed;
        }

        private bool CanMergeChunks(DocumentChunk a, DocumentChunk b) =>
            (a.ChunkType == b.ChunkType && a.Section == b.Section && Math.Abs(a.PageNumber - b.PageNumber) <= 1) ||
            (a.PageNumber == b.PageNumber && b.StartLine - a.EndLine <= 3);

        private DocumentChunk MergeChunks(DocumentChunk a, DocumentChunk b) => new DocumentChunk
        {
            Text = a.Text + "\n\n" + b.Text,
            PageNumber = a.PageNumber,
            Section = a.Section,
            SubSection = string.IsNullOrEmpty(a.SubSection) ? b.SubSection : a.SubSection,
            HierarchyLevel = a.HierarchyLevel,
            ApproxTokens = EstimateTokens(a.Text + "\n\n" + b.Text),
            StartLine = a.StartLine,
            EndLine = b.EndLine,
            ChunkId = $"merged_{a.ChunkId}_{b.ChunkId}",
            ChunkType = a.ChunkType,
            CharacterCount = a.CharacterCount + b.CharacterCount + 2
        };

        private void LinkChunks(List<DocumentChunk> chunks)
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

        private bool CheckOverlap(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            int len = Math.Min(50, a.Length);
            return b.StartsWith(a.Substring(a.Length - len));
        }

        private List<string> ExtractKeyPhrases(string text)
        {
            var phrases = new List<string>();
            var first = text.Split(new[] { '.', '!', '?', '۔', '؛' }, 2)[0].Trim();
            if (first.Length > 10) phrases.Add(first);

            var markers = Regex.Matches(text, @"(باب |الفصل |فصل |أولا|ثانيا|ثالثا).{0,50}");
            foreach (Match m in markers) phrases.Add(m.Value.Trim());

            return phrases.Take(3).ToList();
        }

        private void ValidateContentPreservation(string original, List<DocumentChunk> chunks)
        {
            var origWords = Regex.Matches(original, @"\w+").Count;
            var chunkWords = chunks.Sum(c => Regex.Matches(c.Text, @"\w+").Count);
            double ratio = origWords > 0 ? (double)chunkWords / origWords : 1;
            _logger.LogInformation($"Preservation: {ratio:P2} ({chunkWords}/{origWords} words)");
            if (ratio < 0.95)
                _logger.LogWarning($"Content loss detected: {ratio:P2}");
        }

        private DocumentChunk CreateOrphanChunk(string text, int page, string section) => new DocumentChunk
        {
            Text = text.Trim(),
            PageNumber = page,
            Section = section,
            SubSection = "محتوى إضافي",
            ApproxTokens = EstimateTokens(text),
            ChunkId = $"orphan_{Guid.NewGuid():N}"[..24],
            ChunkType = "orphan",
            CharacterCount = text.Length
        };

        private List<string> CreateEnhancedOverlap(string text)
        {
            var sentences = text.Split(new[] { '.', '!', '?', '۔', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 15)
                .TakeLast(_options.OverlapSentences)
                .ToList();

            if (!sentences.Any()) return new List<string>();

            var overlap = string.Join(". ", sentences);
            if (overlap.Length < 150 && text.Length >= 150)
                overlap = text.Substring(text.Length - 150);

            if (overlap.Length > 300)
                overlap = string.Join(" ", overlap.Split(' ').TakeLast(40));

            return new List<string> { overlap };
        }

        private int EstimateTokens(string text)
        {
            var arabic = text.Count(c => c >= '\u0600' && c <= '\u06FF');
            var english = text.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
            var digits = text.Count(char.IsDigit);
            return Math.Max(50, (int)((arabic / 2.5) + (english / 4.0) + digits + 30));
        }

        private (bool isSection, int level, string cleanedTitle) DetectSectionWithLevel(string line)
        {
            if (line.Length <= 12 || line.Length >= 250) return (false, 0, "");
            var hashMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)");
            if (hashMatch.Success)
            {
                int level = hashMatch.Groups[1].Value.Length;
                string title = hashMatch.Groups[2].Value.Trim();
                return (true, level, title);
            }
            var arabicChars = line.Count(c => c >= '\u0600' && c <= '\u06FF');
            if (arabicChars < line.Length * 0.5) return (false, 0, "");
            if (IsListItem(line)) return (false, 0, "");
            var specialChars = line.Count(c => char.IsDigit(c) || c == '|' || c == '-');
            if (specialChars > line.Length * 0.3) return (false, 0, "");
            var excludeKeywords = new[] { "استعلام", "اختيار", "إضافة", "تقديم", "ادخال", "الكترونيا", "د.ك", "يدوي", "الرسوم", "نوع الخدمة" };
            if (excludeKeywords.Any(k => line.Contains(k))) return (false, 0, "");

            if (line.StartsWith("باب ") || line.Contains("الباب")) return (true, 1, CleanSectionTitle(line));
            if (line.StartsWith("الفصل ") || line.StartsWith("فصل ")) return (true, 2, CleanSectionTitle(line));
            if (Regex.IsMatch(line, @"^(أولا|ثانيا|ثالثا|رابعا|خامسا|سادسا|سابعا|ثامنا|تاسعا)[:\s]", RegexOptions.IgnoreCase))
                return (true, 2, CleanSectionTitle(line));

            return (false, 0, "");
        }

        private string CleanSectionTitle(string title)
        {
            title = Regex.Replace(title, @"^#+\s*", "");
            title = Regex.Replace(title, @"[:\-]+$", "");
            return title.Trim();
        }

        private bool IsContinuationLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (line.Length > 0 && char.IsLower(line[0])) return true;
            if (line.StartsWith("و") || line.StartsWith("أو") || line.StartsWith("من")) return true;
            return false;
        }

        private string RemoveMetadataHeader(string text)
        {
            if (!_options.RemoveMetadata) return text;
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var cleanLines = new List<string>();
            bool inMetadataBlock = false;
            bool metadataBlockEnded = false;
            int lineIndex = 0;
            foreach (var line in lines)
            {
                lineIndex++;
                if (lineIndex < 10 && string.IsNullOrWhiteSpace(line)) continue;
                if (!metadataBlockEnded && (line.Contains("![](_page_") || line.StartsWith("**Source:**") || line.StartsWith("# ![](")))
                {
                    inMetadataBlock = true;
                    continue;
                }
                if (inMetadataBlock)
                {
                    if (line.StartsWith("**Source:**") || line.StartsWith("**Language:**") || line.StartsWith("**Pages:**") ||
                        line.StartsWith("**Extracted:**") || line.Trim() == "---" || line.Trim().StartsWith("===") || string.IsNullOrWhiteSpace(line))
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
                    if (lineIndex < lines.Length - 1 && lines[lineIndex].Contains("![](_page_")) continue;
                }
                if (_options.CleanImageReferences && (line.Contains("![](_page_") || line.Contains("Picture_"))) continue;
                if (metadataBlockEnded || lineIndex > 15) cleanLines.Add(line);
            }
            return string.Join("\n", cleanLines);
        }

        private string PreCleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = RemoveMetadataHeader(text);
            if (_options.CleanImageReferences)
            {
                text = Regex.Replace(text, @"!\[\]\([^)]*\)", "", RegexOptions.Multiline);
                text = Regex.Replace(text, @"^!\[\].*?$", "", RegexOptions.Multiline);
            }
            text = Regex.Replace(text, @"ightarrow", "→", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\$\s*\\rightarrow\s*\$\s*ace\s*1\s*±\s*±\s*±\s*±", "");
            text = Regex.Replace(text, @"\$.*?\$", "");
            text = Regex.Replace(text, @"\\\[.*?\\\]", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\\[a-zA-Z]+(\{[^\}]*\})?", "");
            text = Regex.Replace(text, @"[➔➜➤➢➡➙➛➝➞➟➠➢➣➤➥➦➧➨➩➪➫➬➭➮➯➱➲]+", "→");
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
                    if ((line.StartsWith("\"") && line.EndsWith("\"")) || (line.StartsWith("'") && line.EndsWith("'")))
                        line = line[1..^1];
                    if (_options.CleanImageReferences)
                        line = Regex.Replace(line, @"!\[\]\([^)]+\)", "").Trim();
                    return line.Trim();
                })
                .Where(l => !string.IsNullOrWhiteSpace(l));

            if (_options.FilterShortLines)
                lines = lines.Where(l => l.Length >= _options.MinLineLength);

            return lines.ToArray();
        }

        private bool HandlePageMarker(string line, ref int currentPage, List<string> bufferLines,
            ref int bufferStartLine, string currentSection, List<DocumentChunk> chunks,
            int currentLineNumber, string chunkType, string subSection, int hierarchyLevel)
        {
            var isPageMarker = line.Contains("<<PAGE:") || line.Contains("--- Page ") || line.Contains("--- صفحة ");
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
            if (!_options.RemoveFooters) return false;
            if (line.Length >= 200) return false;
            var simpleMatches = new[] { "وزارة التجارة", "دولة الكويت", "State of Kuwait", "Ministry of Commerce", "Picture_" };
            if (simpleMatches.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
            if (line.Contains("![](_page_")) return true;
            var regexPatterns = new[] { @"صفحة\s*\d+", @"Page\s*\d+", @"^\d+$" };
            return regexPatterns.Any(p => Regex.IsMatch(line, p, RegexOptions.IgnoreCase));
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
            return line.TrimStart().StartsWith("•") || line.TrimStart().StartsWith("*") || line.TrimStart().StartsWith("-") ||
                   line.TrimStart().StartsWith("▪") || line.TrimStart().StartsWith("◄") || line.TrimStart().StartsWith("►") ||
                   line.TrimStart().StartsWith("→") || Regex.IsMatch(line, @"^\d+[\.\-\)]\s") ||
                   Regex.IsMatch(line, @"^[أ-ي][\.\-\)]\s");
        }

        private string ComputeSimpleHash(string text)
        {
            var normalized = Regex.Replace(text, @"\s+", " ").Trim().ToLower();
            var prefix = normalized.Length > 50 ? normalized.Substring(0, 50) : normalized;
            var suffix = normalized.Length > 100 ? normalized.Substring(normalized.Length - 50) : "";
            return $"{normalized.Length}_{prefix.GetHashCode()}_{suffix.GetHashCode()}";
        }

        private async Task<List<DocumentChunk>> DeduplicateChunksAsync(List<DocumentChunk> chunks)
        {
            if (chunks.Count < 2 || !_options.EnableDeduplication) return chunks;

            var deduplicated = new List<DocumentChunk>();
            var seenEmbeddings = new List<(float[] embedding, DocumentChunk chunk)>();
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
                        _logger.LogDebug($"Duplicate: page {chunk.PageNumber} similar to page {seenChunk.PageNumber} (similarity: {similarity:F3})");
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
                if (bufferLines.Count < _options.MinSemanticCheckLines) return false;
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
                    results.Add(cached);
                else
                    uncachedTexts.Add((textArray[i], i));
            }

            if (uncachedTexts.Any())
            {
                try
                {
                    var prefixed = uncachedTexts.Select(t => "passage: " + t.text.Trim()).ToArray();
                    var payload = new { inputs = prefixed };
                    var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

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

        private DocumentChunk CreateFallbackChunk(string text, int startPage)
        {
            return new DocumentChunk
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
            _logger.LogInformation("Advanced chunker caches cleared");
        }
    }
}
