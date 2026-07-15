import axios from "axios";
import type {
  AnalysisHistoryDetail,
  AnalysisHistoryItem,
  AnalyzePayload,
  AnalyzeResponse,
  HealthInfo
} from "./types";

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL?.replace(/\/$/, "") || "/api";

const api = axios.create({
  baseURL: apiBaseUrl,
  headers: {
    "Content-Type": "application/json"
  }
});

export async function checkHealth() {
  const { data } = await api.get<HealthInfo>("/health");
  return data;
}

export async function analyzeTranscript(payload: AnalyzePayload) {
  const { data } = await api.post<AnalyzeResponse>("/analyze", {
    language: payload.language,
    transcriptText: payload.transcriptText
  });
  return data;
}

export async function fetchHistory() {
  const { data } = await api.get<AnalysisHistoryItem[]>("/history");
  return data;
}

export async function fetchHistoryDetail(id: string) {
  const { data } = await api.get<AnalysisHistoryDetail>(`/history/${encodeURIComponent(id)}`);
  return data;
}

export async function deleteHistoryItem(id: string) {
  await api.delete(`/history/${encodeURIComponent(id)}`);
}

export function toApiError(error: unknown) {
  if (axios.isAxiosError<{ detail?: string }>(error)) {
    return error.response?.data?.detail || error.message;
  }

  return error instanceof Error ? error.message : "Unknown error";
}
