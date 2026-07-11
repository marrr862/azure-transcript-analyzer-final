using System.Security.Cryptography;
using System.Text;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class AnalysisResultFileWriter(IWebHostEnvironment environment)
{
    private const string ResultsFolderName = "local-results";

    public async Task SaveAsync(
        AnalyzeResponse response,
        string? language,
        int transcriptLength,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(environment.ContentRootPath, ResultsFolderName);
        Directory.CreateDirectory(outputDirectory);

        var timestamp = createdAtUtc.ToString("yyyy-MM-dd'T'HHmmss'Z'");
        var uniqueId = RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
        var fileName = $"{timestamp}-{uniqueId}-analysis.txt";
        var filePath = Path.Combine(outputDirectory, fileName);

        await File.WriteAllTextAsync(
            filePath,
            BuildText(response, language, transcriptLength, createdAtUtc),
            Encoding.UTF8,
            cancellationToken);
    }

    private static string BuildText(
        AnalyzeResponse response,
        string? language,
        int transcriptLength,
        DateTimeOffset createdAtUtc)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Azure AI Transcript Analyzer - Analysis Result");
        builder.AppendLine("============================================");
        builder.AppendLine($"Created At UTC: {createdAtUtc:yyyy-MM-dd HH:mm:ss}Z");
        builder.AppendLine($"Language: {Normalize(language)}");
        builder.AppendLine($"Detected Language: {Normalize(response.DetectedLanguage)}");
        builder.AppendLine($"Translation Method: {Normalize(response.TranslationMethod)}");
        builder.AppendLine($"Transcript Length: {transcriptLength}");
        builder.AppendLine($"Role Method: {response.RoleMethod}");
        builder.AppendLine();

        builder.AppendLine("Conversation");
        builder.AppendLine("------------");
        if (response.Conversation.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            for (var i = 0; i < response.Conversation.Count; i++)
            {
                var turn = response.Conversation[i];
                builder.AppendLine($"{i + 1}. {turn.Role}: {turn.Text}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Extracted Attributes");
        builder.AppendLine("--------------------");
        AppendAttribute(builder, "Name", response.ExtractedAttributes.Name);
        AppendAttribute(builder, "Address", response.ExtractedAttributes.Address);
        AppendAttribute(builder, "Date Of Birth", response.ExtractedAttributes.DateOfBirth);
        AppendAttribute(builder, "Social Security Number", response.ExtractedAttributes.SocialSecurityNumber);
        AppendAttribute(builder, "Phone Number", response.ExtractedAttributes.PhoneNumber);
        AppendAttribute(builder, "Email", response.ExtractedAttributes.Email);
        AppendAttribute(builder, "Doctor Name", response.ExtractedAttributes.DoctorName);
        AppendList(builder, "Conditions", response.ExtractedAttributes.Conditions);
        AppendList(builder, "Medications", response.ExtractedAttributes.Medications);
        AppendList(builder, "Other", response.ExtractedAttributes.Other);
        AppendList(builder, "Important Details", response.ExtractedAttributes.ImportantDetails);

        builder.AppendLine();
        builder.AppendLine("Attribute Evidence");
        builder.AppendLine("------------------");
        if (response.AttributeEvidence.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var evidence in response.AttributeEvidence)
            {
                builder.AppendLine($"- {evidence.Label}: {evidence.Value}");
                builder.AppendLine($"  Confidence: {evidence.Confidence:P0}");
                builder.AppendLine($"  Source: {Normalize(evidence.Source)}");
                builder.AppendLine($"  Snippet: {Normalize(evidence.Snippet)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Raw Azure Entities");
        builder.AppendLine("------------------");
        if (response.RawAzureEntities.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var entity in response.RawAzureEntities)
            {
                var subcategory = string.IsNullOrWhiteSpace(entity.Subcategory)
                    ? ""
                    : $" / {entity.Subcategory}";
                builder.AppendLine($"- {entity.Text} [{entity.Category}{subcategory}] confidence={entity.Confidence:0.###}");
            }
        }

        if (!string.IsNullOrWhiteSpace(response.Warning))
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            builder.AppendLine("--------");
            builder.AppendLine(response.Warning);
        }

        if (!string.IsNullOrWhiteSpace(response.TranslatedTranscript))
        {
            builder.AppendLine();
            builder.AppendLine("Translated Transcript");
            builder.AppendLine("---------------------");
            builder.AppendLine(response.TranslatedTranscript);
        }

        return builder.ToString();
    }

    private static void AppendAttribute(StringBuilder builder, string label, string value)
    {
        builder.AppendLine($"{label}: {Normalize(value)}");
    }

    private static void AppendList(StringBuilder builder, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            builder.AppendLine($"{label}: (none)");
            return;
        }

        builder.AppendLine($"{label}:");
        foreach (var value in values)
        {
            builder.AppendLine($"- {value}");
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(not detected)" : value.Trim();
}
