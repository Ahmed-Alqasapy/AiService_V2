using System.Text;
using System.Text.RegularExpressions;

namespace AiService.Models
{
    public class HybridChunker
    {
        private readonly HybridChunkingConfig _config;

        public HybridChunker(HybridChunkingConfig config = null)
        {
            _config = config ?? new HybridChunkingConfig();
        }

        /// <summary>
        /// Main method: applies hybrid chunking strategy
        /// </summary>
        public List<Chunk_HY> ChunkText(string text, Dictionary<string, object> metadata = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<Chunk_HY>();

            // Step 1: Recursive chunking for structure
            var structuralChunks = RecursiveChunk(text);

            // Step 2: Apply semantic analysis
            var semanticChunks = ApplySemanticBoundaries(structuralChunks);

            // Step 3: Add sliding overlap for context continuity
            var finalChunks = ApplySlidingOverlap(semanticChunks);

            // Step 4: Add metadata and IDs
            return EnrichChunks(finalChunks, metadata);
        }

        /// <summary>
        /// Step 1: Recursive chunking - splits by document structure
        /// </summary>
        //private List<string> RecursiveChunk(string text)
        //{
        //    var chunks = new List<string>();
        //    int chunkSize = _config.MaxChunkSize;

        //    for (int i = 0; i < text.Length; i += chunkSize)
        //    {
        //        int length = Math.Min(chunkSize, text.Length - i);
        //        chunks.Add(text.Substring(i, length));

        //        if (chunks.Count > 200) break; // Safety limit
        //    }

        //    return chunks;
        //}

        public List<string> RecursiveChunk(string rawPdfText, int targetSize = 2500, int minSize = 1000, int overlap = 400)
        {

            // Split only on strong paragraph breaks first
            var paragraphs = rawPdfText.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(p => p.Trim())
                                   .Where(p => p.Length > 20) // Filter noise
                                   .ToList();

            var chunks = new List<string>();
            StringBuilder current = new StringBuilder();

            foreach (var para in paragraphs)
            {
                // If adding this paragraph exceeds target and we have enough content
                if (current.Length + para.Length + 2 > targetSize && current.Length >= minSize)
                {
                    chunks.Add(current.ToString().Trim());

                    // Overlap: keep last part of current chunk
                    string overlapText = current.ToString();
                    if (overlapText.Length > overlap)
                        overlapText = overlapText.Substring(overlapText.Length - overlap);

                    current.Clear();
                    current.Append(overlapText);
                }

                if (current.Length > 0) current.Append("\n\n");
                current.Append(para);
            }

            // Add final chunk
            if (current.Length >= minSize / 2)
            {
                chunks.Add(current.ToString().Trim());
            }

            // Fallback: if still too few/large, force fixed-size split with overlap
            if (chunks.Count == 1 && chunks[0].Length > targetSize * 2)
            {
                chunks = FixedSizeChunkWithOverlap(chunks[0], targetSize, overlap);
            }

            // Safety cap
            if (chunks.Count > 200) chunks = chunks.Take(200).ToList();

            return chunks;
        }

        private List<string> FixedSizeChunkWithOverlap(string text, int size, int overlap)
        {
            var chunks = new List<string>();
            for (int i = 0; i < text.Length; i += size - overlap)
            {
                int length = Math.Min(size, text.Length - i);
                chunks.Add(text.Substring(i, length).Trim());
            }
            return chunks;
        }
        private int FindLastSentenceBoundary(string text, int start, int end)
        {
            // Look for sentence endings: . ! ?
            for (int i = end - 1; i >= start; i--)
            {
                char c = text[i];
                if (c == '.' || c == '!' || c == '?')
                {
                    // Make sure there's whitespace or end after it
                    if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))
                    {
                        return i + 1;
                    }
                }
            }

