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

    public int TranscriptChunkSize =>
        ReadInt("TranscriptChunking:ChunkSize", "TRANSCRIPT_CHUNK_SIZE", 4000);

    public int TranscriptChunkOverlap =>
        ReadInt("TranscriptChunking:Overlap", "TRANSCRIPT_CHUNK_OVERLAP", 200);

    public int MaxParallelAiCalls =>
        Math.Clamp(ReadInt("Processing:MaxParallelAiCalls", "MAX_PARALLEL_AI_CALLS", 3), 1, 8);

    public bool AzureLanguageConfigured =>
        LooksConfigured(AzureLanguageEndpoint) && LooksConfigured(AzureLanguageKey);

    public bool AzureOpenAiConfigured =>
        LooksConfigured(AzureOpenAiEndpoint)
        && LooksConfigured(AzureOpenAiDeployment);

    private string Read(string configKey, string environmentKey, string fallback = "")
    {
        return configuration[environmentKey]
            ?? configuration[configKey]
            ?? fallback;
    }

    private int ReadInt(string configKey, string environmentKey, int fallback)
    {
        var raw = Read(configKey, environmentKey);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    private static bool LooksConfigured(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && !value.Contains('<');
    }
}
