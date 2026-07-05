"""
Heuristic speaker-role detection.

Rules (easy to extend):
  - Lines starting with "Agent:", "Caller:", "Customer:", "Rep:" are labelled directly.
  - Agent patterns: questions offering help, requests to confirm/provide, greetings.
  - Caller patterns: "My name is", "I live at", "My phone/email/SSN is", first-person statements.
  - Unknown: anything that doesn't match.
"""

import re
from typing import List
from models import ConversationTurn

# Regex patterns that strongly suggest an Agent utterance
AGENT_PATTERNS = [
    r"\bhow (can|may) (i|we) help\b",
    r"\bcan you (please )?(confirm|verify|provide|tell me|spell)\b",
    r"\bplease (provide|confirm|verify|hold|wait)\b",
    r"\bthank you for (calling|contacting|reaching)\b",
    r"\bwhat (is|was) your\b",
    r"\bwould you (like|mind)\b",
    r"\bi (will|can|am going to) (look|check|transfer|update)\b",
    r"\bis there anything else\b",
    r"\bhave a (good|great|nice)\b",
    r"\bgood (morning|afternoon|evening),?\s+(this is|welcome)\b",
]

# Regex patterns that strongly suggest a Caller utterance
CALLER_PATTERNS = [
    r"\bmy name is\b",
    r"\bi (live|am calling|need|want|have a problem|have an issue)\b",
    r"\bmy (phone|number|email|address|ssn|social security|id|case)\b",
    r"\bi (was|am) (born|located|calling)\b",
    r"\byes,?\s+(my|i|it)\b",
    r"\bno,?\s+(i|my|it)\b",
    r"\bactually\b",
]

# Explicit speaker-label prefixes in the raw transcript (English and Armenian)
EXPLICIT_LABEL = re.compile(
    r"^("
    # English labels
    r"agent|caller|customer|representative|rep|support|operator"
    r"|"
    # Armenian labels: Գործակալ (Agent), Զանգահարող (Caller), Հաճախորդ (Customer)
    r"Գործակալ|Զանգահարող|Հաճախորդ|Օպերատոր"
    r")\s*[:\-]\s*",
    re.IGNORECASE,
)

# Map Armenian label roots to role strings
_ARMENIAN_AGENT_LABELS = {"գործակալ", "օপերատոր"}
_ARMENIAN_CALLER_LABELS = {"զանգահարող", "հաճախորդ"}


def _match_any(text: str, patterns: List[str]) -> bool:
    lower = text.lower()
    return any(re.search(p, lower) for p in patterns)


def _classify_line(text: str) -> str:
    """Return 'Agent', 'Caller', or 'Unknown' for a single utterance."""
    # Check for explicit label prefix (English or Armenian)
    m = EXPLICIT_LABEL.match(text)
    if m:
        label = m.group(1).lower()
        if label in ("agent", "representative", "rep", "support", "operator") or label in _ARMENIAN_AGENT_LABELS:
            return "Agent"
        if label in ("caller", "customer") or label in _ARMENIAN_CALLER_LABELS:
            return "Caller"

    if _match_any(text, AGENT_PATTERNS):
        return "Agent"
    if _match_any(text, CALLER_PATTERNS):
        return "Caller"
    return "Unknown"


def _strip_label_prefix(text: str) -> str:
    """Remove 'Agent: ' / 'Caller: ' prefixes from the text."""
    return EXPLICIT_LABEL.sub("", text).strip()


def parse_transcript(transcript: str) -> List[ConversationTurn]:
    """
    Split the transcript into turns and assign speaker roles.

    Handles:
      1. Explicit labels  – "Agent: Hello"
      2. Blank-line-separated paragraphs treated as alternating turns
      3. Sentence-by-sentence fallback when there are no blank lines
    """
    turns: List[ConversationTurn] = []

    # Split on blank lines first
    blocks = [b.strip() for b in re.split(r"\n\s*\n", transcript) if b.strip()]

    if len(blocks) > 1:
        for block in blocks:
            role = _classify_line(block)
            text = _strip_label_prefix(block)
            turns.append(ConversationTurn(role=role, text=text))
        return turns

    # No blank-line paragraphs — try line-by-line
    lines = [l.strip() for l in transcript.splitlines() if l.strip()]
    if len(lines) > 1:
        for line in lines:
            role = _classify_line(line)
            text = _strip_label_prefix(line)
            turns.append(ConversationTurn(role=role, text=text))
        return turns

    # Single block — treat entire text as one Unknown turn
    turns.append(ConversationTurn(role="Unknown", text=transcript.strip()))
    return turns
