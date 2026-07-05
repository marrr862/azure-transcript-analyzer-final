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
builder.Services.AddSingleton<RegexExtractionService>();
builder.Services.AddSingleton<RoleDetectionService>();
builder.Services.AddSingleton<AzureLanguageService>();
builder.Services.AddSingleton<TranscriptAnalysisService>();

var app = builder.Build();

app.MapGet("/openapi/v1.json", () => Results.Json(OpenApiDocumentFactory.Create()))
    .WithName("OpenApiDocument");

app.UseCors("LocalFrontend");

app.MapGet("/health", (ConfigurationService config) => Results.Ok(new
{
    status = "ok",
    azure_configured = config.AzureLanguageConfigured,
    openai_configured = config.AzureOpenAiConfigured,
    backend = ".NET"
}))
.WithName("Health");

app.MapPost("/analyze", async (
    AnalyzeRequest request,
    TranscriptAnalysisService analyzer,
    CancellationToken cancellationToken) =>
{
    var transcript = request.GetTranscript();
    if (string.IsNullOrWhiteSpace(transcript))
    {
        return Results.BadRequest(new { detail = "transcript must not be empty" });
    }

    var response = await analyzer.AnalyzeAsync(transcript, request.Language, cancellationToken);
    return Results.Ok(response);
})
.WithName("AnalyzeTranscript");

Console.WriteLine("Backend routes configured. Starting Kestrel...");

app.Run();
