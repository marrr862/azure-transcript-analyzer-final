"""
Azure AI Language (Text Analytics) integration.

Extracts named entities (PII) from the transcript.
Falls back gracefully if credentials are missing or the SDK call fails.
"""

import os
import logging
from typing import List, Tuple, Any

from models import ExtractedAttributes

logger = logging.getLogger(__name__)

# Category → field mapping
_CATEGORY_MAP = {
    "Person": "name",
    "Address": "address",
    "PhoneNumber": "phoneNumber",
    "Email": "email",
    "USSocialSecurityNumber": "socialSecurityNumber",
    "EUPassportNumber": "socialSecurityNumber",
    "InternationalBankingAccountNumber": "other",
    "Organization": "other",
    "DateTime": "other",
    "URL": "other",
    "IPAddress": "other",
    "Age": "other",
    "Quantity": "other",
    "CreditCardNumber": "other",
    "BankAccountNumber": "other",
}


def _get_client():
    """Create an Azure Text Analytics client from environment variables."""
    endpoint = os.getenv("AZURE_LANGUAGE_ENDPOINT", "").strip()
    key = os.getenv("AZURE_LANGUAGE_KEY", "").strip()

    # Treat placeholder values (from .env.example) as unconfigured
    if not endpoint or not key or "<" in endpoint or "<" in key:
        return None, "AZURE_LANGUAGE_ENDPOINT or AZURE_LANGUAGE_KEY not set"

    try:
        from azure.ai.textanalytics import TextAnalyticsClient
        from azure.core.credentials import AzureKeyCredential

        client = TextAnalyticsClient(
            endpoint=endpoint,
            credential=AzureKeyCredential(key),
        )
        return client, None
    except ImportError:
        return None, "azure-ai-textanalytics package not installed"
    except Exception as exc:
        return None, str(exc)


def _map_language(lang_code: str, text: str) -> str:
    """
    Return an Azure-compatible language code.
    'auto' triggers Azure language detection (pass 'auto' to the API isn't
    valid; we omit language and let Azure detect it, or default to 'en').
    """
    if lang_code == "hy":
        return "hy"
    if lang_code == "en":
        return "en"
    # For 'auto', return None so callers can omit the language parameter
    return None  # type: ignore


def analyze_with_azure(
    text: str,
    language: str = "auto",
) -> Tuple[ExtractedAttributes, List[Any], str | None]:
    """
    Run PII entity recognition via Azure AI Language.

    Returns (ExtractedAttributes, raw_entities_list, warning_or_None).
    """
    client, err = _get_client()
    if client is None:
        return ExtractedAttributes(), [], err

    lang_code = _map_language(language, text)

    try:
        # Azure accepts max 5120 chars per document in the free tier
        doc = {"id": "1", "text": text[:5120]}
        if lang_code:
            doc["language"] = lang_code  # type: ignore

        response = client.recognize_pii_entities(
            documents=[doc],
            show_stats=False,
        )

        result = response[0]
        if result.is_error:
            return (
                ExtractedAttributes(),
                [],
                f"Azure error {result.error.code}: {result.error.message}",
            )

        attrs = ExtractedAttributes()
        raw: List[Any] = []

        # Track the best (highest-confidence) candidate per single-value field
        # so a low-quality match (e.g. Azure mis-tagging "what" as a Person)
        # doesn't win just because it appeared first in the entity list.
        best_confidence = {"name": -1.0, "address": -1.0, "phoneNumber": -1.0,
                            "email": -1.0, "socialSecurityNumber": -1.0}

        # Ignore entities below this confidence — filters out noise like
        # Azure tagging common words ("you", "hi") as low-confidence Person hits.
        MIN_CONFIDENCE = 0.6

        for entity in result.entities:
            raw.append(
                {
                    "text": entity.text,
                    "category": entity.category,
                    "subcategory": entity.subcategory,
                    "confidence": round(entity.confidence_score, 3),
                }
            )

            if entity.confidence_score < MIN_CONFIDENCE:
                continue

            field = _CATEGORY_MAP.get(entity.category)
            if field in best_confidence:
                if entity.confidence_score > best_confidence[field]:
                    best_confidence[field] = entity.confidence_score
                    setattr(attrs, field, entity.text)
            elif field == "other":
                label = f"{entity.category}: {entity.text}"
                if label not in attrs.other:
                    attrs.other.append(label)

        return attrs, raw, None

    except Exception as exc:  # noqa: BLE001
        logger.exception("Azure PII call failed")
        return ExtractedAttributes(), [], f"Azure call failed: {exc}"
