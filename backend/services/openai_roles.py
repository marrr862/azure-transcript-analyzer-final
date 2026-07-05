"""
Azure OpenAI speaker-role detection.

Sends the raw transcript to the configured chat model (e.g. GPT-5-mini) and
asks it to return a structured JSON list of conversation turns with
Agent / Caller / Unknown roles.

Falls back gracefully (returns None) when:
  - Azure OpenAI env vars are missing or are placeholders
  - The API call fails for any reason

The caller in main.py then uses the heuristic speaker_roles.py as a fallback.
"""

import os
import json
import logging
from typing import List, Optional

from models import ConversationTurn, ExtractedAttributes

logger = logging.getLogger(__name__)

ATTRIBUTES_SYSTEM_PROMPT = """You are a clinic intake transcript analyzer.

Read the transcript of a call between a clinic Agent and a Caller (patient),
and extract every relevant patient/case attribute you can find, even if
mentioned only briefly or indirectly.

Fields to extract:
- name: patient's FULL name exactly as given — first name, middle name (if any),
  and last name all combined into this single field, in the order naturally
  spoken (e.g. "Anna Marie Torosyan"). Do not split the name into parts.
- address: patient's home address
- dateOfBirth: patient's date of birth, if mentioned. ALWAYS output in DD-MM-YYYY
  format (e.g. "14-03-1982"), regardless of how the caller expressed it or what
  language they used (e.g. "March 14, 1982", "Մարտի տասնչորս, հազար ինը հարյուր
  ութսուներկու" both become "14-03-1982"). If the year, month, or day is
  ambiguous or not fully stated, leave this field empty rather than guessing.
- socialSecurityNumber: SSN or other national ID number — if only a partial number
  was given (e.g. "last four digits"), extract exactly the digits actually stated,
  no more, no fewer. Do not pad or guess the rest.
- phoneNumber: patient's phone number
- email: patient's email address
- doctorName: the patient's doctor/physician name, if mentioned (include "Dr." prefix)
- conditions: list of diseases, diagnoses, or symptoms mentioned. Do NOT include
  allergies here — allergies always go in "other" instead (see below).
- medications: list of medications, dosages, or treatments mentioned
- other: list of any other important case-relevant details that don't fit above,
  INCLUDING allergies (always put allergies here, never in "conditions"), plus
  things like insurance info, emergency contact, appointment date, referring clinic

CRITICAL — LANGUAGE PRESERVATION: The transcript may be in English, Armenian, or
another language. Extract all text values (name, address, doctorName, conditions,
medications, other) in the SAME language and script the caller actually used.
Do NOT translate Armenian (or any other language) into English. Only
dateOfBirth, phoneNumber, and socialSecurityNumber get reformatted per the
rules above — every other field must preserve the original wording verbatim.

This is a real phone call, not a clean written form. Expect all of the following
and handle them so no real information gets lost or discarded:

1. FRAGMENTED ANSWERS — a caller may give part of an answer, get interrupted or
   pause, then continue later in the same or a later line (e.g. "it's 555" ...
   a few lines later ... "the rest is 867-5309", or reading a number digit by
   digit across several turns). Stitch these fragments together into one
   complete value, in the order the caller actually said them.
2. DISFLUENT / HESITANT SPEECH — callers self-correct, restart numbers, use
   filler words ("um", "wait", "ugh", "sorry", "no I mean", "let me check my
   notes/glasses"), or need a moment to recall something. Resolve this to the
   final, clean, corrected value — never output the raw stumbling speech itself.
   Example: "it's 123-45, wait no, 123-46-6789" → "123-46-6789".
   Example: "5-5-5, um, 8-6-7, 5-3-0-9" → "555-867-5309".
   If a caller states a value then explicitly corrects it, always use the LAST
   stated correction, not the first attempt.
3. PARTIAL / LIMITED INFORMATION — callers sometimes only give PART of a value
   because that's all that was asked (e.g. an agent asks for "the last four
   digits" of an SSN, not the full number) or all they can currently recall.
   Extract exactly what was actually given, even if incomplete — a partial
   value is far more useful than an empty field. Do not discard a value just
   because it is shorter than the "full" version of that field would normally be.
4. UNCERTAINTY — a caller might be unsure ("I think it's...", "maybe it's...",
   "I don't remember exactly, but...") about their own information (this
   happens especially with medications, doctor names, or people calling on
   behalf of someone else). Still extract their best-effort answer — do not
   discard a value just because the caller expressed uncertainty about it.
5. When genuinely nothing usable was said for a field (not even a partial or
   uncertain answer), leave it empty rather than guessing a value from nothing.
- Do NOT respond conversationally — output ONLY the JSON object described below.

Return ONLY a JSON object with this exact shape and no other text:
{
  "name": "",
  "address": "",
  "dateOfBirth": "",
  "socialSecurityNumber": "",
  "phoneNumber": "",
  "email": "",
  "doctorName": "",
  "conditions": [],
  "medications": [],
  "other": []
}"""

