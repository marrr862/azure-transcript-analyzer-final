using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class RawAzureEntity
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("subcategory")]
    public string? Subcategory { get; init; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }
}
