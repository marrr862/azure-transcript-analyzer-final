using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class AnalyzeRequest
{
    [JsonPropertyName("transcriptText")]
    public string? TranscriptText { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; } = "auto";
}
