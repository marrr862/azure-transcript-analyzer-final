using System.Text.RegularExpressions;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed partial class RegexExtractionService
{
    public ExtractedAttributes Extract(string text)
    {
        var attrs = new ExtractedAttributes();

        attrs.Email = FirstValue(EmailRegex().Matches(text));
        attrs.PhoneNumber = CleanPhone(FirstValue(PhoneRegex().Matches(text)));
        attrs.SocialSecurityNumber = CleanSsn(FirstValue(SsnRegex().Matches(text)));
        if (string.IsNullOrWhiteSpace(attrs.SocialSecurityNumber))
        {
            attrs.SocialSecurityNumber = FirstValue(ArmenianIdRegex().Matches(text));
        }

        var nameMatch = NameRegex().Match(text);
        if (nameMatch.Success)
        {
            attrs.Name = TrimArmenianVerb(nameMatch.Groups[1].Value.Trim());
        }

        var addressMatch = AddressRegex().Match(text);
        if (addressMatch.Success)
        {
            attrs.Address = addressMatch.Groups[1].Value.Trim().TrimEnd('.', '։');
        }

        return attrs;
    }

    public ExtractedAttributes Merge(params ExtractedAttributes?[] sources)
    {
        var merged = new ExtractedAttributes();
        var validSources = sources.Where(source => source is not null).Cast<ExtractedAttributes>();

        foreach (var source in validSources)
        {
            merged.Name = FirstNonEmpty(merged.Name, source.Name);
            merged.Address = FirstNonEmpty(merged.Address, source.Address);
            merged.SocialSecurityNumber = FirstNonEmpty(merged.SocialSecurityNumber, source.SocialSecurityNumber);
            merged.PhoneNumber = FirstNonEmpty(merged.PhoneNumber, source.PhoneNumber);
            merged.Email = FirstNonEmpty(merged.Email, source.Email);
            merged.DateOfBirth = FirstNonEmpty(merged.DateOfBirth, source.DateOfBirth);
            merged.DoctorName = FirstNonEmpty(merged.DoctorName, source.DoctorName);

            AddUnique(merged.Conditions, source.Conditions);
            AddUnique(merged.Medications, source.Medications);
            AddUnique(merged.ImportantDetails, source.ImportantDetails);
            foreach (var item in source.Other)
            {
                AddUnique(merged.Other, [item]);
            }
        }

        merged.SocialSecurityNumber = CleanSsn(merged.SocialSecurityNumber);
        merged.PhoneNumber = CleanPhone(merged.PhoneNumber);
        merged.Email = CleanEmail(merged.Email);
        return merged;
    }

    private static string FirstValue(MatchCollection matches) =>
        matches.Count > 0 ? matches[0].Value.Trim() : string.Empty;

    private static string FirstNonEmpty(string current, string candidate) =>
        string.IsNullOrWhiteSpace(current) ? candidate.Trim() : current;

    private static void AddUnique(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            var trimmed = value.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed)
                && !target.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(trimmed);
            }
        }
    }

    private static string TrimArmenianVerb(string value) =>
        value.EndsWith(" է", StringComparison.Ordinal) ? value[..^2].Trim() : value;

    private static string CleanSsn(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (ArmenianIdRegex().IsMatch(trimmed))
        {
            return ArmenianIdRegex().Match(trimmed).Value;
        }

        var digits = NonDigitRegex().Replace(trimmed, "");
        return digits.Length switch
        {
            9 => $"{digits[..3]}-{digits[3..5]}-{digits[5..]}",
            4 => $"***-**-{digits}",
            _ => string.Empty
        };
    }

    private static string CleanPhone(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var digits = NonDigitRegex().Replace(value, "");
        if (value.TrimStart().StartsWith("+374", StringComparison.Ordinal) && digits.Length == 11)
        {
            return $"+374 {digits[3..5]} {digits[5..]}";
        }

        if (digits.Length == 11 && digits.StartsWith('1'))
        {
            digits = digits[1..];
        }

        return digits.Length == 10
            ? $"{digits[..3]}-{digits[3..6]}-{digits[6..]}"
            : string.Empty;
    }

    private static string CleanEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var match = EmailRegex().Match(value);
        return match.Success ? match.Value : string.Empty;
    }

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\+?1[\s\-.]?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}|\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}|\+374[\s\-.]?\d{2}[\s\-.]?\d{6}|0\d{2}[\s\-.]?\d{6}")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b\d{3}[\s\-]\d{2}[\s\-]\d{4}\b")]
    private static partial Regex SsnRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{7}\b")]
    private static partial Regex ArmenianIdRegex();

    [GeneratedRegex(@"(?:my name is|անունը)\s+([A-ZԱ-Ֆա-ֆ\w][a-zA-Zա-ֆԱ-Ֆ\w]+(?:\s+[A-ZԱ-Ֆա-ֆ\w][a-zA-Zա-ֆԱ-Ֆ\w]+){0,3})", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"(?:i live at|my address is|located at|address[:\s]+|ես ապրում եմ)\s*(.+?)(?:\.|։|,\s*[A-Z]{2}\s*\d{5}|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigitRegex();
}
