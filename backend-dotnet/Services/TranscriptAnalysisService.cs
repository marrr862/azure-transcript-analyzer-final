using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class TranscriptAnalysisService(
    TranscriptChunkingService chunking,
    RoleDetectionService roleDetection,
    TranscriptTranslationService translation,
    AzureLanguageService azureLanguage,
    OpenAiExtractionService openAiExtraction,
    RegexExtractionService regexExtraction,
    AnalysisResultFileWriter resultFileWriter)
{
    public async Task<AnalyzeResponse> AnalyzeAsync(
        string transcript,
        string? language,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var normalizedLanguage = NormalizeLanguage(language);
        var translationResult = await translation.TranslateToEnglishAsync(transcript, normalizedLanguage, cancellationToken);
        if (!string.IsNullOrWhiteSpace(translationResult.Warning))
        {
            warnings.Add(translationResult.Warning);
        }

        var analysisTranscript = translationResult.Transcript;
        var orderedRoleChunks = chunking.Split(analysisTranscript, includeOverlap: false);
        var attributeChunks = chunking.Split(transcript);

        var (conversation, roleMethod, roleWarnings) = await roleDetection.DetectAsync(analysisTranscript, orderedRoleChunks, cancellationToken);
        warnings.AddRange(roleWarnings);

        var (azureAttributes, rawEntities, azureWarnings) = await azureLanguage.AnalyzeChunksAsync(attributeChunks, normalizedLanguage, cancellationToken);
        warnings.AddRange(azureWarnings);

        var (openAiAttributes, openAiWarnings) = await openAiExtraction.ExtractChunksAsync(attributeChunks, cancellationToken);
        warnings.AddRange(openAiWarnings);

        var regexAttributes = regexExtraction.Extract(transcript);
        var extractedAttributes = regexExtraction.Merge([.. azureAttributes, .. openAiAttributes, regexAttributes]);
        var (consolidatedAttributes, consolidationWarning) = await openAiExtraction.ConsolidateAttributesAsync(
            extractedAttributes,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(consolidationWarning))
        {
            warnings.Add(consolidationWarning);
        }

        var response = new AnalyzeResponse
        {
            Conversation = conversation,
            ExtractedAttributes = consolidatedAttributes,
            RawAzureEntities = rawEntities,
            AttributeEvidence = BuildAttributeEvidence(consolidatedAttributes, rawEntities, transcript),
            Warning = warnings.Count > 0 ? string.Join("; ", warnings.Distinct(StringComparer.OrdinalIgnoreCase)) : null,
            RoleMethod = roleMethod,
            DetectedLanguage = normalizedLanguage,
            TranslationMethod = translationResult.Method,
            TranslatedTranscript = translationResult.Method == "openai" ? analysisTranscript : null
        };

        await resultFileWriter.SaveAsync(
            response,
            language,
            transcript.Length,
            DateTimeOffset.UtcNow,
            cancellationToken);

        return response;
    }

    private static string NormalizeLanguage(string? language) =>
        language?.Trim().ToLowerInvariant() switch
        {
            "en" => "en",
            "hy" => "hy",
            "mixed-en-hy" => "mixed-en-hy",
            _ => "auto"
        };

    private static IReadOnlyList<AttributeEvidence> BuildAttributeEvidence(
        ExtractedAttributes attributes,
        IReadOnlyList<RawAzureEntity> rawEntities,
        string transcript)
    {
        var evidence = new List<AttributeEvidence>();

        AddScalar(evidence, "name", "Person Name", attributes.Name, rawEntities, transcript, ["Person"]);
        AddScalar(evidence, "phoneNumber", "Phone Number", attributes.PhoneNumber, rawEntities, transcript, ["PhoneNumber"]);
        AddScalar(evidence, "email", "Email", attributes.Email, rawEntities, transcript, ["Email"]);
        AddScalar(evidence, "address", "Address", attributes.Address, rawEntities, transcript, ["Address"]);
        AddScalar(evidence, "doctorName", "Doctor", attributes.DoctorName, rawEntities, transcript, ["Person"]);
        AddScalar(evidence, "dateOfBirth", "Date of Birth", attributes.DateOfBirth, rawEntities, transcript, ["DateTime"]);
        AddScalar(evidence, "socialSecurityNumber", "SSN / National ID", attributes.SocialSecurityNumber, rawEntities, transcript, ["USSocialSecurityNumber", "EUPassportNumber"]);

        AddList(evidence, "importantDetails", "Important", attributes.ImportantDetails, transcript);
        AddList(evidence, "conditions", "Condition", attributes.Conditions, transcript);
        AddList(evidence, "medications", "Medication", attributes.Medications, transcript);
        AddList(evidence, "other", "Other", attributes.Other, transcript);

        return evidence;
    }

    private static void AddScalar(
        List<AttributeEvidence> evidence,
        string field,
        string label,
        string value,
        IReadOnlyList<RawAzureEntity> rawEntities,
        string transcript,
        IReadOnlyCollection<string> azureCategories)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var rawEntity = FindBestRawEntity(value, rawEntities, azureCategories);
        evidence.Add(new AttributeEvidence
        {
            Field = field,
            Label = label,
            Value = value.Trim(),
            Confidence = rawEntity?.Confidence ?? EstimateFallbackConfidence(field),
            Source = rawEntity is null ? EstimateFallbackSource(field) : "Azure AI Language",
            Snippet = FindSnippet(transcript, rawEntity?.Text ?? value)
        });
    }

    private static void AddList(
        List<AttributeEvidence> evidence,
        string field,
        string label,
        IReadOnlyList<string> values,
        string transcript)
    {
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Take(12))
        {
            evidence.Add(new AttributeEvidence
            {
                Field = field,
                Label = label,
                Value = value.Trim(),
                Confidence = 0.84,
                Source = "Azure OpenAI",
                Snippet = FindSnippet(transcript, value)
            });
        }
    }

    private static RawAzureEntity? FindBestRawEntity(
        string value,
        IReadOnlyList<RawAzureEntity> rawEntities,
        IReadOnlyCollection<string> categories)
    {
        var normalizedValue = NormalizeForMatch(value);
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return rawEntities
            .Where(entity => categories.Contains(entity.Category))
            .Where(entity =>
            {
                var normalizedEntity = NormalizeForMatch(entity.Text);
                return normalizedEntity.Contains(normalizedValue, StringComparison.OrdinalIgnoreCase)
                    || normalizedValue.Contains(normalizedEntity, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(entity => entity.Confidence)
            .FirstOrDefault();
    }

    private static double EstimateFallbackConfidence(string field)
    {
        return field is "email" or "phoneNumber" or "socialSecurityNumber"
            ? 0.92
            : 0.84;
    }

    private static string EstimateFallbackSource(string field)
    {
        return field is "email" or "phoneNumber" or "socialSecurityNumber"
            ? "Local pattern"
            : "Azure OpenAI";
    }

    private static string FindSnippet(string transcript, string value)
    {
        if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var searchValue = StripOtherLabel(value.Trim());
        var index = transcript.IndexOf(searchValue, StringComparison.OrdinalIgnoreCase);

        if (index < 0 && searchValue.Length > 28)
        {
            var shorterValue = searchValue[..28].Trim();
            index = transcript.IndexOf(shorterValue, StringComparison.OrdinalIgnoreCase);
        }

        if (index < 0)
        {
            return string.Empty;
        }

        var start = Math.Max(0, index - 90);
        var end = Math.Min(transcript.Length, index + searchValue.Length + 90);
        var prefix = start > 0 ? "..." : "";
        var suffix = end < transcript.Length ? "..." : "";

        return prefix + NormalizeWhitespace(transcript[start..end]) + suffix;
    }

    private static string StripOtherLabel(string value)
    {
        var separatorIndex = value.IndexOf(": ", StringComparison.Ordinal);
        return separatorIndex > 0 && separatorIndex < 30
            ? value[(separatorIndex + 2)..]
            : value;
    }

    private static string NormalizeForMatch(string value) =>
        NormalizeWhitespace(StripOtherLabel(value))
            .Trim()
            .Trim('.', ',', ';', ':', '։');

    private static string NormalizeWhitespace(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
