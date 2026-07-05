using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class AnalyzeRequest
{
    [JsonPropertyName("transcript")]
    public string? Transcript { get; init; }

    [JsonPropertyName("transcriptText")]
    public string? TranscriptText { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; } = "auto";

    public string GetTranscript() => Transcript ?? TranscriptText ?? string.Empty;
}
