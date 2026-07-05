# Project Agent Instructions

This project is finalized around a React/Vite frontend and a C#/.NET ASP.NET Core Web API backend.

## Backend

- Use `backend-dotnet` for all backend work.
- Do not recreate or depend on the old Python/FastAPI backend.
- Keep the public API compatible with the React frontend.
- Preserve support for both request fields: `transcript` and `transcriptText`.
- Keep `/health`, `/analyze`, and `/openapi/v1.json` available.
- Keep local TXT output storage under `backend-dotnet/local-results/`.
- Do not add a database, Entity Framework, or database packages.

## Frontend

- Keep the React/Vite frontend in `frontend`.
- Use `VITE_API_BASE_URL` for the backend URL.
- Do not hardcode deployment URLs or secrets.

## Security

- Never commit `.env` files, API keys, credentials, real transcripts, or real PII.
- Use only sanitized examples in docs, tests, and prompts.
- Keep generated folders such as `node_modules`, `dist`, `bin`, `obj`, and `local-results` out of Git.

## Verification

- Build the .NET backend with `dotnet build backend-dotnet/backend-dotnet.csproj`.
- Run the backend with `dotnet run --project backend-dotnet/backend-dotnet.csproj --no-launch-profile --urls http://localhost:8000`.
- Run the frontend with `VITE_API_BASE_URL=http://localhost:8000 npm run dev` from `frontend`.
