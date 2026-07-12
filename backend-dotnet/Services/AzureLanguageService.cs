using System.Text;
using System.Text.Json;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class AzureLanguageService(
    IHttpClientFactory httpClientFactory,
    ConfigurationService configuration,
    AiConcurrencyLimiter aiConcurrencyLimiter,
    ILogger<AzureLanguageService> logger)
{
    private static readonly Dictionary<string, string> CategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Person"] = nameof(ExtractedAttributes.Name),
        ["Address"] = nameof(ExtractedAttributes.Address),
        ["PhoneNumber"] = nameof(ExtractedAttributes.PhoneNumber),
        ["Email"] = nameof(ExtractedAttributes.Email),
        ["USSocialSecurityNumber"] = nameof(ExtractedAttributes.SocialSecurityNumber),
        ["EUPassportNumber"] = nameof(ExtractedAttributes.SocialSecurityNumber),
        ["InternationalBankingAccountNumber"] = "Other",
        ["Organization"] = "Other",
        ["URL"] = "Other",
        ["IPAddress"] = "Other",
        ["Age"] = "Other",
        ["Quantity"] = "Other",
        ["CreditCardNumber"] = "Other",
        ["BankAccountNumber"] = "Other"
    };

    public async Task<(IReadOnlyList<ExtractedAttributes> Attributes, IReadOnlyList<RawAzureEntity> RawEntities, IReadOnlyList<string> Warnings)> AnalyzeChunksAsync(
        IReadOnlyList<TranscriptChunk> chunks,
        string? language,
        CancellationToken cancellationToken)
    {
        if (!configuration.AzureLanguageConfigured)
        {
            return ([], [], ["Azure AI Language is not configured"]);
        }

        var results = await RunWithBoundedParallelismAsync(
            chunks,
            configuration.MaxParallelAiCalls,
            chunk => AnalyzeChunkAsync(chunk, language, cancellationToken),
            cancellationToken);

        var attributes = new List<ExtractedAttributes>(results.Length);
        var rawEntities = new List<RawAzureEntity>();
        var warnings = new List<string>();

        foreach (var (chunkAttributes, chunkRawEntities, chunkWarning) in results)
        {
            attributes.Add(chunkAttributes);
            rawEntities.AddRange(chunkRawEntities);

            if (!string.IsNullOrWhiteSpace(chunkWarning))
            {
                warnings.Add(chunkWarning);
            }
        }

        return (attributes, rawEntities, warnings);
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

    private async Task<(ExtractedAttributes Attributes, IReadOnlyList<RawAzureEntity> RawEntities, string? Warning)> AnalyzeChunkAsync(
        TranscriptChunk chunk,
        string? language,
        CancellationToken cancellationToken)
    {
        if (!configuration.AzureLanguageConfigured)
        {
            return (new ExtractedAttributes(), [], "Azure AI Language is not configured");
        }

        try
        {
            var endpoint = configuration.AzureLanguageEndpoint.TrimEnd('/');
            var url = $"{endpoint}/language/:analyze-text?api-version=2023-04-01";
            var document = new Dictionary<string, object?>
            {
                ["id"] = chunk.Index.ToString(),
                ["text"] = chunk.Text.Length > 5120 ? chunk.Text[..5120] : chunk.Text
            };

            var azureLanguage = NormalizeAzureLanguage(language);
            if (!string.IsNullOrWhiteSpace(azureLanguage))
            {
                document["language"] = azureLanguage;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Ocp-Apim-Subscription-Key", configuration.AzureLanguageKey);
            request.Content = JsonContent(new
            {
                kind = "PiiEntityRecognition",
                parameters = new { modelVersion = "latest" },
                analysisInput = new { documents = new[] { document } }
            });

            var client = httpClientFactory.CreateClient();
            using var response = await aiConcurrencyLimiter.RunAsync(
                "Azure Language entity analysis",
                token => client.SendAsync(request, token),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Azure Language failed with status {StatusCode}", response.StatusCode);
                return (new ExtractedAttributes(), [], $"Azure AI Language failed for chunk {chunk.Index + 1} with status {(int)response.StatusCode}");
            }

            using var responseDocument = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            var result = responseDocument.RootElement
                .GetProperty("results")
                .GetProperty("documents")[0];

            var attrs = new ExtractedAttributes();
            var raw = new List<RawAzureEntity>();
            var bestConfidence = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(ExtractedAttributes.Name)] = -1,
                [nameof(ExtractedAttributes.Address)] = -1,
                [nameof(ExtractedAttributes.PhoneNumber)] = -1,
                [nameof(ExtractedAttributes.Email)] = -1,
                [nameof(ExtractedAttributes.SocialSecurityNumber)] = -1
            };

            foreach (var entity in result.GetProperty("entities").EnumerateArray())
            {
                var text = entity.GetProperty("text").GetString() ?? "";
                var category = entity.GetProperty("category").GetString() ?? "";
                var subcategory = entity.TryGetProperty("subcategory", out var subcategoryElement)
                    ? subcategoryElement.GetString()
                    : null;
                var confidence = entity.TryGetProperty("confidenceScore", out var confidenceElement)
                    ? confidenceElement.GetDouble()
                    : 0;

                raw.Add(new RawAzureEntity
                {
                    Text = text,
                    Category = category,
                    Subcategory = subcategory,
                    Confidence = Math.Round(confidence, 3)
                });

                if (confidence < 0.6 || !CategoryMap.TryGetValue(category, out var field))
                {
                    continue;
                }

                if (field == "Other")
                {
                    var label = $"{category}: {text}";
                    if (!attrs.Other.Contains(label, StringComparer.OrdinalIgnoreCase))
                    {
                        attrs.Other.Add(label);
                    }

                    continue;
                }

                if (confidence <= bestConfidence[field])
                {
                    continue;
                }

                bestConfidence[field] = confidence;
                SetField(attrs, field, text);
            }

            return (attrs, raw, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure Language analysis failed");
            return (new ExtractedAttributes(), [], $"Azure AI Language failed for chunk {chunk.Index + 1}: {ex.Message}");
        }
    }

    private static string? NormalizeAzureLanguage(string? language)
    {
        return language?.Trim().ToLowerInvariant() switch
        {
            "en" => "en",
            "hy" => "hy",
            _ => null
        };
    }

    private static void SetField(ExtractedAttributes attrs, string field, string value)
    {
        switch (field)
        {
            case nameof(ExtractedAttributes.Name):
                attrs.Name = value;
                break;
            case nameof(ExtractedAttributes.Address):
                attrs.Address = value;
                break;
            case nameof(ExtractedAttributes.PhoneNumber):
                attrs.PhoneNumber = value;
                break;
            case nameof(ExtractedAttributes.Email):
                attrs.Email = value;
                break;
            case nameof(ExtractedAttributes.SocialSecurityNumber):
                attrs.SocialSecurityNumber = value;
                break;
        }
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
