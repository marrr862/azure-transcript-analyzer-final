const BASE_URL = import.meta.env.VITE_API_BASE_URL || "http://localhost:8000";

/**
 * POST /analyze
 * @param {string} transcript
 * @param {string} language  "en" | "hy" | "auto"
 * @returns {Promise<object>} AnalyzeResponse
 */
export async function analyzeTranscript(transcript, language = "auto") {
  const response = await fetch(`${BASE_URL}/analyze`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ transcript, transcriptText: transcript, language }),
  });

  if (!response.ok) {
    const err = await response.json().catch(() => ({}));
    throw new Error(err.detail || `Server error ${response.status}`);
  }

  return response.json();
}

/**
 * GET /health
 * @returns {Promise<object>}
 */
export async function checkHealth() {
  const response = await fetch(`${BASE_URL}/health`);
  return response.json();
}
