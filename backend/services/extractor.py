"""
Regex-based fallback extractor.

Runs when Azure AI Language is unavailable or to supplement Azure results.
Supports English and basic Armenian patterns.
"""

import re
from typing import List
from models import ExtractedAttributes

# ── Email ──────────────────────────────────────────────────────────────────────
EMAIL_RE = re.compile(r"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")

# ── Phone numbers ──────────────────────────────────────────────────────────────
# US formats: (555) 123-4567, 555-123-4567, +1 555 123 4567
# Armenian formats: +374 XX XXXXXX, 0XX XXXXXX
PHONE_RE = re.compile(
    r"""
    (?:
        \+?1[\s\-.]?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}   # US
      | \(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}                # US no-country
      | \+374[\s\-.]?\d{2}[\s\-.]?\d{6}                      # Armenia +374
      | 0\d{2}[\s\-.]?\d{6}                                   # Armenia 0xx
    )
    """,
    re.VERBOSE,
)

# ── SSN / National ID ─────────────────────────────────────────────────────────
# US SSN: 123-45-6789 or 123 45 6789
SSN_RE = re.compile(r"\b\d{3}[\s\-]\d{2}[\s\-]\d{4}\b")

# Armenian passport / ID: typically 2 letters + 7 digits, e.g. AM1234567
ARM_ID_RE = re.compile(r"\b[A-Z]{2}\d{7}\b")

# ── Name heuristics ────────────────────────────────────────────────────────────
# Use only "my name is" — avoids false positives from "I am going to...", etc.
# Armenian equivalent: "իմ անունը ... է" handled by capturing word(s) after "անունը"
NAME_RE = re.compile(
    r"(?:my name is|անունը)\s+([A-ZԱ-Ֆա-ֆ\w][a-zA-Zա-ֆ\w]+(?:\s+[A-ZԱ-Ֆա-ֆ\w][a-zA-Zա-ֆ\w]+)*)",
    re.IGNORECASE,
)

# ── Address heuristics ────────────────────────────────────────────────────────
# Looks for "I live at ...", "my address is ...", "located at ..."
ADDRESS_RE = re.compile(
    r"(?:i live at|my address is|located at|address[:\s]+)\s*(.+?)(?:\.|,\s*[A-Z]{2}\s*\d{5}|$)",
    re.IGNORECASE,
)

# ── Doctor name heuristics ────────────────────────────────────────────────────
# "my doctor is Dr. Smith", "Dr. Smith is my doctor", "see Dr. Smith"
DOCTOR_RE = re.compile(
    r"(?:dr\.?\s+([A-Z][a-zA-Z]+))",
)

# ── Condition / diagnosis heuristics ──────────────────────────────────────────
CONDITION_RE = re.compile(
    r"(?:i have|diagnosed with|suffering from)\s+([a-zA-Z][a-zA-Z\s]{2,30}?)(?:\.|,|$)",
    re.IGNORECASE,
)

# ── Medication heuristics ─────────────────────────────────────────────────────
MEDICATION_RE = re.compile(
    r"(?:i take|taking|prescribed)\s+([a-zA-Z][a-zA-Z0-9\s]{2,30}?)(?:\.|,|$)",
    re.IGNORECASE,
)


def extract_with_regex(text: str) -> ExtractedAttributes:
    """Extract structured fields from raw text using regex heuristics."""
    attrs = ExtractedAttributes()

    # Email
    emails = EMAIL_RE.findall(text)
    if emails:
        attrs.email = emails[0]

    # Phone
    phones = PHONE_RE.findall(text)
    if phones:
        attrs.phoneNumber = phones[0].strip()

    # SSN
    ssns = SSN_RE.findall(text)
    if ssns:
        attrs.socialSecurityNumber = ssns[0]
    else:
        ids = ARM_ID_RE.findall(text)
        if ids:
            attrs.socialSecurityNumber = ids[0]

    # Name
    name_match = NAME_RE.search(text)
    if name_match:
        attrs.name = name_match.group(1).strip()

    # Address
    addr_match = ADDRESS_RE.search(text)
    if addr_match:
        attrs.address = addr_match.group(1).strip()

    # Doctor name
    doctor_match = DOCTOR_RE.search(text)
    if doctor_match:
        attrs.doctorName = f"Dr. {doctor_match.group(1).strip()}"

    # Conditions / diagnoses
    for match in CONDITION_RE.finditer(text):
        condition = match.group(1).strip()
        if condition and condition not in attrs.conditions:
            attrs.conditions.append(condition)

    # Medications
    for match in MEDICATION_RE.finditer(text):
        med = match.group(1).strip()
        if med and med not in attrs.medications:
            attrs.medications.append(med)

    return attrs


