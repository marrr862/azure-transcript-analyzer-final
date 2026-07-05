using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class ConversationTurn
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "Speaker 1";

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}
