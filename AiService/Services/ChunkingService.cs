using AiService.Models;
using System.Text.RegularExpressions;

namespace AiService.Services
{
    public class ChunkingService
    {
        // Sentence delimiters for Arabic and English
        private static readonly char[] SentenceDelimiters = { '.', '!', '?', '؟', '!', '۔', '।' };

        /// <summary>
        /// Creates fixed-length chunks from cleaned text
        /// </summary>
        public List<Chunk_NEW> CreateFixedLengthChunks(
            string cleanedText,
            int chunkSizeInChars = 6500,
            int overlapChars = 600)
        {
            var chunks = new List<Chunk_NEW>();

            if (string.IsNullOrWhiteSpace(cleanedText))
                return chunks;

            // If text is smaller than chunk size, return as single chunk
            if (cleanedText.Length <= chunkSizeInChars)
            {
                var singleChunk = CreateChunk(
                    text: cleanedText,
                    chunkIndex: 0,
                    isFirst: true,
                    isLast: true,
                    startPosition: 0
                );
                chunks.Add(singleChunk);
                return chunks;
            }

            int startPosition = 0;
            int chunkIndex = 0;

            while (startPosition < cleanedText.Length)
            {
                // Calculate end position for this chunk
                int endPosition = Math.Min(startPosition + chunkSizeInChars, cleanedText.Length);

                // If this is not the last chunk, try to find a good split point
                if (endPosition < cleanedText.Length)
                {
                    int splitPoint = FindBestSplitPoint(cleanedText, startPosition, endPosition);
                    endPosition = splitPoint;
                }

                // Extract chunk text
                int chunkLength = endPosition - startPosition;
                string chunkText = cleanedText.Substring(startPosition, chunkLength).Trim();

                // Skip empty chunks
                if (string.IsNullOrWhiteSpace(chunkText))
                {
                    startPosition = endPosition;
                    continue;
                }

                // Create chunk object
                bool isFirst = chunkIndex == 0;
                bool isLast = endPosition >= cleanedText.Length;

                var chunk = CreateChunk(
                    text: chunkText,
                    chunkIndex: chunkIndex,
                    isFirst: isFirst,
                    isLast: isLast,
                    startPosition: startPosition
                );

                chunks.Add(chunk);
                chunkIndex++;

                // Move to next chunk with overlap
                startPosition = endPosition - overlapChars;

                // Ensure we make progress (avoid infinite loop)
                if (startPosition + chunkSizeInChars <= endPosition)
                {
                    startPosition = endPosition;
                }
            }

            // Link chunks together (set PreviousChunkId and NextChunkId)
            LinkChunks(chunks);

            return chunks;
        }

        /// <summary>
        /// Finds the best point to split text (at sentence or word boundary)
        /// </summary>
        private int FindBestSplitPoint(string text, int startPosition, int maxEndPosition)
        {
            int searchStart = Math.Max(startPosition, maxEndPosition - 1000);

            // Search backwards for sentence delimiter
            for (int i = maxEndPosition - 1; i >= searchStart; i--)
            {
                if (SentenceDelimiters.Contains(text[i]))
                {
                    // Move past the delimiter and any following whitespace
                    int splitPoint = i + 1;
                    while (splitPoint < text.Length && char.IsWhiteSpace(text[splitPoint]))
                    {
                        splitPoint++;
                    }
                    return splitPoint;
                }
            }

            // No sentence boundary found, look for word boundary (space)
            for (int i = maxEndPosition - 1; i >= searchStart; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    // Skip consecutive whitespace
                    while (i < text.Length && char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }
                    return i;
                }
            }

            // Fallback: hard cut at maxEndPosition
            return maxEndPosition;
        }