_STRING_FIELDS = [
    "name", "address", "dateOfBirth",
    "socialSecurityNumber", "phoneNumber", "email", "doctorName",
]
# conditions/medications use "first non-empty source wins" like string fields:
# regex heuristics are too broad (e.g. "I have an appointment concern" matching
# the same pattern as "I have diabetes") to safely mix with OpenAI's results.
_FIRST_WINS_LIST_FIELDS = ["conditions", "medications"]
# "other" is a low-stakes catch-all bucket, safe to union across sources.
_UNION_LIST_FIELDS = ["other"]


def merge_attributes(*sources: ExtractedAttributes) -> ExtractedAttributes:
    """
    Merge multiple ExtractedAttributes, preferring earlier sources over later
    ones. String fields and conditions/medications use "first non-empty source
    wins" (so a lower-quality source can't dilute a good result). The "other"
    field takes the union across all sources, preserving first-seen order.

    Call with sources ordered best-to-worst, e.g.:
        merge_attributes(openai_attrs, azure_attrs, regex_attrs)
    """
    merged = ExtractedAttributes()

    for field in _STRING_FIELDS:
        for source in sources:
            value = getattr(source, field)
            if value:
                setattr(merged, field, value)
                break

    for field in _FIRST_WINS_LIST_FIELDS:
        for source in sources:
            value = getattr(source, field)
            if value:
                setattr(merged, field, list(value))
                break

    for field in _UNION_LIST_FIELDS:
        combined: List[str] = []
        for source in sources:
            for item in getattr(source, field):
                if item not in combined:
                    combined.append(item)
        setattr(merged, field, combined)

    return merged


def validate_attributes(attrs: ExtractedAttributes) -> ExtractedAttributes:
    """
    Sanity-check structured fields (SSN, phone, email) after extraction.

    LLM extraction can occasionally echo raw disfluent speech instead of
    resolving it (e.g. a caller who stumbles/self-corrects while reading out
    a number). Rather than trust any source blindly, re-validate the shape of
    these fields here and clear anything that doesn't look like a real value
    — a missing field is safer than a wrong-looking one for sensitive data.
    """
    attrs.socialSecurityNumber = _clean_ssn(attrs.socialSecurityNumber)
    attrs.phoneNumber = _clean_phone(attrs.phoneNumber)
    attrs.email = _clean_email(attrs.email)
    attrs.dateOfBirth = _clean_dob(attrs.dateOfBirth)
    return attrs


def _clean_ssn(value: str) -> str:
    if not value:
        return ""
    # Armenian-style passport/ID: 2 letters + 7 digits, already unambiguous
    if ARM_ID_RE.fullmatch(value.strip()):
        return value.strip()
    digits = re.sub(r"\D", "", value)
    if len(digits) == 9:
        return f"{digits[:3]}-{digits[3:5]}-{digits[5:]}"
    # Clinics commonly verify identity with just the last 4 SSN digits rather
    # than the full number — a 4-digit answer is a legitimate partial value,
    # not noise, so keep it (masked to make clear it's partial, not full).
    if len(digits) == 4:
        return f"***-**-{digits}"
    return ""  # doesn't resolve to a valid SSN shape — discard rather than guess


def _clean_phone(value: str) -> str:
    if not value:
        return ""
    digits = re.sub(r"\D", "", value)
    if value.strip().startswith("+374") and len(digits) == 11:  # 374 + 8 digits
        return f"+374 {digits[3:5]} {digits[5:]}"
    if len(digits) == 11 and digits.startswith("1"):
        digits = digits[1:]
    if len(digits) == 10:
        return f"{digits[:3]}-{digits[3:6]}-{digits[6:]}"
    return ""  # doesn't resolve to a valid phone number — discard rather than guess


def _clean_email(value: str) -> str:
    if not value:
        return ""
    match = EMAIL_RE.search(value)
    return match.group(0) if match else ""


_DOB_DD_MM_YYYY_RE = re.compile(r"^\d{2}-\d{2}-\d{4}$")


def _clean_dob(value: str) -> str:
    """
    Normalize a date of birth to DD-MM-YYYY.

    Azure OpenAI is instructed to already output this format directly (it can
    parse spoken dates in any language, which we can't easily do with regex).
    This is a safety net for the regex/Azure Language fallback paths, which
    hand back whatever raw date text they found — reformat common
    unambiguous forms (ISO, named-month) and leave anything we can't safely
    parse as-is rather than discarding a caller's stated date of birth.
    """
    if not value:
        return ""
    value = value.strip().rstrip(".")
    if _DOB_DD_MM_YYYY_RE.fullmatch(value):
        return value  # already normalized (e.g. produced by OpenAI)

    # ISO format: YYYY-MM-DD
    iso_match = re.fullmatch(r"(\d{4})-(\d{2})-(\d{2})", value)
    if iso_match:
        year, month, day = iso_match.groups()
        return f"{day}-{month}-{year}"

    try:
        from dateutil import parser as date_parser
        parsed = date_parser.parse(value, fuzzy=True)
        return parsed.strftime("%d-%m-%Y")
    except Exception:
        return value  # couldn't safely parse — keep the raw value rather than lose it
