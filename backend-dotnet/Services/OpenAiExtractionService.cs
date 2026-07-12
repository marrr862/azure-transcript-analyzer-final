using Azure.Core;
using Azure.Identity;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class OpenAiExtractionService(
    IHttpClientFactory httpClientFactory,
    ConfigurationService configuration,
    AiConcurrencyLimiter aiConcurrencyLimiter,
    ILogger<OpenAiExtractionService> logger)
{
    private const string SystemPrompt = """
    You are a medical/call-center transcript information extractor.

    Extract only facts explicitly present in the transcript chunk.
    Do not infer or invent missing data.
    Preserve names, addresses, IDs, medications, and conditions exactly as written.
    Preserve every extracted value in the same language and writing system as it appears in the transcript. Do not translate Armenian values to English.
    For importantDetails, choose only concise facts that matter in this specific conversation: urgent needs, requested actions, decisions, preferences, symptoms, problems, deadlines, next steps, risks, account/medical context, or promises.
    Do not add generic filler. Do not create categories. Do not repeat values already captured in fixed fields unless the surrounding context is important.
    Limit each array to at most 8 items. Keep every array item under 120 characters.
    Return only valid minified JSON. Do not use markdown.

    Required JSON shape:
    {"name":"","address":"","dateOfBirth":"","socialSecurityNumber":"","phoneNumber":"","email":"","doctorName":"","conditions":[],"medications":[],"other":[],"importantDetails":[]}
    """;

    private const string JsonRetryPrompt = """
    Your previous extraction response was not valid complete JSON.

    Return one complete minified JSON object only.
    Use the exact required shape.
    Limit arrays to at most 8 short strings.
    Do not include markdown, explanations, comments, or trailing text.

    Required JSON shape:
    {"name":"","address":"","dateOfBirth":"","socialSecurityNumber":"","phoneNumber":"","email":"","doctorName":"","conditions":[],"medications":[],"other":[],"importantDetails":[]}
    """;

    private const string ConsolidationPrompt = """
    Clean and consolidate extracted transcript attributes.

    You receive JSON already extracted from transcript chunks.
    Keep only explicit facts from that JSON. Do not invent anything.
    Remove duplicates, remove generic filler, and keep the most complete useful values.
    Preserve every value in its original language and writing system. Do not translate Armenian values to English or English values to Armenian.
    For importantDetails, keep concise facts that matter to the call: urgent needs, requested actions, decisions, symptoms, deadlines, next steps, risks, promises, and account/medical context.
    Limit each array to at most 8 items. Keep every array item under 120 characters.
    Return only valid minified JSON with the exact same shape.
    """;

    private const string ConsolidationRetryPrompt = """
    Your previous consolidation response was not valid complete JSON.

    Return one complete minified JSON object only.
    Use the exact required shape.
    Limit arrays to at most 8 short strings.
    Do not include markdown, explanations, comments, or trailing text.

    Required JSON shape:
    {"name":"","address":"","dateOfBirth":"","socialSecurityNumber":"","phoneNumber":"","email":"","doctorName":"","conditions":[],"medications":[],"other":[],"importantDetails":[]}
    """;

    public async Task<(IReadOnlyList<ExtractedAttributes> Attributes, IReadOnlyList<string> Warnings)> ExtractChunksAsync(
        IReadOnlyList<TranscriptChunk> chunks,
        CancellationToken cancellationToken)
    {
        if (!configuration.AzureOpenAiConfigured)
        {
            return ([], ["Azure OpenAI is not configured"]);
        }

        var results = await RunWithBoundedParallelismAsync(
            chunks,
            configuration.MaxParallelAiCalls,
            chunk => TryExtractChunkAsync(chunk, cancellationToken),
            cancellationToken);

        var attributes = new List<ExtractedAttributes>();
        var warnings = new List<string>();

        foreach (var (chunkAttributes, warning) in results)
        {
            if (chunkAttributes is not null)
            {
                attributes.Add(chunkAttributes);
            }

            if (!string.IsNullOrWhiteSpace(warning))
            {
                warnings.Add(warning);
            }
        }

        return (attributes, warnings);
    }

    public async Task<(ExtractedAttributes Attributes, string? Warning)> ConsolidateAttributesAsync(
        ExtractedAttributes attributes,
        CancellationToken cancellationToken)
    {
        if (!configuration.AzureOpenAiConfigured)
        {
            return (attributes, null);
        }

        try
        {
            var content = await SendJsonObjectRequestAsync(
                ConsolidationPrompt,
                "Extracted attributes JSON",
                JsonSerializer.Serialize(attributes),
                maxOutputTokens: 4000,
                cancellationToken);

            if (TryParseAttributes(content, out var consolidated, out var parseError))
            {
                return (consolidated, null);
            }

            logger.LogWarning(
                "Azure OpenAI attribute consolidation returned invalid JSON. Parse error: {ParseError}",
                parseError);

            var retryContent = await SendJsonObjectRequestAsync(
                ConsolidationRetryPrompt,
                "Extracted attributes JSON",
                JsonSerializer.Serialize(attributes),
                maxOutputTokens: 4000,
                cancellationToken);

            return TryParseAttributes(retryContent, out var retryConsolidated, out var retryParseError)
                ? (retryConsolidated, null)
                : LogAndKeepMergedAttributes(attributes, retryParseError);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure OpenAI attribute consolidation failed");
            return (attributes, null);
        }
    }

    private (ExtractedAttributes Attributes, string? Warning) LogAndKeepMergedAttributes(
        ExtractedAttributes attributes,
        string? parseError)
    {
        logger.LogWarning(
            "Azure OpenAI attribute consolidation retry returned invalid JSON. Parse error: {ParseError}",
            parseError);

        return (attributes, null);
    }

    private async Task<(ExtractedAttributes? Attributes, string? Warning)> TryExtractChunkAsync(
        TranscriptChunk chunk,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await SendExtractionRequestAsync(
                SystemPrompt,
                chunk.Text,
                maxOutputTokens: 4000,
                cancellationToken);

            var attributes = TryParseAttributes(content, out var parsed, out var parseError)
                ? parsed
                : await RetryParseAttributesAsync(chunk, parseError, cancellationToken);

            return (attributes, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure OpenAI extraction failed for chunk {Chunk}", chunk.Index + 1);
            return (null, $"Azure OpenAI extraction failed for chunk {chunk.Index + 1}: {ex.Message}");
        }
    }

    private async Task<ExtractedAttributes> RetryParseAttributesAsync(
        TranscriptChunk chunk,
        string? parseError,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Azure OpenAI extraction returned invalid JSON for chunk {Chunk}. Retrying. Parse error: {ParseError}",
            chunk.Index + 1,
            parseError);

        var retryContent = await SendExtractionRequestAsync(
            JsonRetryPrompt,
            chunk.Text,
            maxOutputTokens: 4000,
            cancellationToken);

        return ParseAttributes(retryContent);
    }

    private async Task<string> SendExtractionRequestAsync(
        string prompt,
        string transcriptChunk,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        return await SendJsonObjectRequestAsync(
            prompt,
            "Transcript chunk",
            transcriptChunk,
            maxOutputTokens,
            cancellationToken);
    }

    private async Task<string> SendJsonObjectRequestAsync(
        string prompt,
        string inputLabel,
        string inputValue,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var endpoint = configuration.AzureOpenAiEndpoint.TrimEnd('/');
        var url = $"{endpoint}/responses";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        await ApplyAuthorizationAsync(request, cancellationToken);

        request.Content = JsonContent(new
        {
            model = configuration.AzureOpenAiDeployment,
            input = prompt + "\n\n" + inputLabel + ":\n" + inputValue,
            text = new
            {
                format = new
                {
                    type = "json_object"
                }
            },
            max_output_tokens = maxOutputTokens
        });

        var client = httpClientFactory.CreateClient();
        using var response = await aiConcurrencyLimiter.RunAsync(
            "Azure OpenAI extraction",
            token => client.SendAsync(request, token),
            cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Azure OpenAI extraction failed with status {StatusCode}. Response: {ResponseText}",
                response.StatusCode,
                responseText);

            throw new InvalidOperationException($"Azure OpenAI extraction failed with status {(int)response.StatusCode}");
        }

        var content = ExtractTextFromResponsesApi(responseText);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Azure OpenAI extraction returned empty content");
        }

        return content;
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

    private static ExtractedAttributes ParseAttributes(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        return new ExtractedAttributes
        {
            Name = ReadString(root, "name"),
            Address = ReadString(root, "address"),
            DateOfBirth = ReadString(root, "dateOfBirth"),
            SocialSecurityNumber = ReadString(root, "socialSecurityNumber"),
            PhoneNumber = ReadString(root, "phoneNumber"),
            Email = ReadString(root, "email"),
            DoctorName = ReadString(root, "doctorName"),
            Conditions = ReadStringArray(root, "conditions"),
            Medications = ReadStringArray(root, "medications"),
            Other = ReadStringArray(root, "other"),
            ImportantDetails = ReadStringArray(root, "importantDetails")
        };
    }

    private static bool TryParseAttributes(
        string content,
        out ExtractedAttributes attributes,
        out string? parseError)
    {
        try
        {
            attributes = ParseAttributes(content);
            parseError = null;
            return true;
        }
        catch (JsonException ex)
        {
            attributes = new ExtractedAttributes();
            parseError = ex.Message;
            return false;
        }
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim() ?? string.Empty
            : element.ToString().Trim();
    }

    private static List<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

        if (root.TryGetProperty("choices", out var choicesElement)
            && choicesElement.ValueKind == JsonValueKind.Array
            && choicesElement.GetArrayLength() > 0)
        {
            var firstChoice = choicesElement[0];

            if (firstChoice.TryGetProperty("message", out var messageElement)
                && messageElement.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                return contentElement.GetString();
            }
        }

        return null;
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
