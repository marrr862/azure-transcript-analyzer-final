using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TranscriptAnalyzer.Services;

public sealed partial class LanguageDetectionService(
    IHttpClientFactory httpClientFactory,
    ConfigurationService configuration,
    AiConcurrencyLimiter aiConcurrencyLimiter,
    ILogger<LanguageDetectionService> logger)
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en",
        "hy",
        "mixed-en-hy"
    };

    public async Task<LanguageValidationResult> ValidateAsync(
        string transcript,
        string? requestedLanguage,
        CancellationToken cancellationToken)
    {
        var normalizedRequested = NormalizeLanguageCode(requestedLanguage);
        var scriptProfile = DetectScriptProfile(transcript);

        if (scriptProfile.HasSignificantCyrillic)
        {
            return LanguageValidationResult.Unsupported(
                "ru",
                "Russian",
                "Detected Russian/Cyrillic text. This analyzer currently supports English, Armenian, and mixed English-Armenian transcripts only.");
        }

        if (scriptProfile.HasSignificantEnglish && scriptProfile.HasSignificantArmenian)
        {
            return LanguageValidationResult.Supported("mixed-en-hy");
        }

        if (scriptProfile.HasSignificantEnglish && !scriptProfile.HasSignificantArmenian)
        {
            return LanguageValidationResult.Supported("en");
        }

        if (scriptProfile.HasSignificantArmenian && !scriptProfile.HasSignificantEnglish)
        {
            return LanguageValidationResult.Supported("hy");
        }

        var detection = await DetectAsync(transcript, cancellationToken);

        if (detection.Code is not null && !SupportedLanguages.Contains(detection.Code))
        {
            return LanguageValidationResult.Unsupported(
                detection.Code,
                detection.Name,
                $"Detected {detection.Name} text. This analyzer currently supports English and Armenian transcripts only.");
        }

        if (detection.Code is null && normalizedRequested == "auto")
        {
            return LanguageValidationResult.Unsupported(
                "unknown",
                "unknown language",
                "Could not confidently detect the transcript language. This analyzer currently supports English and Armenian transcripts only.");
        }

        var analysisLanguage = normalizedRequested is "en" or "hy"
            ? normalizedRequested
            : detection.Code ?? normalizedRequested;

        return LanguageValidationResult.Supported(analysisLanguage);
    }

    private async Task<DetectedLanguage> DetectAsync(string transcript, CancellationToken cancellationToken)
    {
        if (configuration.AzureLanguageConfigured)
        {
            var azureDetection = await TryDetectWithAzureAsync(transcript, cancellationToken);
            if (azureDetection.Code is not null)
            {
                return azureDetection;
            }
        }

        return DetectWithHeuristics(transcript);
    }

    private async Task<DetectedLanguage> TryDetectWithAzureAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = configuration.AzureLanguageEndpoint.TrimEnd('/');
            var url = $"{endpoint}/language/:analyze-text?api-version=2023-04-01";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Ocp-Apim-Subscription-Key", configuration.AzureLanguageKey);
            request.Content = JsonContent(new
            {
                kind = "LanguageDetection",
                analysisInput = new
                {
                    documents = new[]
                    {
                        new
                        {
                            id = "0",
                            text = transcript.Length > 5120 ? transcript[..5120] : transcript
                        }
                    }
                }
            });

            var client = httpClientFactory.CreateClient();
            using var response = await aiConcurrencyLimiter.RunAsync(
                "Azure Language detection",
                token => client.SendAsync(request, token),
                cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Azure Language detection failed with status {StatusCode}. Response: {ResponseText}",
                    response.StatusCode,
                    responseText);

                return new DetectedLanguage(null, "unknown language");
            }

            using var document = JsonDocument.Parse(responseText);
            var detected = document.RootElement
                .GetProperty("results")
                .GetProperty("documents")[0]
                .GetProperty("detectedLanguage");

            var code = detected.GetProperty("iso6391Name").GetString();
            var name = detected.GetProperty("name").GetString();
            var confidence = detected.TryGetProperty("confidenceScore", out var confidenceElement)
                ? confidenceElement.GetDouble()
                : 0;

            if (confidence < 0.45 || string.IsNullOrWhiteSpace(code) || code == "(Unknown)")
            {
                return new DetectedLanguage(null, "unknown language");
            }

            return new DetectedLanguage(NormalizeLanguageCode(code), name ?? DisplayName(code));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure Language detection failed");
            return new DetectedLanguage(null, "unknown language");
        }
    }

    private static DetectedLanguage DetectWithHeuristics(string transcript)
    {
        var sample = transcript.Length > 5120 ? transcript[..5120] : transcript;
        var profile = DetectScriptProfile(sample);

        if (profile.HasSignificantCyrillic)
        {
            return new DetectedLanguage("ru", "Russian");
        }

        if (profile.HasSignificantEnglish && profile.HasSignificantArmenian)
        {
            return new DetectedLanguage("mixed-en-hy", "mixed English-Armenian");
        }

        if (profile.HasSignificantArmenian)
        {
            return new DetectedLanguage("hy", "Armenian");
        }

        if (profile.HasSignificantEnglish)
        {
            return new DetectedLanguage("en", "English");
        }

        return new DetectedLanguage(null, "unknown language");
    }

    private static string NormalizeLanguageCode(string? language)
    {
        var normalized = language?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or null or "auto" => "auto",
            "eng" => "en",
            "arm" or "hye" => "hy",
            _ => normalized
        };
    }

    private static string DisplayName(string languageCode)
    {
        return languageCode switch
        {
            "en" => "English",
            "hy" => "Armenian",
            "mixed-en-hy" => "mixed English-Armenian",
            "auto" => "auto-detected language",
            "unknown" => "unknown language",
            _ => TryCultureDisplayName(languageCode)
        };
    }

    private static string TryCultureDisplayName(string languageCode)
    {
        try
        {
            return CultureInfo.GetCultureInfo(languageCode).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return languageCode;
        }
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static ScriptProfile DetectScriptProfile(string transcript)
    {
        var sample = transcript.Length > 5120 ? transcript[..5120] : transcript;
        var armenianCount = ArmenianRegex().Matches(sample).Count;
        var cyrillicCount = CyrillicRegex().Matches(sample).Count;
        var latinCount = LatinRegex().Matches(sample).Count;
        var total = armenianCount + cyrillicCount + latinCount;

        static bool IsSignificant(int count, int total) =>
            count >= 3 && total > 0 && (double)count / total >= 0.05;

        return new ScriptProfile(
            HasSignificantEnglish: IsSignificant(latinCount, total),
            HasSignificantArmenian: IsSignificant(armenianCount, total),
            HasSignificantCyrillic: IsSignificant(cyrillicCount, total));
    }

    [GeneratedRegex(@"\p{IsArmenian}")]
    private static partial Regex ArmenianRegex();

    [GeneratedRegex(@"\p{IsCyrillic}")]
    private static partial Regex CyrillicRegex();

    [GeneratedRegex(@"[A-Za-z]")]
    private static partial Regex LatinRegex();
}

internal sealed record ScriptProfile(
    bool HasSignificantEnglish,
    bool HasSignificantArmenian,
    bool HasSignificantCyrillic);

public sealed record LanguageValidationResult(
    bool IsSupported,
    string Language,
    string? DetectedLanguage,
    string? Message)
{
    public static LanguageValidationResult Supported(string language) =>
        new(true, language == "auto" ? "en" : language, language == "auto" ? null : language, null);

    public static LanguageValidationResult Unsupported(string detectedLanguage, string detectedName, string message) =>
        new(false, "auto", detectedLanguage, message);
}

internal sealed record DetectedLanguage(string? Code, string Name);
