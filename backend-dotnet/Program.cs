using System.Text.Json.Serialization;
using TranscriptAnalyzer.Models;
using TranscriptAnalyzer.Services;

var builder = WebApplication.CreateBuilder(args);

Console.WriteLine("Starting Azure AI Transcript Analyzer .NET backend...");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalFrontend", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5173",
                "http://127.0.0.1:5173",
                "http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<AiConcurrencyLimiter>();
builder.Services.AddSingleton<LanguageDetectionService>();
builder.Services.AddSingleton<TranscriptChunkingService>();
builder.Services.AddSingleton<TranscriptTranslationService>();
builder.Services.AddSingleton<RegexExtractionService>();
builder.Services.AddSingleton<RoleDetectionService>();
builder.Services.AddSingleton<AzureLanguageService>();
builder.Services.AddSingleton<OpenAiExtractionService>();
builder.Services.AddSingleton<AnalysisResultFileWriter>();
builder.Services.AddSingleton<AnalysisHistoryService>();
builder.Services.AddSingleton<TranscriptAnalysisService>();

var app = builder.Build();

var swaggerUiHtml = """
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Azure AI Transcript Analyzer API</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui.css">
    <style>
        body { margin: 0; background: #fafafa; }
        .swagger-ui .topbar { display: none; }
    </style>
</head>
<body>
    <div id="swagger-ui"></div>
    <script src="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
    <script>
        window.onload = () => {
            window.ui = SwaggerUIBundle({
                url: "/openapi/v1.json",
                dom_id: "#swagger-ui",
                presets: [SwaggerUIBundle.presets.apis],
                layout: "BaseLayout"
            });
        };
    </script>
</body>
</html>
""";

app.MapGet("/swagger", () => Results.Content(swaggerUiHtml, "text/html"))
    .WithName("SwaggerUi");

app.MapGet("/openapi/v1.json", () => Results.Json(OpenApiDocumentFactory.Create()))
    .WithName("OpenApiDocument");

app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/swagger/", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/swagger", permanent: false);
        return;
    }

    await next();
});

app.UseCors("LocalFrontend");

app.MapGet("/health", (ConfigurationService config) => Results.Ok(new
{
    status = "ok",
    azure_configured = config.AzureLanguageConfigured,
    openai_configured = config.AzureOpenAiConfigured,
    backend = ".NET"
}))
.WithName("Health");

app.MapGet("/history", async (
    AnalysisHistoryService history,
    CancellationToken cancellationToken) =>
{
    var items = await history.ListAsync(cancellationToken);
    return Results.Ok(items);
})
.WithName("ListAnalysisHistory");

app.MapGet("/history/{id}", async (
    string id,
    AnalysisHistoryService history,
    CancellationToken cancellationToken) =>
{
    var item = await history.GetAsync(id, cancellationToken);
    return item is null ? Results.NotFound(new { detail = "history item not found" }) : Results.Ok(item);
})
.WithName("GetAnalysisHistoryDetail");

app.MapDelete("/history/{id}", async (
    string id,
    AnalysisHistoryService history,
    CancellationToken cancellationToken) =>
{
    var deleted = await history.DeleteAsync(id, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound(new { detail = "history item not found" });
})
.WithName("DeleteAnalysisHistoryItem");

app.MapPost("/analyze", async (
    AnalyzeRequest request,
    LanguageDetectionService languageDetection,
    TranscriptAnalysisService analyzer,
    CancellationToken cancellationToken) =>
{
    var transcriptText = !string.IsNullOrWhiteSpace(request.TranscriptText)
        ? request.TranscriptText
        : request.Transcript;

    if (string.IsNullOrWhiteSpace(transcriptText))
    {
        return Results.BadRequest(new { detail = "transcript or transcriptText must not be empty" });
    }

    var normalizedTranscriptText = NormalizeTranscriptNewlines(transcriptText);
    var languageResult = await languageDetection.ValidateAsync(
        normalizedTranscriptText,
        request.Language,
        cancellationToken);

    if (!languageResult.IsSupported)
    {
        return Results.BadRequest(new
        {
            detail = languageResult.Message,
            detectedLanguage = languageResult.DetectedLanguage
        });
    }

    var response = await analyzer.AnalyzeAsync(normalizedTranscriptText, languageResult.Language, cancellationToken);
    return Results.Ok(response);
})
.WithName("AnalyzeTranscript");

Console.WriteLine("Backend routes configured. Starting Kestrel...");

app.Run();

static string NormalizeTranscriptNewlines(string transcriptText)
{
    return transcriptText
        .Replace("\\r\\n", "\n", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal);
}
