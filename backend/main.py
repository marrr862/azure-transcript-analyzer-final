"""
Azure AI Transcript Analyzer — FastAPI backend.

Endpoints:
  GET  /health   — liveness check
  POST /analyze  — analyze a transcript and return structured JSON
"""

import os
from dotenv import load_dotenv

load_dotenv()  # load .env before importing services

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware

from models import AnalyzeRequest, AnalyzeResponse, ExtractedAttributes
from services.speaker_roles import parse_transcript
from services.extractor import extract_with_regex, merge_attributes, validate_attributes
from services.azure_language import analyze_with_azure
from services.openai_roles import (
    parse_with_openai,
    extract_attributes_with_openai,
    is_configured as openai_is_configured,
)

app = FastAPI(
    title="Azure AI Transcript Analyzer",
    version="1.0.0",
    description="Extracts structured PII attributes from call-center transcripts.",
)

# Allow the Vite dev server (and any localhost origin) to call the API
app.add_middleware(
    CORSMiddleware,
    allow_origins=[
        "http://localhost:5173",
        "http://localhost:3000",
        "http://127.0.0.1:5173",
    ],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.get("/health")
def health():
    """Liveness check — also reports whether Azure credentials are configured."""
    endpoint = os.getenv("AZURE_LANGUAGE_ENDPOINT", "")
    key = os.getenv("AZURE_LANGUAGE_KEY", "")
    # Treat placeholder values from .env.example as unconfigured
    azure_configured = bool(
        endpoint and key and "<" not in endpoint and "<" not in key
    )
    return {
        "status": "ok",
        "azure_configured": azure_configured,
        "openai_configured": openai_is_configured(),
    }


@app.post("/analyze", response_model=AnalyzeResponse)
def analyze(req: AnalyzeRequest):
    """
    Analyze a transcript:
      1. Parse it into speaker turns (Azure OpenAI, falls back to heuristic).
      2. Try Azure AI Language PII extraction (standard fields).
      3. Run regex fallback extraction.
      4. Try Azure OpenAI full attribute extraction (adds clinic-specific fields).
      5. Merge results (OpenAI preferred, then Azure Language, then regex).
      6. Return structured JSON.
    """
    if not req.transcript or not req.transcript.strip():
        raise HTTPException(status_code=400, detail="transcript must not be empty")

    language = req.language if req.language in ("en", "hy", "auto") else "auto"

    # Step 1 — speaker role detection
    # Try Azure OpenAI first (context-aware); fall back to keyword heuristic.
    # role_method reflects which path actually produced the result, not just
    # whether OpenAI is configured (the call can still fail at runtime).
    openai_turns = parse_with_openai(req.transcript)
    if openai_turns:
        conversation = openai_turns
        role_method = "openai"
    else:
        conversation = parse_transcript(req.transcript)
        role_method = "heuristic"

    # Step 2 — Azure PII extraction (standard fields: name/address/phone/email/SSN)
    azure_attrs, raw_entities, azure_warning = analyze_with_azure(
        req.transcript, language
    )

    # Step 3 — regex fallback (always runs, cheapest safety net)
    regex_attrs = extract_with_regex(req.transcript)

    # Step 4 — Azure OpenAI full attribute extraction (name, contact info, and
    # clinic-specific fields like conditions/medications/doctor name that
    # regex and standard PII models can't reliably infer from context)
    openai_attrs = extract_attributes_with_openai(req.transcript)

    # Step 5 — merge, preferring OpenAI (best context understanding) over
    # Azure Language PII (accurate for standard fields) over regex (fallback)
    sources = [s for s in (openai_attrs, azure_attrs, regex_attrs) if s is not None]
    final_attrs = merge_attributes(*sources)

    # Step 6 — validate structured fields (SSN/phone/email); clear anything
    # that doesn't resolve to a real value instead of showing raw noise
    # (e.g. a caller's self-corrected number echoed verbatim by the LLM)
    final_attrs = validate_attributes(final_attrs)

    return AnalyzeResponse(
        conversation=conversation,
        extractedAttributes=final_attrs,
        rawAzureEntities=raw_entities,
        warning=azure_warning,
        roleMethod=role_method,
    )
