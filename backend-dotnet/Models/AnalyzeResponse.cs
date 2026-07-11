using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class AnalyzeResponse
{
    [JsonPropertyName("conversation")]
    public IReadOnlyList<ConversationTurn> Conversation { get; init; } = [];

    [JsonPropertyName("extractedAttributes")]
    public ExtractedAttributes ExtractedAttributes { get; init; } = new();

    [JsonPropertyName("rawAzureEntities")]
    public IReadOnlyList<RawAzureEntity> RawAzureEntities { get; init; } = [];

    [JsonPropertyName("attributeEvidence")]
    public IReadOnlyList<AttributeEvidence> AttributeEvidence { get; init; } = [];

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }

    [JsonPropertyName("roleMethod")]
    public string RoleMethod { get; init; } = "fallback";

    [JsonPropertyName("detectedLanguage")]
    public string DetectedLanguage { get; init; } = "auto";

    [JsonPropertyName("translationMethod")]
    public string TranslationMethod { get; init; } = "none";

    [JsonPropertyName("translatedTranscript")]
    public string? TranslatedTranscript { get; init; }
}
