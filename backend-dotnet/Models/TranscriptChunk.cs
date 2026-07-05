namespace TranscriptAnalyzer.Models;

public sealed class TranscriptChunk
{
    public int Index { get; init; }

    public int Start { get; init; }

    public int End { get; init; }

    public string Text { get; init; } = string.Empty;
}
