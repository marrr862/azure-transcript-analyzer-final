using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class TranscriptAnalysisService(
    RoleDetectionService roleDetection,
    AzureLanguageService azureLanguage,
    RegexExtractionService regexExtraction)
{
    public async Task<AnalyzeResponse> AnalyzeAsync(
        string transcript,
        string? language,
        CancellationToken cancellationToken)
    {
        var (conversation, roleMethod) = await roleDetection.DetectAsync(transcript, cancellationToken);
        var (azureAttributes, rawEntities, warning) = await azureLanguage.AnalyzeAsync(transcript, language, cancellationToken);
        var regexAttributes = regexExtraction.Extract(transcript);
        var extractedAttributes = regexExtraction.Merge(azureAttributes, regexAttributes);

        return new AnalyzeResponse
        {
            Conversation = conversation,
            ExtractedAttributes = extractedAttributes,
            RawAzureEntities = rawEntities,
            Warning = warning,
            RoleMethod = roleMethod
        };
    }
}
