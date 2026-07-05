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

## Long Transcripts

Long transcripts are split into configurable ordered chunks before Azure AI Language or Azure OpenAI calls. Azure AI Language chunks can include overlap to avoid losing entities at boundaries; Azure OpenAI role-detection chunks are non-overlapping so conversation turns remain ordered without duplicate overlap text.

```bash
export TRANSCRIPT_CHUNK_SIZE=4000
export TRANSCRIPT_CHUNK_OVERLAP=200
```

Chunk-level extraction results are merged into one final `extractedAttributes` object. If one chunk fails, the API returns partial results with a warning instead of failing the whole request.

## Local TXT Outputs

Each successful `POST /analyze` response is saved as a human-readable TXT file under `backend-dotnet/local-results/`. The folder is created automatically and is ignored by Git. These files are for local development/testing only; do not use real transcripts or real PII in committed files.
