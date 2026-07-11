namespace TranscriptAnalyzer.Services;

public static class OpenApiDocumentFactory
{
    public static object Create() => new
    {
        openapi = "3.0.3",
        info = new
        {
            title = "Azure AI Transcript Analyzer .NET API",
            version = "1.0.0"
        },
        paths = new Dictionary<string, object>
        {
            ["/health"] = new
            {
                get = new
                {
                    summary = "Backend health and Azure configuration status",
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new
                        {
                            description = "Health response"
                        }
                    }
                }
            },
            ["/analyze"] = new
            {
                post = new
                {
                    summary = "Analyze a call-center transcript",
                    requestBody = new
                    {
                        required = true,
                        content = new Dictionary<string, object>
                        {
                            ["application/json"] = new
                            {
                                schema = new
                                {
                                    type = "object",
                                    required = new[] { "transcriptText" },
                                    properties = new Dictionary<string, object>
                                    {
                                        ["language"] = new { type = "string", example = "auto", description = "auto, en, or hy. Mixed English-Armenian is auto-detected and translated to English. Other detected languages are rejected." },
                                        ["transcriptText"] = new { type = "string", example = "Agent: Hello.\\nCaller: My name is John Smith." }
                                    }
                                }
                            }
                        }
                    },
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new
                        {
                            description = "Transcript analysis response"
                        },
                        ["400"] = new
                        {
                            description = "Invalid request"
                        }
                    }
                }
            },
            ["/history"] = new
            {
                get = new
                {
                    summary = "List saved local analyses",
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new { description = "Saved analysis list" }
                    }
                }
            },
            ["/history/{id}"] = new
            {
                get = new
                {
                    summary = "Get saved local analysis detail",
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new { description = "Saved analysis detail" },
                        ["404"] = new { description = "Saved analysis not found" }
                    }
                },
                delete = new
                {
                    summary = "Delete saved local analysis",
                    responses = new Dictionary<string, object>
                    {
                        ["204"] = new { description = "Saved analysis deleted" },
                        ["404"] = new { description = "Saved analysis not found" }
                    }
                }
            }
        }
    };
}
