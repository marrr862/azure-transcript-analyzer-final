# Azure AI Transcript Analyzer

A local web app that analyzes already-transcribed call-center text in English and Armenian. It structures the conversation by speaker role and extracts key PII/case attributes such as name, address, phone, email, and SSN or national ID.

The migrated backend is now **C# / .NET / ASP.NET Core Web API** in `backend-dotnet`. The original Python/FastAPI backend remains in `backend` only as a deprecated reference until the .NET backend is fully accepted.

## Current Stack

- Backend: ASP.NET Core Web API (`backend-dotnet`)
- Frontend: React + Vite (`frontend`)
- Azure AI Language: PII/entity extraction when configured
- Azure OpenAI: role detection when configured and explicit labels are missing
- Regex fallback: local extraction when Azure services are missing or fail

## Configuration

Do not commit real keys, `.env` files, real transcripts, or real PII.

Set configuration with environment variables, .NET user secrets, or local uncommitted settings:

```bash
export AZURE_LANGUAGE_ENDPOINT="https://<your-language-resource>.cognitiveservices.azure.com/"
export AZURE_LANGUAGE_KEY="<your-language-key>"
export AZURE_OPENAI_ENDPOINT="https://<your-openai-resource>.openai.azure.com/"
export AZURE_OPENAI_KEY="<your-openai-key>"
export AZURE_OPENAI_DEPLOYMENT="<your-deployment-name>"
```

If Azure services are not configured, the backend still runs with regex extraction and Speaker 1 / Speaker 2 fallback role labels.

## Running The .NET Backend

```bash
cd backend-dotnet
dotnet restore
dotnet run --no-launch-profile --urls http://localhost:8000
```

Available endpoints:

- `GET http://localhost:8000/health`
- `POST http://localhost:8000/analyze`
- `GET http://localhost:8000/openapi/v1.json`

## Running The Frontend

```bash
cd frontend
npm install
npm run dev
```

Open:

```text
http://localhost:5173
```

The frontend uses `VITE_API_BASE_URL` when provided, otherwise it defaults to `http://localhost:8000`.

Example:

```bash
cd frontend
VITE_API_BASE_URL=http://localhost:8000 npm run dev
```

## API Request

The .NET backend accepts both `transcript` and `transcriptText`:

```json
{
  "language": "en",
  "transcript": "Agent: Hello, how can I help?\nCaller: My name is John Smith."
}
```

```json
{
  "language": "hy",
  "transcriptText": "Գործակալ: Բարև ձեզ։\nԶանգահարող: Իմ անունը Արա Պետրոսյան է։"
}
```

## API Response Shape

```json
{
  "conversation": [
    { "role": "Agent", "text": "Hello, how can I help?" },
    { "role": "Caller", "text": "My name is John Smith." }
  ],
  "extractedAttributes": {
    "name": "John Smith",
    "address": "",
    "dateOfBirth": "",
    "socialSecurityNumber": "",
    "phoneNumber": "",
    "email": "",
    "doctorName": "",
    "conditions": [],
    "medications": [],
    "other": []
  },
  "rawAzureEntities": [],
  "warning": "Azure AI Language is not configured",
  "roleMethod": "labels"
}
```

## Manual Test Requests

English:

```bash
curl -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"en","transcript":"Agent: Hello, how can I help you?\nCaller: My name is John Smith. My phone number is 555-867-5309 and my email is john.smith@example.com. I live at 123 Main Street. My SSN is 123-45-6789."}'
```

Armenian:

```bash
curl -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"hy","transcriptText":"Գործակալ: Բարև ձեզ։\nԶանգահարող: Իմ անունը Արա Պետրոսյան է։ Իմ հեռախոսահամարն է +374 91 123456։ Ես ապրում եմ Երևան, Աբովյան փողոց 15։"}'
```

Empty transcript validation:

```bash
curl -i -X POST http://localhost:8000/analyze \
  -H "Content-Type: application/json" \
  -d '{"language":"en","transcript":""}'
```

## Deprecated Python Backend

The old Python/FastAPI backend in `backend` should stay temporarily as a behavior reference. Once the .NET backend is accepted and tested with real Azure resources using sanitized inputs, the Python backend can be moved to an archive folder or deleted in a separate cleanup step.

Do not copy secrets from `backend/.env` into tracked files.
