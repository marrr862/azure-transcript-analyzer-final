namespace TranscriptAnalyzer.Services;

public sealed class AiConcurrencyLimiter(ConfigurationService configuration, ILogger<AiConcurrencyLimiter> logger)
{
    private readonly SemaphoreSlim _semaphore = new(configuration.MaxParallelAiCalls, configuration.MaxParallelAiCalls);

    public async Task<T> RunAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        var waited = false;
        if (_semaphore.CurrentCount == 0)
        {
            waited = true;
            logger.LogInformation("AI concurrency limit reached; queueing {Operation}", operation);
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (waited)
            {
                logger.LogInformation("AI concurrency slot acquired for {Operation}", operation);
            }

            return await work(cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