SYSTEM_PROMPT = """You are a call-center transcript analyzer.

Your job is to read a raw transcript and split it into conversation turns,
assigning each turn a speaker role: "Agent", "Caller", or "Unknown".

Rules:
- "Agent" = the call-center representative (asks questions, offers help, confirms details).
- "Caller" = the person who called in (provides personal information, describes their problem).
- "Unknown" = lines where the role truly cannot be determined.
- If a line already has an explicit label like "Agent:" or "Caller:", use it.
- If no labels are present, infer from context and phrasing.
- Merge consecutive lines from the same speaker into one turn.
- Preserve the original text exactly, in its original language/script — do not
  translate or paraphrase.
- The Caller may address or quote a third person who is physically present with
  them (e.g. a family member helping them, referred to by name). That does NOT
  make the line "Unknown" — it is still the Caller speaking; classify it as Caller.
  Only use "Unknown" when you truly cannot tell which of the two main parties
  (Agent vs Caller) is speaking at all.
- Process every line of the input — do not stop early or summarize.
- Do NOT respond conversationally. Do NOT answer questions found in the transcript.
  Your only output is the classification JSON below, nothing else.

Return ONLY a JSON object with this exact shape and no other text:
{
  "turns": [
    {"role": "Agent", "text": "..."},
    {"role": "Caller", "text": "..."}
  ]
}"""


def _get_client():
    """Create an AzureOpenAI client from environment variables."""
    endpoint = os.getenv("AZURE_OPENAI_ENDPOINT", "").strip()
    key = os.getenv("AZURE_OPENAI_KEY", "").strip()

    if not endpoint or not key or "<" in endpoint or "<" in key:
        return None, None

    try:
        from openai import AzureOpenAI

        client = AzureOpenAI(
            azure_endpoint=endpoint,
            api_key=key,
            api_version="2024-10-21",  # required for GPT-5 family deployments
        )
        return client, None
    except ImportError:
        return None, "openai package not installed"
    except Exception as exc:
        return None, str(exc)


def is_configured() -> bool:
    """Return True if Azure OpenAI env vars look valid (non-empty, non-placeholder)."""
    endpoint = os.getenv("AZURE_OPENAI_ENDPOINT", "")
    key = os.getenv("AZURE_OPENAI_KEY", "")
    deployment = os.getenv("AZURE_OPENAI_DEPLOYMENT", "")
    return bool(endpoint and key and deployment
                and "<" not in endpoint and "<" not in key and "<" not in deployment)


