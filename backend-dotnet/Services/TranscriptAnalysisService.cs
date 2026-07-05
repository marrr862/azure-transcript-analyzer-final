using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class TranscriptAnalysisService(
    TranscriptChunkingService chunking,
    RoleDetectionService roleDetection,
    AzureLanguageService azureLanguage,
    RegexExtractionService regexExtraction,
    AnalysisResultFileWriter resultFileWriter)
{
    public async Task<AnalyzeResponse> AnalyzeAsync(
        string transcript,
        string? language,
        CancellationToken cancellationToken)
    {
        var chunks = chunking.Split(transcript);
        var orderedRoleChunks = chunking.Split(transcript, includeOverlap: false);
        var warnings = new List<string>();

        var (conversation, roleMethod, roleWarnings) = await roleDetection.DetectAsync(transcript, orderedRoleChunks, cancellationToken);
        warnings.AddRange(roleWarnings);

        var (azureAttributes, rawEntities, azureWarnings) = await azureLanguage.AnalyzeChunksAsync(chunks, language, cancellationToken);
        warnings.AddRange(azureWarnings);

        var regexAttributes = regexExtraction.Extract(transcript);
        var extractedAttributes = regexExtraction.Merge([.. azureAttributes, regexAttributes]);

        var response = new AnalyzeResponse
        {
            Conversation = conversation,
            ExtractedAttributes = extractedAttributes,
            RawAzureEntities = rawEntities,
            Warning = warnings.Count > 0 ? string.Join("; ", warnings.Distinct(StringComparer.OrdinalIgnoreCase)) : null,
            RoleMethod = roleMethod
        };

        await resultFileWriter.SaveAsync(
            response,
            language,
            transcript.Length,
            DateTimeOffset.UtcNow,
            cancellationToken);

        return response;
    }
}
