# Azure AI Transcript Analyzer .NET Backend

ASP.NET Core Web API backend for transcript role detection and PII/entity extraction.

## Run

```bash
cd backend-dotnet
dotnet restore
dotnet run --no-launch-profile --urls http://localhost:8000
```

OpenAPI is available in development at:

```text
http://localhost:8000/openapi/v1.json
```

## Configuration

Use environment variables, .NET user secrets, or local appsettings overrides. Do not commit real keys.

```bash
export AZURE_LANGUAGE_ENDPOINT="https://<your-language-resource>.cognitiveservices.azure.com/"
export AZURE_LANGUAGE_KEY="<your-language-key>"
export AZURE_OPENAI_ENDPOINT="https://<your-openai-resource>.openai.azure.com/"
export AZURE_OPENAI_KEY="<your-openai-key>"
export AZURE_OPENAI_DEPLOYMENT="<your-deployment-name>"
```

Azure Language is used for PII/entity extraction. Azure OpenAI is used for role detection only when no explicit Agent/Caller labels exist and the service is configured. Regex and Speaker 1/Speaker 2 fallbacks keep the API usable without Azure services.
