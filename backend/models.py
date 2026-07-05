from pydantic import BaseModel
from typing import List, Optional, Any


class AnalyzeRequest(BaseModel):
    language: str = "auto"  # "en", "hy", or "auto"
    transcript: str


class ConversationTurn(BaseModel):
    role: str  # "Agent", "Caller", or "Unknown"
    text: str


class ExtractedAttributes(BaseModel):
    name: str = ""  # full name, including middle name if given
    address: str = ""
    dateOfBirth: str = ""
    socialSecurityNumber: str = ""
    phoneNumber: str = ""
    email: str = ""
    doctorName: str = ""
    conditions: List[str] = []   # diagnoses / diseases mentioned
    medications: List[str] = []  # medications / dosages mentioned
    other: List[str] = []


class AnalyzeResponse(BaseModel):
    conversation: List[ConversationTurn]
    extractedAttributes: ExtractedAttributes
    rawAzureEntities: List[Any] = []
    warning: Optional[str] = None
    roleMethod: str = "heuristic"  # "openai" | "heuristic"
