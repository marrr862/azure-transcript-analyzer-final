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
                                        ["language"] = new { type = "string", example = "en" },
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
            }
        }
    };
}