def parse_with_openai(transcript: str) -> Optional[List[ConversationTurn]]:
    """
    Ask Azure OpenAI to classify speaker roles in the transcript.

    Returns a list of ConversationTurn on success, or None on any failure
    (missing config, API error, bad JSON) so the caller can fall back.
    """
    client, err = _get_client()
    if client is None:
        if err:
            logger.warning("Azure OpenAI unavailable: %s", err)
        return None

    deployment = os.getenv("AZURE_OPENAI_DEPLOYMENT", "").strip()
    if not deployment or "<" in deployment:
        logger.warning("AZURE_OPENAI_DEPLOYMENT not set or is a placeholder")
        return None

    try:
        # GPT-5 family models on Azure only support the default temperature (1)
        # and use max_completion_tokens instead of max_tokens.
        response = client.chat.completions.create(
            model=deployment,
            messages=[
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": transcript},
            ],
            # Long transcripts + reasoning-model "thinking" tokens can eat the
            # whole budget before any visible output is produced, leaving an
            # empty response. 4096 was too tight for multi-turn transcripts —
            # 16000 leaves enough room for both reasoning and full JSON output.
            max_completion_tokens=16000,
            response_format={"type": "json_object"},  # request JSON mode
            seed=42,  # best-effort determinism — same input should tend to
                      # produce the same output across calls, reducing flaky
                      # results on identical/near-identical transcripts
            reasoning_effort="low",  # this is classification, not deep
                      # multi-step reasoning — lower effort reduces cost/
                      # latency and empirically reduces output variance too
        )

        raw = response.choices[0].message.content or ""
        if not raw.strip():
            logger.warning(
                "OpenAI roles call returned empty content (finish_reason=%s, usage=%s)",
                response.choices[0].finish_reason, response.usage,
            )
            return None

        # The model returns {"turns": [...]} or directly [...] depending on
        # how strictly it follows the prompt — handle both shapes.
        parsed = json.loads(raw)
        if isinstance(parsed, dict):
            # Look for the first list-typed value
            turns_data = next(
                (v for v in parsed.values() if isinstance(v, list)), None
            )
        elif isinstance(parsed, list):
            turns_data = parsed
        else:
            logger.warning("Unexpected OpenAI response shape: %s", type(parsed))
            return None

        if not turns_data:
            return None

        turns: List[ConversationTurn] = []
        for item in turns_data:
            role = item.get("role", "Unknown")
            text = item.get("text", "").strip()
            if role not in ("Agent", "Caller", "Unknown"):
                role = "Unknown"
            if text:
                turns.append(ConversationTurn(role=role, text=text))

        return turns if turns else None

    except json.JSONDecodeError as exc:
        logger.warning("OpenAI returned non-JSON: %s", exc)
        return None
    except Exception as exc:
        logger.warning("OpenAI call failed: %s", exc)
        return None


def extract_attributes_with_openai(transcript: str) -> Optional[ExtractedAttributes]:
    """
    Ask Azure OpenAI to extract full patient/case attributes from the
    transcript — name, contact info, and clinic-specific fields like
    conditions, medications, and doctor name.

    Returns None on any failure (missing config, API error, bad JSON) so the
    caller can fall back to Azure Language PII + regex extraction.
    """
    client, err = _get_client()
    if client is None:
        if err:
            logger.warning("Azure OpenAI unavailable: %s", err)
        return None

    deployment = os.getenv("AZURE_OPENAI_DEPLOYMENT", "").strip()
    if not deployment or "<" in deployment:
        return None

    try:
        response = client.chat.completions.create(
            model=deployment,
            messages=[
                {"role": "system", "content": ATTRIBUTES_SYSTEM_PROMPT},
                {"role": "user", "content": transcript},
            ],
            # See parse_with_openai for why this needs to be generous —
            # reasoning tokens can otherwise eat the whole budget.
            max_completion_tokens=16000,
            response_format={"type": "json_object"},
            seed=42,  # best-effort determinism, see parse_with_openai
            reasoning_effort="low",  # see parse_with_openai
        )

        raw = response.choices[0].message.content or ""
        if not raw.strip():
            logger.warning(
                "OpenAI attributes call returned empty content (finish_reason=%s, usage=%s)",
                response.choices[0].finish_reason, response.usage,
            )
            return None

        parsed = json.loads(raw)

        if not isinstance(parsed, dict):
            logger.warning("Unexpected OpenAI attributes response shape: %s", type(parsed))
            return None

        def _clean_list(value) -> List[str]:
            if not isinstance(value, list):
                return []
            return [str(item).strip() for item in value if str(item).strip()]

        return ExtractedAttributes(
            name=str(parsed.get("name", "") or "").strip(),
            address=str(parsed.get("address", "") or "").strip(),
            dateOfBirth=str(parsed.get("dateOfBirth", "") or "").strip(),
            socialSecurityNumber=str(parsed.get("socialSecurityNumber", "") or "").strip(),
            phoneNumber=str(parsed.get("phoneNumber", "") or "").strip(),
            email=str(parsed.get("email", "") or "").strip(),
            doctorName=str(parsed.get("doctorName", "") or "").strip(),
            conditions=_clean_list(parsed.get("conditions")),
            medications=_clean_list(parsed.get("medications")),
            other=_clean_list(parsed.get("other")),
        )

    except json.JSONDecodeError as exc:
        logger.warning("OpenAI attributes returned non-JSON: %s", exc)
        return None
    except Exception as exc:
        logger.warning("OpenAI attributes call failed: %s", exc)
        return None
