export type Language = "auto" | "en" | "hy";

export interface HealthInfo {
  status: "ok";
  azure_configured: boolean;
  openai_configured: boolean;
  backend: string;
}

export interface ConversationTurn {
  role: "Agent" | "Caller" | "Unknown" | string;
  text: string;
}

export interface ExtractedAttributes {
  name: string;
  address: string;
  dateOfBirth: string;
  socialSecurityNumber: string;
  phoneNumber: string;
  email: string;
  doctorName: string;
  conditions: string[];
  medications: string[];
  other: string[];
  importantDetails: string[];
}

export interface RawAzureEntity {
  text: string;
  category: string;
  subcategory?: string;
  confidence: number;
}

export interface AttributeEvidence {
  field: string;
  label: string;
  value: string;
  confidence: number;
  source: string;
  snippet: string;
}

export interface AnalyzeResponse {
  conversation: ConversationTurn[];
  extractedAttributes: ExtractedAttributes;
  rawAzureEntities: RawAzureEntity[];
  attributeEvidence: AttributeEvidence[];
  warning?: string;
  roleMethod: string;
  detectedLanguage: Language | string;
  translationMethod: "none" | "openai" | string;
  translatedTranscript?: string;
}

export interface AnalyzePayload {
  transcriptText: string;
  language: Language;
}

export interface AnalysisHistoryItem {
  id: string;
  fileName: string;
  createdAtUtc: string;
  language: string;
  detectedLanguage: string;
  translationMethod: string;
  roleMethod: string;
  transcriptLength: number;
}

export interface AnalysisHistoryDetail extends AnalysisHistoryItem {
  content: string;
}
