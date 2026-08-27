using System.Text;
using System.Text.RegularExpressions;

namespace AiService.Services
{
    /// <summary>
    /// Available chunking strategies
    /// </summary>
    public enum ChunkStrategy
    {
        FixedSize,
        Semantic,
        Recursive,
        Sentence,
        Paragraph
    }

    /// <summary>
    /// Represents a text chunk with metadata
    /// </summary>
    public class Chunk
    {
        public string Text { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public int ChunkId { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public int Length => Text?.Length ?? 0;

        public Chunk()
        {
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Advanced text chunking system with multiple strategies for optimal RAG performance
    /// </summary>
    public class SmartChunker
    {
        private readonly int _chunkSize;
        private readonly int _chunkOverlap;
        private readonly ChunkStrategy _strategy;
        private readonly bool _respectSentenceBoundary;
        private readonly int _minChunkSize;
        private readonly Func<string, int> _tokenCounter;

        // Regex patterns
        private readonly Regex _sentenceEndings = new Regex(@"[.!?]+[\s\n]+", RegexOptions.Compiled);
        private readonly Regex _paragraphSeparator = new Regex(@"\n\s*\n", RegexOptions.Compiled);
        private readonly Regex _whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private readonly Regex _multipleNewlines = new Regex(@"\n+", RegexOptions.Compiled);

        // Hierarchical separators for recursive chunking
        private readonly string[] _separators = new[]
        {
            "\n\n\n",  // Multiple newlines
            "\n\n",    // Paragraph breaks
            "\n",      // Line breaks
            ". ",      // Sentences
            "! ",
            "? ",
            "; ",      // Clauses
            ", ",      // Phrases
            " ",       // Words
            ""         // Characters
        };

        /// <summary>
        /// Initialize the smart chunker
        /// </summary>
        public SmartChunker(
            int chunkSize = 512,
            int chunkOverlap = 50,
            ChunkStrategy strategy = ChunkStrategy.Recursive,
            bool respectSentenceBoundary = true,
            int minChunkSize = 50,
            Func<string, int> tokenCounter = null)
        {
            _chunkSize = chunkSize;
            _chunkOverlap = chunkOverlap;
            _strategy = strategy;
            _respectSentenceBoundary = respectSentenceBoundary;
            _minChunkSize = minChunkSize;
            _tokenCounter = tokenCounter ?? (s => s.Length);
        }

        /// <summary>
        /// Main chunking method that routes to appropriate strategy
        /// </summary>
        public List<Chunk> ChunkText(string text, Dictionary<string, object> metadata = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<Chunk>();

            // Clean text
            text = CleanText(text);

            // Route to appropriate strategy
            List<Chunk> chunks = _strategy switch
            {
                ChunkStrategy.FixedSize => FixedSizeChunk(text),
                ChunkStrategy.Semantic => SemanticChunk(text),
                ChunkStrategy.Recursive => RecursiveChunk(text),
                ChunkStrategy.Sentence => SentenceChunk(text),
                ChunkStrategy.Paragraph => ParagraphChunk(text),
                _ => RecursiveChunk(text)
            };

            // Add metadata to all chunks
            if (metadata != null)
            {
                foreach (var chunk in chunks)
                {
                    foreach (var kvp in metadata)
                    {
                        chunk.Metadata[kvp.Key] = kvp.Value;
                    }
                }
            }

            return chunks;
        }

        /// <summary>
        /// Clean and normalize text
        /// </summary>
        private string CleanText(string text)
        {
            // Remove excessive whitespace
            text = _whitespace.Replace(text, " ");
            // Normalize newlines
            text = _multipleNewlines.Replace(text, "\n");
            return text.Trim();
        }

        /// <summary>
        /// Fixed-size chunking with overlap and sentence boundary respect
        /// </summary>
        private List<Chunk> FixedSizeChunk(string text)
        {
            var chunks = new List<Chunk>();
            int chunkId = 0;
            int start = 0;

            while (start < text.Length)
            {
                int end = Math.Min(start + _chunkSize, text.Length);

                // Try to break at sentence boundary if requested
                if (_respectSentenceBoundary && end < text.Length)
                {
                    end = FindSentenceBoundary(text, start, end);
                }

                string chunkText = text.Substring(start, end - start).Trim();

                if (chunkText.Length >= _minChunkSize)
                {
                    chunks.Add(new Chunk
                    {
                        Text = chunkText,
                        StartIndex = start,
                        EndIndex = end,
                        ChunkId = chunkId++
                    });
                }

                // Move start with overlap
                start = end - _chunkOverlap;

                // Prevent infinite loop
                if (chunks.Count > 0 && start <= chunks[^1].StartIndex)
                {
                    start = end;
                }

                // Ensure progress
                if (start >= text.Length - _minChunkSize)
                    break;
            }

            return chunks;
        }

        /// <summary>
        /// Recursive chunking using hierarchical separators - Best for general purpose
        /// </summary>
        private List<Chunk> RecursiveChunk(string text)
        {
            var chunks = new List<Chunk>();
            RecursiveSplit(text, _separators, 0, 0, chunks);
            return chunks;
        }

        /// <summary>
        /// Helper method for recursive splitting
        /// </summary>
        private void RecursiveSplit(
            string text,
            string[] separators,
            int separatorIndex,
            int startIdx,
            List<Chunk> chunks)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            // If text is small enough, add as chunk
            if (_tokenCounter(text) <= _chunkSize)
            {
                if (text.Length >= _minChunkSize)
                {
                    chunks.Add(new Chunk
                    {
                        Text = text.Trim(),
                        StartIndex = startIdx,
                        EndIndex = startIdx + text.Length,
                        ChunkId = chunks.Count
                    });
                }
                return;
            }

            // Try current separator
            if (separatorIndex < separators.Length)
            {
                string separator = separators[separatorIndex];

                if (!string.IsNullOrEmpty(separator) && text.Contains(separator))
                {
                    var splits = text.Split(new[] { separator }, StringSplitOptions.None);
                    var mergedChunks = MergeSmallSplits(splits, separator);

                    int currentIdx = startIdx;
                    foreach (var split in mergedChunks)
                    {
                        if (!string.IsNullOrWhiteSpace(split))
                        {
                            RecursiveSplit(split, separators, separatorIndex + 1, currentIdx, chunks);
                        }
                        currentIdx += split.Length + separator.Length;
                    }
                    return;
                }
            }

            // Fallback: force split at chunk size
            if (text.Length > _chunkSize)
            {
                int splitPoint = _chunkSize;
                if (_respectSentenceBoundary)
                {
                    splitPoint = FindSentenceBoundary(text, 0, _chunkSize);
                }

                var part1 = text.Substring(0, splitPoint);
                var part2 = text.Substring(Math.Max(0, splitPoint - _chunkOverlap));

                RecursiveSplit(part1, separators, separatorIndex + 1, startIdx, chunks);
                RecursiveSplit(part2, separators, separatorIndex + 1, startIdx + splitPoint - _chunkOverlap, chunks);
            }
            else
            {
                // Add remaining text
                if (text.Length >= _minChunkSize)
                {
                    chunks.Add(new Chunk
                    {
                        Text = text.Trim(),
                        StartIndex = startIdx,
                        EndIndex = startIdx + text.Length,
                        ChunkId = chunks.Count
                    });
                }
            }
        }

        /// <summary>
        /// Merge small splits to meet minimum chunk size
        /// </summary>
        private List<string> MergeSmallSplits(string[] splits, string separator)
        {
            var merged = new List<string>();
            var current = new StringBuilder();

            foreach (var split in splits)
            {
                if (current.Length > 0)
                    current.Append(separator);

                current.Append(split);

                if (_tokenCounter(current.ToString()) >= _chunkSize)
                {
                    merged.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                merged.Add(current.ToString());
            }

            return merged;
        }

        /// <summary>
        /// Sentence-based chunking - preserves complete sentences
        /// </summary>
        private List<Chunk> SentenceChunk(string text)
        {
            var chunks = new List<Chunk>();
            var sentences = SplitIntoSentences(text);

            var currentChunk = new StringBuilder();
            int chunkStart = 0;
            int currentPos = 0;

            foreach (var sentence in sentences)
            {
                var testChunk = currentChunk.Length > 0
                    ? currentChunk + " " + sentence
                    : sentence;

                if (_tokenCounter(testChunk) > _chunkSize && currentChunk.Length > 0)
                {
                    // Save current chunk
                    var chunkText = currentChunk.ToString().Trim();
                    if (chunkText.Length >= _minChunkSize)
                    {
                        chunks.Add(new Chunk
                        {
                            Text = chunkText,
                            StartIndex = chunkStart,
                            EndIndex = currentPos,
                            ChunkId = chunks.Count
                        });
                    }

                    // Start new chunk with overlap
                    currentChunk.Clear();
                    chunkStart = Math.Max(0, currentPos - _chunkOverlap);
                }

                if (currentChunk.Length > 0)
                    currentChunk.Append(" ");
                currentChunk.Append(sentence);
                currentPos += sentence.Length + 1;
            }

            // Add remaining chunk
            if (currentChunk.Length >= _minChunkSize)
            {
                chunks.Add(new Chunk
                {
                    Text = currentChunk.ToString().Trim(),
                    StartIndex = chunkStart,
                    EndIndex = text.Length,
                    ChunkId = chunks.Count
                });
            }

            return chunks;
        }

        /// <summary>
        /// Paragraph-based chunking
        /// </summary>
        private List<Chunk> ParagraphChunk(string text)
        {
            var chunks = new List<Chunk>();
            var paragraphs = _paragraphSeparator.Split(text);

            var currentChunk = new StringBuilder();
            int chunkStart = 0;
            int currentPos = 0;

            foreach (var paragraph in paragraphs)
            {
                if (string.IsNullOrWhiteSpace(paragraph))
                    continue;

                var testChunk = currentChunk.Length > 0
                    ? currentChunk + "\n\n" + paragraph
                    : paragraph;

                if (_tokenCounter(testChunk) > _chunkSize && currentChunk.Length > 0)
                {
                    var chunkText = currentChunk.ToString().Trim();
                    if (chunkText.Length >= _minChunkSize)
                    {
                        chunks.Add(new Chunk
                        {
                            Text = chunkText,
                            StartIndex = chunkStart,
                            EndIndex = currentPos,
                            ChunkId = chunks.Count
                        });
                    }

                    currentChunk.Clear();
                    chunkStart = currentPos;
                }

                if (currentChunk.Length > 0)
                    currentChunk.Append("\n\n");
                currentChunk.Append(paragraph);
                currentPos += paragraph.Length + 2;
            }

            // Add remaining chunk
            if (currentChunk.Length >= _minChunkSize)
            {
                chunks.Add(new Chunk
                {
                    Text = currentChunk.ToString().Trim(),
                    StartIndex = chunkStart,
                    EndIndex = text.Length,
                    ChunkId = chunks.Count
                });
            }

            return chunks;
        }

        /// <summary>
        /// Semantic chunking based on topic shifts (simplified implementation)
        /// </summary>
        private List<Chunk> SemanticChunk(string text)
        {
            // For semantic chunking, fall back to sentence-based with stricter boundaries
            // In production, this would use embeddings to detect topic shifts
            return SentenceChunk(text);
        }

        /// <summary>
        /// Find the best sentence boundary near the target position
        /// </summary>
        private int FindSentenceBoundary(string text, int start, int targetEnd)
        {
            if (targetEnd >= text.Length)
                return text.Length;

            // Look for sentence ending near target
            int searchStart = Math.Max(start, targetEnd - 100);
            int searchEnd = Math.Min(text.Length, targetEnd + 100);
            string searchText = text.Substring(searchStart, searchEnd - searchStart);

            var matches = _sentenceEndings.Matches(searchText);

            if (matches.Count > 0)
            {
                // Find closest match to target
                int targetOffset = targetEnd - searchStart;
                var closestMatch = matches
                    .Cast<Match>()
                    .OrderBy(m => Math.Abs(m.Index - targetOffset))
                    .First();

                return searchStart + closestMatch.Index + closestMatch.Length;
            }

            return targetEnd;
        }

        /// <summary>
        /// Split text into sentences
        /// </summary>
        private List<string> SplitIntoSentences(string text)
        {
            var sentences = new List<string>();
            var matches = _sentenceEndings.Matches(text);

            int lastIndex = 0;
            foreach (Match match in matches)
            {
                int endIndex = match.Index + match.Length;
                string sentence = text.Substring(lastIndex, endIndex - lastIndex).Trim();
                if (!string.IsNullOrWhiteSpace(sentence))
                {
                    sentences.Add(sentence);
                }
                lastIndex = endIndex;
            }

            // Add remaining text
            if (lastIndex < text.Length)
            {
                string remaining = text.Substring(lastIndex).Trim();
                if (!string.IsNullOrWhiteSpace(remaining))
                {
                    sentences.Add(remaining);
                }
            }

            return sentences;
        }
    }
}
