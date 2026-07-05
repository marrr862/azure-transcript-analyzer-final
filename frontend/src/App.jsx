import { useState, useEffect } from "react";
import { analyzeTranscript, checkHealth } from "./api";
import { ENGLISH_SAMPLE, ARMENIAN_SAMPLE } from "./samples";

// ── Small helper components ────────────────────────────────────────────────

function RoleBadge({ role }) {
  const cls =
    role === "Agent" ? "role-agent" : role === "Caller" ? "role-caller" : "role-unknown";
  return <span className={`turn-role ${cls}`}>{role}</span>;
}

function ConversationView({ turns }) {
  if (!turns?.length) return null;
  return (
    <div className="conversation">
      {turns.map((t, i) => (
        <div className="turn" key={i}>
          <RoleBadge role={t.role} />
          <span className="turn-text">{t.text}</span>
        </div>
      ))}
    </div>
  );
}

function AttributesTable({ attrs }) {
  const fields = [
    ["Name", attrs.name],
    ["Date of Birth", attrs.dateOfBirth],
    ["Address", attrs.address],
    ["SSN / National ID", attrs.socialSecurityNumber],
    ["Phone Number", attrs.phoneNumber],
    ["Email", attrs.email],
    ["Doctor", attrs.doctorName],
  ];

  const listFields = [
    ["Conditions", attrs.conditions],
    ["Medications", attrs.medications],
    ["Other", attrs.other],
  ];

  const empty = (v) => !v || v === "";

  return (
    <table className="attr-table">
      <tbody>
        {fields.map(([label, value]) => (
          <tr key={label}>
            <th>{label}</th>
            <td>
              {empty(value) ? (
                <span className="attr-value-empty">not detected</span>
              ) : (
                value
              )}
            </td>
          </tr>
        ))}
        {listFields.map(([label, items]) => (
          items?.length > 0 && (
            <tr key={label}>
              <th>{label}</th>
              <td>
                {items.map((item, i) => (
                  <span key={i} className="attr-tag">{item}</span>
                ))}
              </td>
            </tr>
          )
        ))}
      </tbody>
    </table>
  );
}

function JsonBlock({ data }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button className="json-toggle" onClick={() => setOpen((o) => !o)}>
        {open ? "▲ Hide" : "▼ Show"} raw JSON
      </button>
      {open && (
        <pre className="json-block">{JSON.stringify(data, null, 2)}</pre>
      )}
    </>
  );
}

// ── Main app ───────────────────────────────────────────────────────────────

export default function App() {
  const [transcript, setTranscript] = useState("");
  const [language, setLanguage] = useState("auto");
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState(null);
  const [error, setError] = useState(null);
  const [healthInfo, setHealthInfo] = useState(null);

  // Check backend health on mount
  useEffect(() => {
    checkHealth()
      .then(setHealthInfo)
      .catch(() => setHealthInfo(null));
  }, []);

  async function handleAnalyze() {
    if (!transcript.trim()) return;
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const data = await analyzeTranscript(transcript, language);
      setResult(data);
    } catch (err) {
      setError(err.message || "Unknown error");
    } finally {
      setLoading(false);
    }
  }

  function loadSample(sample, lang) {
    setTranscript(sample);
    setLanguage(lang);
    setResult(null);
    setError(null);
  }

  const azureOn = healthInfo?.azure_configured;

  return (
    <div className="app">
      {/* ── Header ── */}
      <header>
        <h1>Azure AI <span>Transcript Analyzer</span></h1>
        <p>Extract structured PII attributes from call-center transcripts (English & Armenian)</p>
        {healthInfo && (
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginTop: 10 }}>
            <span className={`status-badge ${azureOn ? "azure-on" : "azure-off"}`}>
              <span className="dot" />
              {azureOn ? "Azure AI Language connected" : "Azure Language — regex fallback"}
            </span>
            <span className={`status-badge ${healthInfo.openai_configured ? "azure-on" : "azure-off"}`}>
              <span className="dot" />
              {healthInfo.openai_configured
                ? "Azure OpenAI connected — smart role detection"
                : "Azure OpenAI — heuristic role detection"}
            </span>
          </div>
        )}
      </header>

      {/* ── Input card ── */}
      <div className="card">
        <h2>Transcript Input</h2>
        <textarea
          value={transcript}
          onChange={(e) => setTranscript(e.target.value)}
          placeholder="Paste your transcript here…"
          spellCheck={false}
        />
        <div className="controls">
          <select value={language} onChange={(e) => setLanguage(e.target.value)}>
            <option value="auto">Auto-detect language</option>
            <option value="en">English</option>
            <option value="hy">Armenian</option>
          </select>

          <button
            className="btn-primary"
            onClick={handleAnalyze}
            disabled={loading || !transcript.trim()}
          >
            {loading ? <><span className="spinner" /> Analyzing…</> : "▶ Analyze"}
          </button>

          <button className="btn-sample" onClick={() => loadSample(ENGLISH_SAMPLE, "en")}>
            🇺🇸 English sample
          </button>
          <button className="btn-sample" onClick={() => loadSample(ARMENIAN_SAMPLE, "hy")}>
            🇦🇲 Armenian sample
          </button>
        </div>
      </div>

      {/* ── Error ── */}
      {error && (
        <div className="error-banner">
          <strong>Error:</strong> {error}
        </div>
      )}

      {/* ── Results ── */}
      {result && (
        <>
          {result.warning && (
            <div className="warning-banner">
              ⚠ {result.warning} — results are based on regex fallback only.
            </div>
          )}

          {/* Extracted attributes */}
          <div className="card">
            <h2>Extracted Attributes</h2>
            <AttributesTable attrs={result.extractedAttributes} />
          </div>

          {/* Conversation turns */}
          <div className="card">
            <h2>
              Conversation ({result.conversation?.length} turns)
              <span style={{
                marginLeft: 10,
                fontSize: "0.72rem",
                fontWeight: 400,
                color: result.roleMethod === "openai" ? "var(--green)" : "var(--text-muted)",
                textTransform: "none",
                letterSpacing: 0,
              }}>
                {result.roleMethod === "openai" ? "⚡ roles by Azure OpenAI" : "roles by heuristic"}
              </span>
            </h2>
            <ConversationView turns={result.conversation} />
          </div>

          {/* Azure entities */}
          {result.rawAzureEntities?.length > 0 && (
            <div className="card">
              <h2>Azure Raw Entities ({result.rawAzureEntities.length})</h2>
              <table className="attr-table">
                <thead>
                  <tr>
                    <th>Text</th>
                    <th>Category</th>
                    <th>Confidence</th>
                  </tr>
                </thead>
                <tbody>
                  {result.rawAzureEntities.map((e, i) => (
                    <tr key={i}>
                      <td>{e.text}</td>
                      <td><span className="attr-tag">{e.category}</span></td>
                      <td>{(e.confidence * 100).toFixed(1)}%</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {/* Full JSON toggle */}
          <div className="card">
            <h2>Full Response</h2>
            <JsonBlock data={result} />
          </div>
        </>
      )}
    </div>
  );
}
