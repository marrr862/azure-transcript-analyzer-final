using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class TranscriptChunkingService(ConfigurationService configuration)
{
    public IReadOnlyList<TranscriptChunk> Split(string transcript, bool includeOverlap = true)
    {
        var chunkSize = Math.Max(500, configuration.TranscriptChunkSize);
        var overlap = includeOverlap
            ? Math.Clamp(configuration.TranscriptChunkOverlap, 0, chunkSize / 4)
            : 0;

        if (transcript.Length <= chunkSize)
        {
            return [new TranscriptChunk { Index = 0, Start = 0, End = transcript.Length, Text = transcript }];
        }

        var chunks = new List<TranscriptChunk>();
        var start = 0;

        while (start < transcript.Length)
        {
            var targetEnd = Math.Min(start + chunkSize, transcript.Length);
            var end = targetEnd == transcript.Length
                ? targetEnd
                : FindBoundary(transcript, start, targetEnd);

            if (end <= start)
            {
                end = targetEnd;
            }

            chunks.Add(new TranscriptChunk
            {
                Index = chunks.Count,
                Start = start,
                End = end,
                Text = transcript[start..end].Trim()
            });

            if (end >= transcript.Length)
            {
                break;
            }

            var nextStart = Math.Max(0, end - overlap);
            start = nextStart > start ? nextStart : end;
        }

        return chunks
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
            .ToList();
    }

    private static int FindBoundary(string text, int start, int targetEnd)
    {
        var minBoundary = start + Math.Max(100, (targetEnd - start) / 2);
        var boundaryChars = new[] { '\n', '.', '!', '?', '։', ' ' };

        for (var i = targetEnd - 1; i >= minBoundary; i--)
        {
            if (boundaryChars.Contains(text[i]))
            {
                return i + 1;
            }
        }

        return targetEnd;
    }
}
