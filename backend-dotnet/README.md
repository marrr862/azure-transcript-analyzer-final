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

Interactive Swagger UI is available at:

```text
http://localhost:8000/swagger
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

Azure Language and Azure OpenAI are used for PII/entity extraction. Azure OpenAI is also used for role detection when no explicit Agent/Caller labels exist and the service is configured. Regex and Speaker 1/Speaker 2 fallbacks keep the API usable without Azure services.

The backend detects the transcript language before analysis. English (`en`), Armenian (`hy`), and mixed English-Armenian transcripts are supported. Mixed English-Armenian text is translated to English before role detection and attribute extraction. Other detected languages, such as Russian, return a `400` response with a clear message instead of falling through to regex-only extraction.

## Analyze Request

```bash
curl -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"en","transcriptText":"Agent: Hello.\nCaller: My name is John Smith."}'
```

`transcriptText` may contain real newlines or escaped literal newline text such as `\\n`; the backend normalizes both before analysis.

## Long Transcripts

Long transcripts are split into configurable ordered chunks before Azure AI Language or Azure OpenAI calls. Azure AI Language and Azure OpenAI extraction chunks can include overlap to avoid losing entities at boundaries; Azure OpenAI role-detection chunks are non-overlapping so conversation turns remain ordered without duplicate overlap text.

```bash
export TRANSCRIPT_CHUNK_SIZE=4000
export TRANSCRIPT_CHUNK_OVERLAP=200
export MAX_PARALLEL_AI_CALLS=3
```

`MAX_PARALLEL_AI_CALLS` is enforced globally across simultaneous `/analyze` requests, so multiple large transcripts queue Azure/OpenAI calls instead of multiplying concurrency per request.

Chunk-level Azure AI calls run with bounded parallelism, then extraction results are merged and cleaned with a final Azure OpenAI consolidation pass. If one chunk fails, the API returns partial results with a warning instead of failing the whole request. For 20k-50k word transcripts, keep `MAX_PARALLEL_AI_CALLS` conservative (2-4) unless your Azure quota supports more concurrent calls.

## Local TXT Outputs

Each successful `POST /analyze` response is saved as a human-readable TXT file under `backend-dotnet/local-results/`. The folder is created automatically and is ignored by Git. These files are for local development/testing only; do not use real transcripts or real PII in committed files.
