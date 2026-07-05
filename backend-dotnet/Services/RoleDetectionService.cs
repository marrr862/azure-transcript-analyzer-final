using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed partial class RoleDetectionService(
    IHttpClientFactory httpClientFactory,
    ConfigurationService configuration,
    ILogger<RoleDetectionService> logger)
{
    private const string SystemPrompt = """
        You are a call-center transcript analyzer.
        Split the transcript into conversation turns with roles "Agent", "Caller", or "Unknown".
        Preserve original wording and language. Return only JSON:
        {"turns":[{"role":"Agent","text":"..."},{"role":"Caller","text":"..."}]}
        """;

    public async Task<(IReadOnlyList<ConversationTurn> Turns, string Method)> DetectAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        if (ContainsExplicitLabels(transcript))
        {
            return (ParseExplicitLabels(transcript), "labels");
        }

        var openAiTurns = await TryDetectWithOpenAiAsync(transcript, cancellationToken);
        if (openAiTurns.Count > 0 && openAiTurns.All(turn => turn.Role is "Agent" or "Caller"))
        {
            return (openAiTurns, "openai");
        }

        return (SpeakerFallback(transcript), "fallback");
    }

    private static bool ContainsExplicitLabels(string transcript)
    {
        return SplitCandidateTurns(transcript).Any(line => ExplicitLabelRegex().IsMatch(line));
    }

    private static IReadOnlyList<ConversationTurn> ParseExplicitLabels(string transcript)
    {
        var turns = new List<ConversationTurn>();
        foreach (var line in SplitCandidateTurns(transcript))
        {
            var match = ExplicitLabelRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var role = NormalizeExplicitRole(match.Groups[1].Value);
            var text = ExplicitLabelRegex().Replace(line, "").Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                turns.Add(new ConversationTurn { Role = role, Text = text });
            }
        }

        return turns.Count > 0 ? turns : SpeakerFallback(transcript);
    }

    private async Task<IReadOnlyList<ConversationTurn>> TryDetectWithOpenAiAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        if (!configuration.AzureOpenAiConfigured)
        {
            return [];
        }

        try
        {
            var endpoint = configuration.AzureOpenAiEndpoint.TrimEnd('/');
            var deployment = Uri.EscapeDataString(configuration.AzureOpenAiDeployment);
            var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={configuration.AzureOpenAiApiVersion}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("api-key", configuration.AzureOpenAiKey);
            request.Content = JsonContent(new
            {
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = transcript }
                },
                max_completion_tokens = 4000,
                response_format = new { type = "json_object" }
            });

            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Azure OpenAI role detection failed with status {StatusCode}", response.StatusCode);
                return [];
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                return [];
            }

            using var roleDocument = JsonDocument.Parse(content);
            var turnsElement = roleDocument.RootElement.TryGetProperty("turns", out var turns)
                ? turns
                : roleDocument.RootElement;

            if (turnsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var parsed = new List<ConversationTurn>();
            foreach (var item in turnsElement.EnumerateArray())
            {
                var role = item.TryGetProperty("role", out var roleElement)
                    ? roleElement.GetString()
                    : "Unknown";
                var text = item.TryGetProperty("text", out var textElement)
                    ? textElement.GetString()
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    parsed.Add(new ConversationTurn
                    {
                        Role = role is "Agent" or "Caller" ? role : "Unknown",
                        Text = text.Trim()
                    });
                }
            }

            return parsed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure OpenAI role detection failed");
            return [];
        }
    }

    private static IReadOnlyList<ConversationTurn> SpeakerFallback(string transcript)
    {
        var parts = SplitCandidateTurns(transcript).ToList();
        if (parts.Count == 0)
        {
            parts = SentenceRegex().Split(transcript)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();
        }

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(transcript))
        {
            parts.Add(transcript.Trim());
        }

        return parts.Select((text, index) => new ConversationTurn
        {
            Role = index % 2 == 0 ? "Speaker 1" : "Speaker 2",
            Text = text
        }).ToList();
    }

    private static IEnumerable<string> SplitCandidateTurns(string transcript)
    {
        var lines = transcript
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count > 1)
        {
            return lines;
        }

        return transcript
            .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(block => !string.IsNullOrWhiteSpace(block));
    }

    private static string NormalizeExplicitRole(string label)
    {
        var normalized = label.Trim().ToLowerInvariant();
        return normalized switch
        {
            "agent" or "representative" or "rep" or "support" or "operator" => "Agent",
            "գործակալ" or "օպերատոր" => "Agent",
            "caller" or "customer" => "Caller",
            "զանգահարող" or "հաճախորդ" => "Caller",
            _ => "Speaker 1"
        };
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [GeneratedRegex(@"^(agent|caller|customer|representative|rep|support|operator|Գործակալ|Զանգահարող|Հաճախորդ|Օպերատոր)\s*[:\-]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitLabelRegex();

    [GeneratedRegex(@"(?<=[.!?։])\s+")]
    private static partial Regex SentenceRegex();
}
