using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class ExtractedAttributes
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("dateOfBirth")]
    public string DateOfBirth { get; set; } = string.Empty;

    [JsonPropertyName("socialSecurityNumber")]
    public string SocialSecurityNumber { get; set; } = string.Empty;

    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("doctorName")]
    public string DoctorName { get; set; } = string.Empty;

    [JsonPropertyName("conditions")]
    public List<string> Conditions { get; set; } = [];

    [JsonPropertyName("medications")]
    public List<string> Medications { get; set; } = [];

    [JsonPropertyName("other")]
    public List<string> Other { get; set; } = [];

    [JsonPropertyName("importantDetails")]
    public List<string> ImportantDetails { get; set; } = [];
}
