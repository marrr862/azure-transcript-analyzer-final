using Azure.Core;
using Azure.Identity;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed partial class TranscriptTranslationService(
    IHttpClientFactory httpClientFactory,
    ConfigurationService configuration,
    TranscriptChunkingService chunking,
    AiConcurrencyLimiter aiConcurrencyLimiter,
    ILogger<TranscriptTranslationService> logger)
{
    private const string SystemPrompt = """
    Translate the transcript to English.

    The transcript may be English, Armenian, or mixed Armenian-English.
    When the transcript is mixed, translate the Armenian parts to natural English and keep existing English terms in place when they carry the exact meaning.
    Preserve the meaning and all explicit facts.
    Preserve IDs, phone numbers, emails, dates, addresses, medication names, doctor names, and person names as accurately as possible.
    Preserve call-center speaker labels as Agent: and Caller: when they are present or clear.
    Return only the translated transcript text. Do not add explanations or markdown.
    """;

    public async Task<TranslationResult> TranslateToEnglishAsync(
        string transcript,
        string detectedLanguage,
        CancellationToken cancellationToken)
    {
        if (detectedLanguage == "en" && !ContainsArmenian(transcript))
        {
            return new TranslationResult(transcript, "none", null);
        }

        if (!configuration.AzureOpenAiConfigured)
        {
            return new TranslationResult(
                transcript,
                "none",
                "Azure OpenAI translation is not configured; analyzed the original transcript");
        }

        var chunks = chunking.Split(transcript, includeOverlap: false);
        var results = await RunWithBoundedParallelismAsync(
            chunks,
            configuration.MaxParallelAiCalls,
            chunk => TryTranslateChunkAsync(chunk, cancellationToken),
            cancellationToken);

        var translatedChunks = new List<string>(results.Length);
        foreach (var (translatedChunk, warning) in results)
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                return new TranslationResult(transcript, "none", warning);
            }

            translatedChunks.Add(translatedChunk);
        }

        return new TranslationResult(string.Join("\n\n", translatedChunks), "openai", null);
    }

    private static async Task<TResult[]> RunWithBoundedParallelismAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        int maxParallelism,
        Func<TItem, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        var results = new TResult[items.Count];
        using var semaphore = new SemaphoreSlim(maxParallelism);

        var tasks = items.Select(async (item, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                results[index] = await work(item);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<(string TranslatedText, string? Warning)> TryTranslateChunkAsync(
        TranscriptChunk chunk,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = configuration.AzureOpenAiEndpoint.TrimEnd('/');
            var url = $"{endpoint}/responses";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            await ApplyAuthorizationAsync(request, cancellationToken);

            request.Content = JsonContent(new
            {
                model = configuration.AzureOpenAiDeployment,
                input = SystemPrompt + "\n\nTranscript chunk:\n" + chunk.Text,
                max_output_tokens = Math.Max(1200, Math.Min(6000, chunk.Text.Length * 2))
            });

            var client = httpClientFactory.CreateClient();
            using var response = await aiConcurrencyLimiter.RunAsync(
                "Azure OpenAI translation",
                token => client.SendAsync(request, token),
                cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Azure OpenAI translation failed for chunk {Chunk} with status {StatusCode}. Response: {ResponseText}",
                    chunk.Index + 1,
                    response.StatusCode,
                    responseText);

                return (chunk.Text, $"Azure OpenAI translation failed for chunk {chunk.Index + 1} with status {(int)response.StatusCode}");
            }

            var translated = ExtractTextFromResponsesApi(responseText);
            if (string.IsNullOrWhiteSpace(translated))
            {
                return (chunk.Text, $"Azure OpenAI translation returned empty content for chunk {chunk.Index + 1}");
            }

            return (translated.Trim(), null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure OpenAI translation failed for chunk {Chunk}", chunk.Index + 1);
            return (chunk.Text, $"Azure OpenAI translation failed for chunk {chunk.Index + 1}: {ex.Message}");
        }
    }

    private async Task ApplyAuthorizationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ShouldUseApiKeyAuth()
            && !string.IsNullOrWhiteSpace(configuration.AzureOpenAiKey))
        {
            request.Headers.Add("api-key", configuration.AzureOpenAiKey);
            return;
        }

        var credential = new DefaultAzureCredential();
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://ai.azure.com/.default"]),
            cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private bool ShouldUseApiKeyAuth()
    {
        var endpoint = configuration.AzureOpenAiEndpoint.Trim().ToLowerInvariant();
        return endpoint.Contains(".openai.azure.com", StringComparison.Ordinal);
    }

    private static string? ExtractTextFromResponsesApi(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputTextElement)
            && outputTextElement.ValueKind == JsonValueKind.String)
        {
            return outputTextElement.GetString();
        }

        if (root.TryGetProperty("output", out var outputElement)
            && outputElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var outputItem in outputElement.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("content", out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in contentElement.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String)
                    {
                        return textElement.GetString();
                    }
                }
            }
        }

        return null;
    }

    private static bool ContainsArmenian(string transcript) => ArmenianRegex().IsMatch(transcript);

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [GeneratedRegex(@"\p{IsArmenian}")]
    private static partial Regex ArmenianRegex();
}

public sealed record TranslationResult(
    string Transcript,
    string Method,
    string? Warning);
