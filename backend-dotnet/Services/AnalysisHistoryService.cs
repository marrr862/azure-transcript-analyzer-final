using System.Globalization;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class AnalysisHistoryService(IWebHostEnvironment environment)
{
    private const string ResultsFolderName = "local-results";

    public async Task<IReadOnlyList<AnalysisHistoryItem>> ListAsync(CancellationToken cancellationToken)
    {
        var directory = GetOutputDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = Directory
            .EnumerateFiles(directory, "*-analysis.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        var items = new List<AnalysisHistoryItem>(files.Length);
        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            items.Add(ToItem(file, content));
        }

        return items;
    }

    public async Task<AnalysisHistoryDetail?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsSafeId(id))
        {
            return null;
        }

        var filePath = Path.Combine(GetOutputDirectory(), $"{id}-analysis.txt");
        if (!File.Exists(filePath))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var item = ToItem(filePath, content);

        return new AnalysisHistoryDetail
        {
            Id = item.Id,
            FileName = item.FileName,
            CreatedAtUtc = item.CreatedAtUtc,
            Language = item.Language,
            DetectedLanguage = item.DetectedLanguage,
            TranslationMethod = item.TranslationMethod,
            RoleMethod = item.RoleMethod,
            TranscriptLength = item.TranscriptLength,
            Content = content
        };
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsSafeId(id))
        {
            return Task.FromResult(false);
        }

        var filePath = Path.Combine(GetOutputDirectory(), $"{id}-analysis.txt");
        if (!File.Exists(filePath))
        {
            return Task.FromResult(false);
        }

        File.Delete(filePath);
        return Task.FromResult(true);
    }

    private string GetOutputDirectory() => Path.Combine(environment.ContentRootPath, ResultsFolderName);

    private static AnalysisHistoryItem ToItem(string filePath, string content)
    {
        var fileName = Path.GetFileName(filePath);
        var id = fileName.EndsWith("-analysis.txt", StringComparison.Ordinal)
            ? fileName[..^"-analysis.txt".Length]
            : Path.GetFileNameWithoutExtension(fileName);

        return new AnalysisHistoryItem
        {
            Id = id,
            FileName = fileName,
            CreatedAtUtc = ParseCreatedAt(content, fileName, File.GetLastWriteTimeUtc(filePath)),
            Language = ReadMetadata(content, "Language") ?? "auto",
            DetectedLanguage = ReadMetadata(content, "Detected Language") ?? ReadMetadata(content, "Language") ?? "auto",
            TranslationMethod = ReadMetadata(content, "Translation Method") ?? "none",
            RoleMethod = ReadMetadata(content, "Role Method") ?? "fallback",
            TranscriptLength = int.TryParse(ReadMetadata(content, "Transcript Length"), out var length) ? length : 0
        };
    }

    private static DateTimeOffset ParseCreatedAt(string content, string fileName, DateTime fallbackUtc)
    {
        var value = ReadMetadata(content, "Created At UTC");
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        var timestamp = fileName.Split('-', 4).Length >= 3
            ? string.Join("-", fileName.Split('-', 4).Take(3))
            : string.Empty;

        return DateTimeOffset.TryParseExact(
            timestamp,
            "yyyy-MM-dd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed)
            ? parsed
            : new DateTimeOffset(fallbackUtc, TimeSpan.Zero);
    }

    private static string? ReadMetadata(string content, string key)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith($"{key}:", StringComparison.OrdinalIgnoreCase))
            {
                return line[(key.Length + 1)..].Trim();
            }
        }

        return null;
    }

    private static bool IsSafeId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.All(static c => char.IsLetterOrDigit(c) || c is '-' or '_');
}
