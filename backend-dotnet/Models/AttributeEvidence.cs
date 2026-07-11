using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class AttributeEvidence
{
    [JsonPropertyName("field")]
    public string Field { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("snippet")]
    public string Snippet { get; init; } = string.Empty;
}
