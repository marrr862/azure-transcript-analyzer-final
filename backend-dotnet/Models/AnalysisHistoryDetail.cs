using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class AnalysisHistoryDetail
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("language")]
    public string Language { get; init; } = "auto";

    [JsonPropertyName("detectedLanguage")]
    public string DetectedLanguage { get; init; } = "auto";

    [JsonPropertyName("translationMethod")]
    public string TranslationMethod { get; init; } = "none";

    [JsonPropertyName("roleMethod")]
    public string RoleMethod { get; init; } = "fallback";

    [JsonPropertyName("transcriptLength")]
    public int TranscriptLength { get; init; }

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}
