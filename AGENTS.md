# Project Agent Instructions

This project has migrated from a Python/FastAPI backend to a C#/.NET ASP.NET Core Web API backend.

## Primary Backend

- Use `backend-dotnet` for new backend work.
- Keep the public API compatible with the React frontend.
- Preserve support for both request fields: `transcript` and `transcriptText`.
- Keep `/health`, `/analyze`, and `/openapi/v1.json` available.

## Deprecated Backend

- The `backend` Python/FastAPI folder is deprecated and should be used only as a behavior reference.
- Do not delete or archive it unless explicitly requested.

## Frontend

- Keep the React/Vite frontend in `frontend`.
- Use `VITE_API_BASE_URL` for the backend URL.
- Do not hardcode deployment URLs or secrets.

## Security

- Never commit `.env` files, API keys, credentials, real transcripts, or real PII.
- Use only sanitized examples in docs, tests, and prompts.
- Keep generated folders such as `node_modules`, `venv`, `bin`, `obj`, and `__pycache__` out of Git.

## Verification

- Build the .NET backend with `dotnet build backend-dotnet/backend-dotnet.csproj`.
- Run the backend with `dotnet run --project backend-dotnet/backend-dotnet.csproj --no-launch-profile --urls http://localhost:8000`.
- Run the frontend with `npm run dev` from `frontend`.
