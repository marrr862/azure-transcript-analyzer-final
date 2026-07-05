# Requirements Summary: Azure AI Transcript Analyzer

## Source

Based on the project requirements document: "Azure AI Endpoint for Transcript Analysis and Attribute Extraction".

## Goal

Build a backend API that analyzes already transcribed call-center text in English and Armenian, extracts structured personal/business attributes, and structures the conversation by speaker roles.

## Required technology stack

- Programming language: C#
- Framework: .NET
- Web technology: ASP.NET Core Web API
- IDE compatibility: Visual Studio 2022 or higher

## Current project state

The current project was generated with Claude Code.

Current stack:
- Frontend: React/Vite/JavaScript
- Backend: Python/FastAPI

Migration goal:
- Keep the frontend if possible.
- Create a new C# backend in `backend-dotnet`.
- Do not delete the current Python backend until the C# backend is tested.
- Keep API contract compatible with the current frontend where possible.

## Required backend behavior

The backend should provide an API endpoint for submitting already transcribed text.

The backend should:
1. Accept transcript text.
2. Accept language, for example `en` or `hy`. Language may later become optional if automatic detection is implemented.
3. Send transcript text to the selected Azure AI service.
4. Process Azure AI response.
5. Extract structured attributes.
6. Structure conversation into roles.
7. Return a structured JSON response.

## Required extracted attributes

The system should extract, when mentioned:

- Person name
- Address
- Social Security Number
- Phone number
- Email address
- Other important personal or case-related information

## Conversation role requirements

The system should structure conversation turns using roles such as:

- Agent
- Caller

If Agent/Caller cannot be confidently detected, return fallback labels:

- Speaker 1
- Speaker 2

## Example request

```json
{
  "transcriptText": "Hello, how can I help you? My name is John Smith. I live at 123 Main Street. Can you please confirm your Social Security Number? Yes, it is 123-45-6789.",
  "language": "en"
}
```

## Example response

```json
{
  "conversation": [
    {
      "role": "Agent",
      "text": "Hello, how can I help you?"
    },
    {
      "role": "Caller",
      "text": "My name is John Smith. I live at 123 Main Street."
    },
    {
      "role": "Agent",
      "text": "Can you please confirm your Social Security Number?"
    },
    {
      "role": "Caller",
      "text": "Yes, it is 123-45-6789."
    }
  ],
  "extractedAttributes": {
    "name": "John Smith",
    "address": "123 Main Street",
    "socialSecurityNumber": "123-45-6789",
    "phoneNumber": null,
    "email": null,
    "other": []
  }
}
```

## Acceptance criteria

- API endpoint is available for submitting transcript text.
- Backend sends transcript text to selected Azure AI service.
- Backend receives and processes Azure AI response.
- System returns structured conversation data.
- System extracts key attributes when mentioned.
- System supports Armenian and English transcript text.
- System handles cases where Agent/Caller roles are not provided.
- System returns fallback speaker labels when roles cannot be confidently identified.
- Validation is added for required fields.
- Error handling is implemented for Azure AI failures and invalid responses.
- Integration testing is completed with sample Armenian and English transcripts.
- API documentation is prepared.

## Deliverables

- Working C# ASP.NET Core Web API backend integration.
- API endpoint for transcript analysis.
- Structured response with extracted business information.
- Conversation role separation logic.
- Validation and error handling.
- Integration and functional test results.
- API documentation.

## Important security rules

- Do not commit `.env`, API keys, credentials, real transcripts, or real PII.
- Use only sanitized sample transcripts.
- Keep secrets in local environment variables, local `.env`, or .NET user secrets.
- Keep `backend/.env`, `frontend/node_modules`, `backend/venv`, and generated files out of Git.
- Do not paste real API keys or real patient/customer data into Codex prompts.