            return -1; // No boundary found
        }

        private List<string> SplitByNextSeparator(string text, List<string> separators, int startIndex)
        {
            for (int i = startIndex; i < separators.Count; i++)
            {
                var parts = text.Split(new[] { separators[i] }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                if (parts.Count > 1)
                    return parts;
            }

            return SplitByCharacterCount(text);
        }

        private List<string> SplitByCharacterCount(string text)
        {
            var chunks = new List<string>();
            for (int i = 0; i < text.Length; i += _config.MaxChunkSize)
            {
                var chunkSize = Math.Min(_config.MaxChunkSize, text.Length - i);
                chunks.Add(text.Substring(i, chunkSize));
            }
            return chunks;
        }

        /// <summary>
        /// Step 2: Semantic boundary detection - ensures chunks break at meaningful points
        /// </summary>
        private List<string> ApplySemanticBoundaries(List<string> chunks)
        {
            if (!_config.UseSemanticBoundaries)
                return chunks;

            var refinedChunks = new List<string>();

            foreach (var chunk in chunks)
            {
                // Check if chunk is too large and needs semantic splitting
                if (chunk.Length > _config.MaxChunkSize)
                {
                    var semanticParts = SplitBySemanticBoundary(chunk);
                    refinedChunks.AddRange(semanticParts);
                }
                else
                {
                    refinedChunks.Add(chunk);
                }
            }

            return refinedChunks;
        }

        private List<string> SplitBySemanticBoundary(string text)
        {
            var chunks = new List<string>();

            // Try to split at sentence boundaries first
            var sentences = Regex.Split(text, @"(?<=[.!?])\s+");
            var currentChunk = "";

            foreach (var sentence in sentences)
            {
                if ((currentChunk + sentence).Length <= _config.MaxChunkSize)
                {
                    currentChunk += (currentChunk.Length > 0 ? " " : "") + sentence;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(currentChunk))
                        chunks.Add(currentChunk.Trim());
                    currentChunk = sentence;
                }
            }

            if (!string.IsNullOrWhiteSpace(currentChunk))
                chunks.Add(currentChunk.Trim());

            return chunks;
        }

        /// <summary>
        /// Step 3: Sliding overlap - adds context continuity between chunks
        /// </summary>
        private List<string> ApplySlidingOverlap(List<string> chunks)
        {
            if (_config.OverlapSize == 0 || chunks.Count <= 1)
                return chunks;

            var overlappedChunks = new List<string>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var prefix = "";
                var suffix = "";

                // Add prefix from previous chunk
                if (i > 0 && _config.OverlapSize > 0)
                {
                    var prevChunk = chunks[i - 1];
                    var overlapStart = Math.Max(0, prevChunk.Length - _config.OverlapSize);
                    prefix = prevChunk.Substring(overlapStart) + " ";
                }

                // Add suffix from next chunk
                if (i < chunks.Count - 1 && _config.OverlapSize > 0)
                {
                    var nextChunk = chunks[i + 1];
                    var overlapEnd = Math.Min(_config.OverlapSize, nextChunk.Length);
                    suffix = " " + nextChunk.Substring(0, overlapEnd);
                }

                overlappedChunks.Add(prefix + chunk + suffix);
            }

            return overlappedChunks;
        }

        /// <summary>
        /// Step 4: Enrich chunks with metadata and IDs
        /// </summary>
        private List<Chunk_HY> EnrichChunks(List<string> chunkTexts, Dictionary<string, object> metadata)
        {
            var enrichedChunks = new List<Chunk_HY>();

            for (int i = 0; i < chunkTexts.Count; i++)
            {
                var chunk = new Chunk_HY
                {
                    Id = Guid.NewGuid().ToString(),
                    Text = chunkTexts[i],
                    Index = i,
                    TotalChunks = chunkTexts.Count,
                    CharacterCount = chunkTexts[i].Length,
                    Metadata = new Dictionary<string, object>(metadata ?? new Dictionary<string, object>())
                };

                // Add position metadata
                chunk.Metadata["position"] = i;
                chunk.Metadata["is_first"] = i == 0;
                chunk.Metadata["is_last"] = i == chunkTexts.Count - 1;

                enrichedChunks.Add(chunk);
            }

            return enrichedChunks;
        }
    }

    /// <summary>
    /// Configuration for hybrid chunking
    /// </summary>
    public class HybridChunkingConfig
    {
        /// <summary>
        /// Maximum size of each chunk in characters
        /// </summary>
        public int MaxChunkSize { get; set; } = 2500;

        /// <summary>
        /// Size of overlap between chunks for context continuity
        /// </summary>
        public int OverlapSize { get; set; } = 100;

        /// <summary>
        /// Enable semantic boundary detection
        /// </summary>
        public bool UseSemanticBoundaries { get; set; } = true;

        /// <summary>
        /// Structural separators in order of priority
        /// </summary>
        public List<string> StructuralSeparators { get; set; } = new List<string>
        {
            "\n\n\n",      // Document sections
            "\n\n",        // Paragraphs
            "\n",          // Lines
            ". ",          // Sentences
            "? ",
            "! ",
            "; ",
            ", ",
            " "            // Words (last resort)
        };
    }

    /// <summary>
    /// Represents a single chunk with metadata
    /// </summary>
    public class Chunk_HY
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public int Index { get; set; }
        public int TotalChunks { get; set; }
        public int CharacterCount { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public string file_name { get; set; }

        public override string ToString()
        {
            return $"Chunk {Index + 1}/{TotalChunks} ({CharacterCount} chars): {Text.Substring(0, Math.Min(50, Text.Length))}...";
        }
    }
}