        /// <summary>
        /// Creates a single Chunk_NEW object with all metadata
        /// </summary>
        private Chunk_NEW CreateChunk(
            string text,
            int chunkIndex,
            bool isFirst,
            bool isLast,
            int startPosition)
        {
            // Generate ChunkId
            string chunkId = $"chunk_{chunkIndex:D4}";

            // Calculate line numbers (approximate based on newlines in text)
            int startLine = startPosition > 0 ? 0 : 0; // You can enhance this if needed
            int endLine = CountLinesInText(text);

            // Extract key phrases
            var keyPhrases = ExtractKeyPhrases(text);

            // Estimate tokens (Arabic/English mixed average: ~3 chars per token)
            int approxTokens = EstimateTokens(text);

            return new Chunk_NEW
            {
                file_name = string.Empty,
                Text = text,
                PageNumber = 0,
                Section = string.Empty,
                SubSection = string.Empty,
                ApproxTokens = approxTokens,
                StartLine = startLine,
                EndLine = endLine,
                ChunkId = chunkId,
                ChunkType = "text",
                HierarchyLevel = 1,
                CharacterCount = text.Length,
                HasOverlapWithPrevious = !isFirst,
                HasOverlapWithNext = !isLast,
                KeyPhrases = keyPhrases,
                PreviousChunkId = null, // Will be set in LinkChunks
                NextChunkId = null       // Will be set in LinkChunks
            };
        }

        /// <summary>
        /// Links chunks together by setting Previous/Next ChunkIds
        /// </summary>
        private void LinkChunks(List<Chunk_NEW> chunks)
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                if (i > 0)
                {
                    chunks[i].PreviousChunkId = chunks[i - 1].ChunkId;
                }

                if (i < chunks.Count - 1)
                {
                    chunks[i].NextChunkId = chunks[i + 1].ChunkId;
                }
            }
        }

        /// <summary>
        /// Counts lines in text
        /// </summary>
        private int CountLinesInText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            return text.Count(c => c == '\n') + 1;
        }

        /// <summary>
        /// Estimates token count (Arabic/English mixed: ~3 chars per token)
        /// </summary>
        private int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            // Mixed Arabic/English average
            return (int)Math.Ceiling(text.Length / 3.0);
        }

        /// <summary>
        /// Extracts key phrases from text (first sentence or first 150 chars)
        /// </summary>
        private List<string> ExtractKeyPhrases(string text)
        {
            var phrases = new List<string>();

            if (string.IsNullOrWhiteSpace(text))
                return phrases;

            // Extract first sentence as key phrase
            int firstSentenceEnd = -1;
            foreach (var delimiter in SentenceDelimiters)
            {
                int pos = text.IndexOf(delimiter);
                if (pos > 0 && (firstSentenceEnd == -1 || pos < firstSentenceEnd))
                {
                    firstSentenceEnd = pos;
                }
            }

            if (firstSentenceEnd > 0)
            {
                string firstSentence = text.Substring(0, firstSentenceEnd + 1).Trim();
                if (firstSentence.Length > 10) // Minimum length check
                {
                    phrases.Add(firstSentence);
                }
            }
            else
            {
                // No sentence delimiter found, take first 150 characters
                int length = Math.Min(150, text.Length);
                phrases.Add(text.Substring(0, length).Trim());
            }

            // Optional: Extract important words (words longer than 5 chars)
            var words = Regex.Matches(text, @"\b\w{6,}\b")
                .Cast<Match>()
                .Select(m => m.Value)
                .Distinct()
                .Take(5)
                .ToList();

            phrases.AddRange(words);

            return phrases;
        }
        public List<Chunk_NEW> ChunkText(string cleanedText, int chunkSize = 800, int overlap = 100)
        {
            var chunks = new List<Chunk_NEW>();

            if (string.IsNullOrWhiteSpace(cleanedText))
                return chunks;

            int chunkIndex = 0;
            int step = chunkSize - overlap;

            for (int i = 0; i < cleanedText.Length; i += step)
            {
                int length = Math.Min(chunkSize, cleanedText.Length - i);
                string chunkText = cleanedText.Substring(i, length);

                chunks.Add(new Chunk_NEW
                {
                    Text = chunkText,
                    ChunkId = $"chunk_{chunkIndex:D4}",
                    CharacterCount = chunkText.Length,
                    ChunkType="list",
                    ApproxTokens = chunkText.Length / 3
                });

                chunkIndex++;
            }

            return chunks;
        }
    }
}
