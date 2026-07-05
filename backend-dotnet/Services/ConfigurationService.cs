namespace TranscriptAnalyzer.Services;

public sealed class ConfigurationService(IConfiguration configuration)
{
    public string AzureLanguageEndpoint =>
        Read("AzureLanguage:Endpoint", "AZURE_LANGUAGE_ENDPOINT");

    public string AzureLanguageKey =>
        Read("AzureLanguage:Key", "AZURE_LANGUAGE_KEY");

    public string AzureOpenAiEndpoint =>
        Read("AzureOpenAI:Endpoint", "AZURE_OPENAI_ENDPOINT");

    public string AzureOpenAiKey =>
        Read("AzureOpenAI:Key", "AZURE_OPENAI_KEY");

    public string AzureOpenAiDeployment =>
        Read("AzureOpenAI:Deployment", "AZURE_OPENAI_DEPLOYMENT");

    public string AzureOpenAiApiVersion =>
        Read("AzureOpenAI:ApiVersion", "AZURE_OPENAI_API_VERSION", "2024-10-21");

    public bool AzureLanguageConfigured =>
        LooksConfigured(AzureLanguageEndpoint) && LooksConfigured(AzureLanguageKey);

    public bool AzureOpenAiConfigured =>
        LooksConfigured(AzureOpenAiEndpoint)
        && LooksConfigured(AzureOpenAiKey)
        && LooksConfigured(AzureOpenAiDeployment);

    private string Read(string configKey, string environmentKey, string fallback = "")
    {
        return configuration[environmentKey]
            ?? configuration[configKey]
            ?? fallback;
    }

    private static bool LooksConfigured(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && !value.Contains('<');
    }
}
