import { yupResolver } from "@hookform/resolvers/yup";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  App as AntApp,
  Button,
  ConfigProvider,
  Descriptions,
  Empty,
  Flex,
  Layout,
  List,
  Progress,
  Select,
  Space,
  Spin,
  Steps,
  Table,
  Tag,
  Typography,
  Upload
} from "antd";
import { theme } from "antd";
import type { ColumnsType } from "antd/es/table";
import TextArea from "antd/es/input/TextArea";
import dayjs from "dayjs";
import styled from "@emotion/styled";
import * as React from "react";
import { Controller, useForm, useWatch } from "react-hook-form";
import { Link, Route, Routes, useNavigate, useParams } from "react-router-dom";
import * as yup from "yup";
import {
  analyzeTranscript,
  checkHealth,
  deleteHistoryItem,
  fetchHistory,
  fetchHistoryDetail,
  toApiError
} from "./api";
import { ARMENIAN_SAMPLE, ENGLISH_SAMPLE } from "./samples";
import type {
  AnalysisHistoryItem,
  AnalyzePayload,
  AnalyzeResponse,
  AttributeEvidence,
  ConversationTurn,
  ExtractedAttributes,
  Language,
  RawAzureEntity
} from "./types";

const { Header, Content } = Layout;
const { Text, Title } = Typography;
type ThemeMode = "light" | "dark";
const DRAFT_KEY = "transcriptAnalyzerDraft";
const LAST_RESULT_KEY = "transcriptAnalyzerLastResult";

const schema = yup.object({
  transcriptText: yup
    .string()
    .trim()
    .min(8, "Enter at least 8 characters")
    .required("Transcript is required"),
  language: yup.mixed<Language>().oneOf(["auto", "en", "hy"]).required()
});

const PageShell = styled(Layout)`
  min-height: 100vh;
  background: var(--app-bg);
  transition: background 0.2s ease;
`;

const TopBar = styled(Header)`
  height: 56px;
  padding: 0 28px;
  background: #252525;
  color: #ffffff;
  border-bottom: 1px solid #1e1e1e;
`;

const WorkArea = styled(Content)`
  padding: 0;
`;

const Workspace = styled.main`
  width: min(1180px, calc(100vw - 48px));
  margin: 0 auto;
  padding: 96px 0 56px;

  @media (max-width: 760px) {
    width: calc(100vw - 28px);
    padding: 44px 0 36px;
  }
`;

const Panel = styled.section`
  background: var(--panel-bg);
  border: 1px solid var(--panel-border);
  border-radius: 8px;
  padding: 18px;
  box-shadow: var(--panel-shadow);
`;

const Hero = styled.section`
  text-align: center;
  margin-bottom: 22px;
`;

const AppMark = styled.div`
  width: 36px;
  height: 36px;
  margin: 0 auto 10px;
  display: grid;
  place-items: center;
  border-radius: 8px;
  color: #ffffff;
  font-weight: 700;
  background: linear-gradient(135deg, #6c63ff, #13c2c2);
`;

const InputCard = styled(Panel)`
  margin-bottom: 22px;
`;

const ResultCanvas = styled.div`
  display: grid;
  grid-template-columns: minmax(0, 1fr) 320px;
  gap: 18px;
  align-items: start;
  margin-top: 18px;

  @media (max-width: 1000px) {
    grid-template-columns: 1fr;
  }
`;

const ConversationStack = styled.div`
  display: flex;
  flex-direction: column;
  gap: 10px;
`;

const Bubble = styled.div<{ role: string }>`
  align-self: ${({ role }) => (role === "Agent" ? "flex-start" : "flex-end")};
  max-width: min(760px, 90%);
  padding: 10px 12px;
  border: 1px solid ${({ role }) => (role === "Agent" ? "var(--panel-border)" : "var(--accent-soft-border)")};
  border-radius: 8px;
  background: ${({ role }) => (role === "Agent" ? "var(--panel-bg)" : "var(--accent-soft)")};
`;

const PreBlock = styled.pre`
  margin: 0;
  padding: 16px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-word;
  border-radius: 8px;
  background: var(--muted-bg);
  border: 1px solid var(--panel-border);
  color: var(--text-main);
  font-size: 0.82rem;
  line-height: 1.55;
`;

const RecentPanel = styled(Panel)`
  margin-top: 22px;
`;

const AttributeStack = styled.div`
  display: flex;
  flex-direction: column;
  gap: 10px;
`;

const AttributeCard = styled.div`
  padding: 12px;
  border: 1px solid var(--panel-border);
  border-radius: 8px;
  background: var(--panel-bg);
`;

const ThemeToggle = styled.button`
  min-width: 86px;
  height: 32px;
  padding: 0 12px;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.08);
  color: #ffffff;
  cursor: pointer;
`;

function HealthStatus() {
  const { data } = useQuery({ queryKey: ["health"], queryFn: checkHealth });

  if (!data) {
    return null;
  }

  return (
    <Space wrap>
      <Tag color={data.azure_configured ? "green" : "gold"}>
        {data.azure_configured ? "Azure AI Language connected" : "Regex fallback"}
      </Tag>
      <Tag color={data.openai_configured ? "green" : "gold"}>
        {data.openai_configured ? "Azure OpenAI connected" : "Heuristic roles"}
      </Tag>
      <Tag>{data.backend}</Tag>
    </Space>
  );
}

function RoleTag({ role }: { role: string }) {
  const color = role === "Agent" ? "blue" : role === "Caller" ? "green" : "default";
  return <Tag color={color}>{role}</Tag>;
}

function ConversationView({ turns }: { turns: ConversationTurn[] }) {
  const normalizedTurns = normalizeConversationTurns(turns);

  if (!normalizedTurns.length) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No turns detected" />;
  }

  return (
    <ConversationStack>
      {normalizedTurns.map((turn, index) => (
        <Bubble key={`${turn.role}-${index}`} role={turn.role}>
          <Space direction="vertical" size={4}>
            <RoleTag role={turn.role} />
            <Text>{turn.text}</Text>
          </Space>
        </Bubble>
      ))}
    </ConversationStack>
  );
}

function AttributesTable({ attrs }: { attrs: ExtractedAttributes }) {
  const rows = [
    ["Name", attrs.name],
    ["Date of Birth", attrs.dateOfBirth],
    ["Address", attrs.address],
    ["SSN / National ID", attrs.socialSecurityNumber],
    ["Phone Number", attrs.phoneNumber],
    ["Email", attrs.email],
    ["Doctor", attrs.doctorName],
    ["Conditions", attrs.conditions],
    ["Medications", attrs.medications],
    ["Important Details", attrs.importantDetails],
    ["Other", attrs.other]
  ];

  return (
    <Descriptions bordered column={1} size="small">
      {rows.map(([label, value]) => (
        <Descriptions.Item key={label as string} label={label}>
          {Array.isArray(value) ? (
            value.length ? (
              <Space wrap>
                {value.map((item) => (
                  <Tag key={item}>{item}</Tag>
                ))}
              </Space>
            ) : (
              <Text type="secondary">not detected</Text>
            )
          ) : value ? (
            value
          ) : (
            <Text type="secondary">not detected</Text>
          )}
        </Descriptions.Item>
      ))}
    </Descriptions>
  );
}

function FlaggedAttributes({ attrs, evidence }: { attrs: ExtractedAttributes; evidence: AttributeEvidence[] }) {
  const findEvidence = (field: string, value: string) =>
    evidence.find((item) => item.field === field && item.value.toLowerCase() === value.toLowerCase());

  const primary = [
    ["name", "Person Name", attrs.name],
    ["phoneNumber", "Phone Number", attrs.phoneNumber],
    ["email", "Email", attrs.email],
    ["address", "Address", attrs.address],
    ["doctorName", "Doctor", attrs.doctorName],
    ["dateOfBirth", "Date of Birth", attrs.dateOfBirth],
    ["socialSecurityNumber", "SSN / National ID", attrs.socialSecurityNumber]
  ].filter(([, , value]) => Boolean(value));

  const detailRows = [
    ["Important", attrs.importantDetails, "processing"],
    ["Conditions", attrs.conditions, "purple"],
    ["Medications", attrs.medications, "cyan"],
    ["Other", attrs.other, "default"]
  ].filter(([, value]) => Array.isArray(value) && value.length > 0);

  if (!primary.length && !detailRows.length) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No flagged attributes" />;
  }

  return (
    <AttributeStack>
      {primary.map(([field, label, value]) => {
        const itemEvidence = findEvidence(field as string, value as string);
        const confidence = itemEvidence?.confidence ?? 0.84;

        return (
        <AttributeCard key={field as string}>
          <Flex justify="space-between" align="center" gap={10}>
            <Space direction="vertical" size={2} style={{ minWidth: 0 }}>
              <Text type="secondary" style={{ fontSize: 11, textTransform: "uppercase" }}>
                {label}
              </Text>
              <Text strong>{value}</Text>
            </Space>
            <Tag color={confidence >= 0.9 ? "green" : "gold"}>
              {formatConfidence(confidence)}
            </Tag>
          </Flex>
        </AttributeCard>
        );
      })}

      {detailRows.map(([label, values, color]) => (
        <AttributeCard key={label as string}>
          <Space direction="vertical" size={8} style={{ width: "100%" }}>
            <Text type="secondary" style={{ fontSize: 11, textTransform: "uppercase" }}>
              {label}
            </Text>
            <Space wrap>
              {(values as string[]).map((value) => (
                <Tag key={value} color={color as string}>
                  {value}
                </Tag>
              ))}
            </Space>
          </Space>
        </AttributeCard>
      ))}
    </AttributeStack>
  );
}

function RawEntityTable({ entities }: { entities: RawAzureEntity[] }) {
  const columns: ColumnsType<RawAzureEntity> = [
    { title: "Text", dataIndex: "text", key: "text" },
    {
      title: "Category",
      key: "category",
      render: (_, entity) => (
        <Space wrap>
          <Tag>{entity.category}</Tag>
          {entity.subcategory ? <Tag>{entity.subcategory}</Tag> : null}
        </Space>
      )
    },
    {
      title: "Confidence",
      dataIndex: "confidence",
      key: "confidence",
      render: (value: number) => `${(value * 100).toFixed(1)}%`
    }
  ];

  return (
    <Table
      rowKey={(entity, index) => `${entity.text}-${entity.category}-${index}`}
      columns={columns}
      dataSource={entities}
      pagination={false}
      size="small"
    />
  );
}

function AnalysisProgressPanel({
  active,
  wordCount,
  estimatedChunks,
  language,
  transcript,
  lastSyncedAt,
  runId
}: {
  active: boolean;
  wordCount: number;
  estimatedChunks: number;
  language: Language;
  transcript: string;
  lastSyncedAt: Date | null;
  runId: number;
}) {
  const progress = useAnalysisProgress(active, wordCount, estimatedChunks, language, transcript, runId);

  if (!active && !lastSyncedAt) {
    return null;
  }

  if (!active && lastSyncedAt) {
    return (
      <Alert
        type="success"
        showIcon
        message="Synced"
        description={`Analysis result and local history were synchronized at ${dayjs(lastSyncedAt).format("HH:mm:ss")}.`}
      />
    );
  }

  return (
    <Panel>
      <Space direction="vertical" size={12} style={{ width: "100%" }}>
        <Flex justify="space-between" align="center" wrap="wrap" gap={8}>
          <Space direction="vertical" size={0}>
            <Text strong>{progress.label}</Text>
            <Text type="secondary">{progress.description}</Text>
          </Space>
          <Tag color="blue">{Math.max(1, estimatedChunks).toLocaleString()} chunks</Tag>
        </Flex>
        <Progress percent={progress.percent} status="active" />
        <Steps
          size="small"
          current={progress.step}
          items={progress.steps.map((title) => ({ title }))}
        />
      </Space>
    </Panel>
  );
}

function ResultPanel({ result }: { result: AnalyzeResponse }) {
  return (
    <Space direction="vertical" size={16} style={{ width: "100%" }}>
      <Panel>
        <Flex justify="space-between" align="center" wrap="wrap" gap={12}>
          <Title level={4} style={{ margin: 0 }}>
            Analysis Summary
          </Title>
          <Space wrap>
            <Tag color="cyan">Detected: {displayLanguage(result.detectedLanguage)}</Tag>
            {result.translationMethod === "openai" ? (
              <Tag color="purple">Translated to English</Tag>
            ) : null}
            <Tag color={result.roleMethod === "openai" ? "green" : "default"}>
              {result.roleMethod === "openai" ? "roles by Azure OpenAI" : `roles by ${result.roleMethod}`}
            </Tag>
          </Space>
        </Flex>
        <Flex gap={10} wrap="wrap" style={{ marginTop: 14 }}>
          <Button onClick={() => downloadJson("analysis-result.json", result)}>Download JSON</Button>
          <Button onClick={() => downloadText("analysis-result.txt", buildResultText(result))}>
            Download TXT
          </Button>
          <Button onClick={() => navigator.clipboard.writeText(JSON.stringify(result.extractedAttributes, null, 2))}>
            Copy Attributes
          </Button>
        </Flex>
      </Panel>

      {result.warning ? (
        <Alert
          type="warning"
          showIcon
          message={formatWarningMessage(result.warning)}
          description="The result includes the available extracted data."
        />
      ) : null}

      <ResultCanvas>
        <Panel>
          <Flex justify="space-between" align="center" wrap="wrap" gap={12}>
            <Space direction="vertical" size={0}>
              <Title level={4} style={{ margin: 0 }}>
                Conversation, split by speaker.
              </Title>
              <Text type="secondary">Conversation text is shown in the original transcript language.</Text>
            </Space>
            <Tag color={result.roleMethod === "openai" ? "green" : "default"}>
              {result.roleMethod === "openai" ? "Azure OpenAI" : result.roleMethod}
            </Tag>
          </Flex>
          <div style={{ marginTop: 16 }}>
            <ConversationView turns={result.conversation} />
          </div>
        </Panel>

        <Panel>
          <Title level={4}>Flagged attributes.</Title>
          <FlaggedAttributes attrs={result.extractedAttributes} evidence={result.attributeEvidence ?? []} />
        </Panel>
      </ResultCanvas>

      <Panel>
        <Title level={4}>All Extracted Attributes</Title>
        <AttributesTable attrs={result.extractedAttributes} />
      </Panel>

      {result.rawAzureEntities.length ? (
        <Panel>
          <Title level={4}>Azure Raw Entities</Title>
          <RawEntityTable entities={result.rawAzureEntities} />
        </Panel>
      ) : null}

      <Panel>
        <Title level={4}>Full Response</Title>
        <PreBlock>{JSON.stringify(result, null, 2)}</PreBlock>
      </Panel>
    </Space>
  );
}

function HistoryList({ compact = false }: { compact?: boolean }) {
  const queryClient = useQueryClient();
  const { message } = AntApp.useApp();
  const { data, isLoading } = useQuery({ queryKey: ["history"], queryFn: fetchHistory });
  const deleteMutation = useMutation({
    mutationFn: deleteHistoryItem,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["history"] });
      message.success("Saved analysis deleted");
    },
    onError: (error) => {
      message.error(toApiError(error));
    }
  });

  if (isLoading) {
    return <Spin />;
  }

  if (!data?.length) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No saved analyses" />;
  }

  return (
    <List
      size="small"
      dataSource={data}
      renderItem={(item: AnalysisHistoryItem) => (
        <List.Item
          actions={[
            <Button
              key="delete"
              size="small"
              danger
              loading={deleteMutation.isPending}
              onClick={() => deleteMutation.mutate(item.id)}
            >
              Delete
            </Button>
          ]}
        >
          <List.Item.Meta
            title={<Link to={`/transcription/${item.id}`}>{formatTimestamp(item.createdAtUtc)}</Link>}
            description={
              compact
                ? `${displayLanguage(item.detectedLanguage)} / ${item.translationMethod} / ${item.roleMethod}`
                : `${displayLanguage(item.detectedLanguage)} / ${item.translationMethod} / ${item.roleMethod} / ${item.transcriptLength} chars`
            }
          />
        </List.Item>
      )}
    />
  );
}

function TranscriptionPage() {
  const [savedDraft] = React.useState(() => readSavedDraft());
  const [result, setResult] = React.useState<AnalyzeResponse | null>(() => readSavedResult());
  const [analyzeError, setAnalyzeError] = React.useState<string | null>(null);
  const [lastSyncedAt, setLastSyncedAt] = React.useState<Date | null>(null);
  const [analysisRunId, setAnalysisRunId] = React.useState(0);
  const queryClient = useQueryClient();
  const { message } = AntApp.useApp();
  const navigate = useNavigate();

  const { control, handleSubmit, setValue, formState } = useForm<AnalyzePayload>({
    resolver: yupResolver(schema),
    defaultValues: {
      transcriptText: savedDraft.transcriptText,
      language: savedDraft.language
    }
  });

  const transcriptText = useWatch({ control, name: "transcriptText" });
  const language = useWatch({ control, name: "language" });
  const transcriptWordCount = countWords(transcriptText || "");
  const estimatedChunks = Math.max(0, Math.ceil((transcriptText || "").length / 4000));

  React.useEffect(() => {
    const draft = {
      transcriptText: transcriptText || "",
      language: language || "auto"
    };
    window.localStorage.setItem(DRAFT_KEY, JSON.stringify(draft));
  }, [language, transcriptText]);

  const mutation = useMutation({
    mutationFn: analyzeTranscript,
    onSuccess: async (data) => {
      const normalizedData = normalizeAnalyzeResponse(data);
      setResult(normalizedData);
      setAnalyzeError(null);
      window.localStorage.setItem(LAST_RESULT_KEY, JSON.stringify(normalizedData));
      await queryClient.invalidateQueries({ queryKey: ["history"] });
      await queryClient.refetchQueries({ queryKey: ["history"] });
      setLastSyncedAt(new Date());
      message.success("Analysis complete");
    },
    onError: (error) => {
      const apiError = toApiError(error);
      setResult(null);
      setAnalyzeError(apiError);
      message.error(apiError);
    }
  });

  const loadSample = (sample: string, language: Language) => {
    setValue("transcriptText", sample, { shouldValidate: true, shouldDirty: true });
    setValue("language", language, { shouldValidate: true, shouldDirty: true });
    setResult(null);
    setAnalyzeError(null);
    setLastSyncedAt(null);
  };

  const loadFile = async (file: File) => {
    if (!file.name.toLowerCase().endsWith(".txt")) {
      message.error("Upload a .txt transcript file");
      return Upload.LIST_IGNORE;
    }

    const text = await file.text();
    setValue("transcriptText", text, { shouldValidate: true, shouldDirty: true });
    setResult(null);
    setAnalyzeError(null);
    setLastSyncedAt(null);
    message.success(`Loaded ${file.name}`);
    return Upload.LIST_IGNORE;
  };

  const onSubmit = (values: AnalyzePayload) => {
    setAnalyzeError(null);
    setResult(null);
    setLastSyncedAt(null);
    setAnalysisRunId((current) => current + 1);
    window.localStorage.removeItem(LAST_RESULT_KEY);
    mutation.mutate(values);
  };

  const clearResult = () => {
    setResult(null);
    setAnalyzeError(null);
    setLastSyncedAt(null);
    window.localStorage.removeItem(LAST_RESULT_KEY);
    message.success("Current result cleared");
  };

  return (
    <Workspace>
      <Hero>
        <AppMark>AI</AppMark>
        <Title level={2} style={{ margin: 0 }}>
          New Transcription
        </Title>
        <Text type="secondary">
          Paste a transcript or upload a TXT file to detect language, translate, and extract what matters.
        </Text>
      </Hero>

      <InputCard>
        <form onSubmit={handleSubmit(onSubmit)}>
          <Space direction="vertical" size={14} style={{ width: "100%" }}>
            <Flex justify="space-between" align="center" wrap="wrap" gap={12}>
              <Text strong>Transcript text</Text>
              <Space wrap>
                <Controller
                  name="language"
                  control={control}
                  render={({ field }) => (
                    <Select
                      {...field}
                      style={{ width: 210 }}
                      options={[
                        { value: "auto", label: "Auto-detect language" },
                        { value: "en", label: "Analyze as English" },
                        { value: "hy", label: "Analyze as Armenian" }
                      ]}
                    />
                  )}
                />
                <Upload beforeUpload={loadFile} maxCount={1} accept=".txt,text/plain" showUploadList={false}>
                  <Button>Upload TXT</Button>
                </Upload>
                <Button onClick={() => navigate("/transcription/latest")}>Latest saved</Button>
              </Space>
            </Flex>

            <Controller
              name="transcriptText"
              control={control}
              render={({ field, fieldState }) => (
                <TextArea
                  {...field}
                  rows={4}
                  spellCheck={false}
                  status={fieldState.error ? "error" : undefined}
                  placeholder="Paste transcript text here"
                />
              )}
            />
            <Flex justify="space-between" align="center" wrap="wrap" gap={8}>
              <Text type="secondary">
                {transcriptWordCount.toLocaleString()} words / {estimatedChunks.toLocaleString()} estimated chunks
              </Text>
              {transcriptWordCount >= 20000 ? (
                <Tag color="blue">Large transcript mode</Tag>
              ) : null}
            </Flex>
            {transcriptWordCount >= 20000 ? (
              <Alert
                type="info"
                showIcon
                message="Large transcript processing is enabled"
                description="The backend will process chunks in bounded parallel batches and consolidate attributes at the end. Very large Armenian or mixed English-Armenian transcripts may take longer because translation can be used internally."
              />
            ) : null}
            {formState.errors.transcriptText ? (
              <Text type="danger">{formState.errors.transcriptText.message}</Text>
            ) : null}

            {analyzeError ? <Alert type="error" showIcon message={analyzeError} /> : null}

            <AnalysisProgressPanel
              active={mutation.isPending}
              wordCount={transcriptWordCount}
              estimatedChunks={estimatedChunks}
              language={language || "auto"}
              transcript={transcriptText || ""}
              lastSyncedAt={lastSyncedAt}
              runId={analysisRunId}
            />

            <Flex justify="space-between" align="center" gap={10} wrap="wrap">
              <Space wrap>
                <Button onClick={() => loadSample(ENGLISH_SAMPLE, "en")}>English sample</Button>
                <Button onClick={() => loadSample(ARMENIAN_SAMPLE, "hy")}>Armenian sample</Button>
                {result ? <Button onClick={clearResult}>Clear result</Button> : null}
              </Space>
              <Button
                type="primary"
                htmlType="submit"
                loading={mutation.isPending}
                disabled={!transcriptText?.trim()}
              >
                Analyze
              </Button>
            </Flex>
          </Space>
        </form>
      </InputCard>

      {result ? <ResultPanel result={result} /> : null}

      <RecentPanel>
        <Flex justify="space-between" align="center" wrap="wrap" gap={12}>
          <Space direction="vertical" size={0}>
            <Title level={4} style={{ margin: 0 }}>
              Recent Transcriptions
            </Title>
            <Text type="secondary">Saved analyses from local-results</Text>
          </Space>
          <Tag color="default">All</Tag>
        </Flex>
        <div style={{ marginTop: 12 }}>
          <HistoryList />
        </div>
      </RecentPanel>
    </Workspace>
  );
}

function HistoryDetailPage() {
  const params = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { message } = AntApp.useApp();
  const history = useQuery({
    queryKey: ["history"],
    queryFn: fetchHistory,
    enabled: params.id === "latest"
  });
  const latestId = history.data?.[0]?.id;
  const id = params.id === "latest" ? latestId : params.id;

  React.useEffect(() => {
    if (params.id === "latest" && latestId) {
      navigate(`/transcription/${latestId}`, { replace: true });
    }
  }, [latestId, navigate, params.id]);

  const { data, isLoading, error } = useQuery({
    queryKey: ["history-detail", id],
    queryFn: () => fetchHistoryDetail(id!),
    enabled: Boolean(id) && params.id !== "latest"
  });
  const deleteMutation = useMutation({
    mutationFn: deleteHistoryItem,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["history"] });
      message.success("Saved analysis deleted");
      navigate("/");
    },
    onError: (deleteError) => {
      message.error(toApiError(deleteError));
    }
  });

  if (isLoading || (params.id === "latest" && history.isLoading)) {
    return <Spin />;
  }

  if (error || !data) {
    return (
      <Panel>
        <Alert type="error" showIcon message="Saved analysis not found" description={toApiError(error)} />
      </Panel>
    );
  }

  return (
    <Workspace>
      <Space direction="vertical" size={16} style={{ width: "100%" }}>
      <Flex justify="space-between" align="center" wrap="wrap" gap={12}>
        <Space direction="vertical" size={0}>
          <Title level={3} style={{ margin: 0 }}>
            Analysis Detail
          </Title>
          <Text type="secondary">{data.fileName}</Text>
        </Space>
        <Space wrap>
          <Button onClick={() => downloadText(data.fileName, data.content)}>Download TXT</Button>
          <Button onClick={() => downloadJson(`${data.id}.json`, data)}>Download JSON</Button>
          <Button danger loading={deleteMutation.isPending} onClick={() => deleteMutation.mutate(data.id)}>
            Delete
          </Button>
          <Button onClick={() => navigate("/")}>New Transcription</Button>
        </Space>
      </Flex>

      <Panel>
        <Descriptions bordered size="small" column={{ xs: 1, md: 2 }}>
          <Descriptions.Item label="Created">{formatTimestamp(data.createdAtUtc)}</Descriptions.Item>
          <Descriptions.Item label="Language">{data.language}</Descriptions.Item>
          <Descriptions.Item label="Detected Language">
            {displayLanguage(data.detectedLanguage)}
          </Descriptions.Item>
          <Descriptions.Item label="Translation">{data.translationMethod}</Descriptions.Item>
          <Descriptions.Item label="Role Method">{data.roleMethod}</Descriptions.Item>
          <Descriptions.Item label="Transcript Length">{data.transcriptLength}</Descriptions.Item>
        </Descriptions>
      </Panel>

      <Panel>
        <Title level={4}>Saved TXT Output</Title>
        <PreBlock>{data.content}</PreBlock>
      </Panel>
      </Space>
    </Workspace>
  );
}

function formatTimestamp(value: string) {
  return dayjs(value).format("MMM D, YYYY HH:mm");
}

function useAnalysisProgress(
  active: boolean,
  wordCount: number,
  estimatedChunks: number,
  language: Language,
  transcript: string,
  runId: number
) {
  const [tick, setTick] = React.useState(0);

  React.useEffect(() => {
    if (!active) {
      return;
    }

    const resetTimer = window.setTimeout(() => {
      setTick(0);
    }, 0);
    const timer = window.setInterval(() => {
      setTick((current) => current + 1);
    }, 1400);

    return () => {
      window.clearTimeout(resetTimer);
      window.clearInterval(timer);
    };
  }, [active, runId]);

  const hasArmenian = /[\u0530-\u058f]/.test(transcript);
  const shouldTranslate = hasArmenian;
  const steps = [
    "Queue",
    "Language",
    ...(shouldTranslate ? ["Translation"] : []),
    "Speakers",
    "Attributes",
    "History"
  ];
  const step = Math.min(Math.floor((active ? tick : 0) / 2), steps.length - 1);
  const basePercent = Math.round(((step + 1) / steps.length) * 100);
  const percent = Math.min(95, Math.max(8, basePercent - (step === steps.length - 1 ? 5 : 0)));
  const label = steps[step] ?? "Working";

  return {
    steps,
    step,
    percent,
    label: progressLabel(label),
    description: progressDescription(label, wordCount, estimatedChunks, shouldTranslate)
  };
}

function progressLabel(step: string) {
  return step === "Queue"
    ? "Preparing request"
    : step === "Language"
      ? "Detecting language"
      : step === "Translation"
        ? "Translating to English"
        : step === "Speakers"
          ? "Synchronizing speaker turns"
          : step === "Attributes"
            ? "Extracting attributes and evidence"
            : "Synchronizing local history";
}

function progressDescription(
  step: string,
  wordCount: number,
  estimatedChunks: number,
  shouldTranslate: boolean
) {
  if (step === "Queue") {
    return `${wordCount.toLocaleString()} words are being prepared for analysis.`;
  }

  if (step === "Translation") {
    return "Mixed or Armenian text can be translated internally while visible results stay in the original language.";
  }

  if (step === "Speakers") {
    return "Agent and Caller turns are being aligned before evidence is attached.";
  }

  if (step === "Attributes") {
    return `Processing about ${Math.max(1, estimatedChunks).toLocaleString()} chunks with bounded backend parallelism.`;
  }

  if (step === "History") {
    return "Saving the analysis and refreshing recent transcriptions.";
  }

  return shouldTranslate
    ? "Checking whether the transcript is English, Armenian, or mixed English-Armenian."
    : "Checking the transcript language before extraction.";
}

function readSavedDraft(): AnalyzePayload {
  if (typeof window === "undefined") {
    return { transcriptText: "", language: "auto" };
  }

  try {
    const stored = window.localStorage.getItem(DRAFT_KEY);
    if (!stored) {
      return { transcriptText: "", language: "auto" };
    }

    const parsed = JSON.parse(stored) as Partial<AnalyzePayload>;
    return {
      transcriptText: typeof parsed.transcriptText === "string" ? parsed.transcriptText : "",
      language: parsed.language === "en" || parsed.language === "hy" ? parsed.language : "auto"
    };
  } catch {
    return { transcriptText: "", language: "auto" };
  }
}

function readSavedResult(): AnalyzeResponse | null {
  if (typeof window === "undefined") {
    return null;
  }

  try {
    const stored = window.localStorage.getItem(LAST_RESULT_KEY);
    return stored ? normalizeAnalyzeResponse(JSON.parse(stored) as AnalyzeResponse) : null;
  } catch {
    return null;
  }
}

function normalizeAnalyzeResponse(result: AnalyzeResponse): AnalyzeResponse {
  return {
    ...result,
    conversation: normalizeConversationTurns(result.conversation),
    warning: normalizeResultWarning(result.warning)
  };
}

function normalizeConversationTurns(turns: ConversationTurn[]) {
  const firstSpeakerOne = turns.find((turn) => normalizeRoleName(turn.role) === "speaker 1")?.text ?? "";
  const speakerOneIsCaller = looksLikeCallerTurn(firstSpeakerOne) || !looksLikeAgentTurn(firstSpeakerOne);

  const normalizedTurns = turns.map((turn) => {
    const normalizedRole = normalizeRoleName(turn.role);

    if (normalizedRole === "speaker 1") {
      return { ...turn, role: speakerOneIsCaller ? "Caller" : "Agent" };
    }

    if (normalizedRole === "speaker 2") {
      return { ...turn, role: speakerOneIsCaller ? "Agent" : "Caller" };
    }

    return turn;
  });

  return splitEmbeddedConversationTurns(normalizedTurns);
}

function splitEmbeddedConversationTurns(turns: ConversationTurn[]) {
  return turns.flatMap((turn) => splitEmbeddedConversationTurn(turn));
}

function splitEmbeddedConversationTurn(turn: ConversationTurn): ConversationTurn[] {
  const parts = splitConversationText(turn.text);
  if (parts.length < 2) {
    return [turn];
  }

  let currentRole = turn.role === "Agent" || turn.role === "Caller" ? turn.role : "Caller";
  let previousText = "";
  const splitTurns: ConversationTurn[] = [];

  parts.forEach((part) => {
    const inferredRole = inferEmbeddedRole(part, previousText, currentRole);
    const role = inferredRole ?? currentRole;
    const previous = splitTurns[splitTurns.length - 1];

    if (previous?.role === role && previous.text.length + part.length <= 420) {
      previous.text = `${previous.text} ${part}`;
    } else {
      splitTurns.push({ role, text: part });
    }

    currentRole = role;
    previousText = part;
  });

  const roles = new Set(splitTurns.map((item) => item.role));
  return roles.size > 1 || turn.text.length >= 700 ? splitTurns : [turn];
}

function splitConversationText(text: string) {
  const normalized = text.replace(
    /\s+(?=(այո[,։]|խնդրում եմ|communication preferences|appointment reminders|այսինքն|email-ը|նաեւ portal issue|նաև portal issue|appointment page|որ browser|փորձել եմ|բոլոր տեղերում|և մեկ բան|եւ մեկ բան|դուք ուզում եք|words like|կարո՞ղ եք summarize|appointment-ը|insurance card-ը|billing-ը|address-ը|official documents-ը|ticket-ը|ամեն ինչ|ձեր էլ\.?\s*հասցեն|ձեր հասցեն|շնորհակալություն,|կարո՞ղ եք|կարող եք|շատ լավ|մի պահ սպասեք|լավ,\s*շնորհակալություն|your email address|your address|can you also|could you also|thank you[, ]|one moment|please hold))/gi,
    "\n"
  );

  return normalized
    .split(/\n+/)
    .flatMap(splitSentenceLikeParts)
    .map((part) => part.trim())
    .filter(Boolean);
}

function splitSentenceLikeParts(text: string) {
  const parts: string[] = [];
  let start = 0;

  for (let index = 0; index < text.length; index += 1) {
    const char = text[index];
    const next = text[index + 1] ?? "";
    const candidate = text.slice(start, index + 1);
    const lowerCandidate = candidate.toLocaleLowerCase();
    const isPunctuation = char === "." || char === "!" || char === "?" || char === "։" || char === ":";
    const isKnownAbbreviation = lowerCandidate.endsWith("էլ.") || lowerCandidate.endsWith("բնակ.");

    if (!isPunctuation || isKnownAbbreviation || !/\s/.test(next)) {
      continue;
    }

    parts.push(candidate);
    while (/\s/.test(text[index + 1] ?? "")) {
      index += 1;
    }
    start = index + 1;
  }

  if (start < text.length) {
    parts.push(text.slice(start));
  }

  return parts.length ? parts : [text];
}

function inferEmbeddedRole(text: string, previousText: string, currentRole: string) {
  const trimmed = text.trim();

  if (looksLikeAgentTurn(trimmed)) {
    return "Agent";
  }

  if (looksLikeCallerTurn(trimmed)) {
    return "Caller";
  }

  if (currentRole === "Agent" && endsWithQuestionOrPrompt(previousText)) {
    return "Caller";
  }

  return undefined;
}

function endsWithQuestionOrPrompt(text: string) {
  return /[?՞:]$/.test(text.trim());
}

function normalizeRoleName(role: string) {
  return role.trim().toLowerCase().replace(/\s+/g, " ");
}

function looksLikeCallerTurn(text: string) {
  return /(^|\b)(i'?m calling|i am calling|my name is|i would like|i need|i want|my primary|my phone|my email|my address|hello,\s*i|yes,\s*(the|my|i|please)|sure,?\s*my)|իմ անունը|ուզում եմ|իմ հեռախոսահամար|իհարկե|ես ապրում եմ|իմ էլ|բարեւ|^այո[,։\s]|^խնդրում եմ|^communication preferences|^appointment reminders|^այսինքն|^email-ը|^նաեւ portal issue|^նաև portal issue|^appointment page|^փորձել եմ|^բոլոր տեղերում|^և մեկ բան|^եւ մեկ բան|^words like|^appointment-ը|^insurance card-ը|^billing-ը|^address-ը|^official documents-ը|^ticket-ը|^ամեն ինչ|^[\w.%+-]+@[\w.-]+\.[A-Z]{2,}\b|^[A-Z]{2}\d{7}\b|^լավ,\s*շնորհակալություն/i.test(text);
}

function looksLikeAgentTurn(text: string) {
  return /(how can i help|thank you for calling|could you|can you confirm|do you have|i understand|i'?ll|i will|i can help|շնորհակալություն զանգելու համար|ինչպե՞ս կարող եմ օգնել|ուրախ եմ օգնել|կարո՞ղ եք հաստատել|կարող եք հաստատել|ձեր էլ\.?\s*հասցեն|ձեր հասցեն|շնորհակալություն[։.!]?$|կարո՞ղ եք նաև նշել|կարո՞ղ եք summarize|շատ լավ|մի պահ սպասեք|հիմա կստուգեմ|ես կթարմացնեմ|կթարմացնեմ|ես կավելացնեմ|կավելացնեմ|դուք ուզում եք|որ browser|հասկացա|հասկագա)/i.test(text);
}

function normalizeResultWarning(warning?: string) {
  if (!warning) {
    return undefined;
  }

  const parts = warning
    .split(";")
    .map((part) => part.trim())
    .filter(Boolean)
    .filter((part) => !/speaker split|role detection/i.test(part));

  return parts.length ? parts.join("; ") : undefined;
}

function displayLanguage(language: string) {
  if (language === "en") {
    return "English";
  }

  if (language === "hy") {
    return "Armenian";
  }

  if (language === "mixed-en-hy") {
    return "Mixed English-Armenian";
  }

  return language;
}

function formatConfidence(confidence: number) {
  return `${Math.round(confidence * 100)}%`;
}

function formatWarningMessage(warning: string) {
  if (/role detection|speaker split|expected end of string|empty content|invalid json/i.test(warning)) {
    return "Speaker splitting was partially retried. Some turns may use fallback splitting.";
  }

  if (/translation/i.test(warning)) {
    return "Translation was partially unavailable. The analyzer used the available transcript text.";
  }

  if (/azure ai language/i.test(warning)) {
    return "Azure entity extraction was partially unavailable. Other extraction methods were used.";
  }

  if (/openai extraction|attribute/i.test(warning)) {
    return "Attribute extraction was partially retried. The result includes the available attributes.";
  }

  return warning;
}

function countWords(text: string) {
  return text.trim() ? text.trim().split(/\s+/).length : 0;
}

function buildResultText(result: AnalyzeResponse) {
  const lines = [
    "Azure AI Transcript Analyzer - Analysis Result",
    "============================================",
    `Detected Language: ${displayLanguage(result.detectedLanguage)}`,
    `Translation Method: ${result.translationMethod}`,
    `Role Method: ${result.roleMethod}`,
    "",
    "Extracted Attributes",
    "--------------------",
    JSON.stringify(result.extractedAttributes, null, 2),
    "",
    "Attribute Evidence",
    "------------------",
    ...(result.attributeEvidence?.length
      ? result.attributeEvidence.map(
          (item) =>
            `- ${item.label}: ${item.value}\n  Confidence: ${formatConfidence(item.confidence)}\n  Source: ${item.source}\n  Snippet: ${item.snippet || "(not found)"}`
        )
      : ["(none)"]),
    "",
    "Conversation",
    "------------",
    ...result.conversation.map((turn, index) => `${index + 1}. ${turn.role}: ${turn.text}`)
  ];

  if (result.translatedTranscript) {
    lines.push("", "Translated Transcript", "---------------------", result.translatedTranscript);
  }

  if (result.warning) {
    lines.push("", "Warnings", "--------", result.warning);
  }

  return lines.join("\n");
}

function downloadJson(fileName: string, value: unknown) {
  downloadText(fileName, JSON.stringify(value, null, 2), "application/json");
}

function downloadText(fileName: string, content: string, type = "text/plain") {
  const blob = new Blob([content], { type });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

function getInitialThemeMode(): ThemeMode {
  if (typeof window === "undefined") {
    return "light";
  }

  const stored = window.localStorage.getItem("themeMode");
  return stored === "dark" || stored === "light" ? stored : "light";
}

function getThemeTokens(mode: ThemeMode) {
  const isDark = mode === "dark";

  return {
    algorithm: isDark ? theme.darkAlgorithm : theme.defaultAlgorithm,
    token: {
      colorPrimary: "#6c63ff",
      colorBgBase: isDark ? "#10131a" : "#f6f7fb",
      colorBgContainer: isDark ? "#171b24" : "#ffffff",
      colorBorder: isDark ? "#2a3040" : "#e8ebf3",
      colorText: isDark ? "#eef2ff" : "#1f2430",
      colorTextSecondary: isDark ? "#a5adbd" : "#6b7280",
      borderRadius: 8,
      fontFamily: "Inter, system-ui, -apple-system, BlinkMacSystemFont, sans-serif"
    }
  };
}

function applyThemeVariables(mode: ThemeMode) {
  const isDark = mode === "dark";
  const root = document.documentElement;

  root.dataset.theme = mode;
  root.style.setProperty("--app-bg", isDark ? "#10131a" : "#f5f6fb");
  root.style.setProperty("--panel-bg", isDark ? "#171b24" : "#ffffff");
  root.style.setProperty("--panel-border", isDark ? "#2a3040" : "#edf0f7");
  root.style.setProperty("--panel-shadow", isDark ? "0 12px 40px rgba(0, 0, 0, 0.22)" : "0 12px 40px rgba(31, 36, 48, 0.06)");
  root.style.setProperty("--muted-bg", isDark ? "#10131a" : "#f8f9fd");
  root.style.setProperty("--text-main", isDark ? "#eef2ff" : "#1f2430");
  root.style.setProperty("--accent-soft", isDark ? "rgba(108, 99, 255, 0.18)" : "#eef4ff");
  root.style.setProperty("--accent-soft-border", isDark ? "rgba(108, 99, 255, 0.35)" : "#dbeafe");
}

export default function App() {
  const [themeMode, setThemeMode] = React.useState<ThemeMode>(getInitialThemeMode);

  React.useEffect(() => {
    applyThemeVariables(themeMode);
    window.localStorage.setItem("themeMode", themeMode);
  }, [themeMode]);

  const toggleTheme = () => {
    setThemeMode((current) => (current === "light" ? "dark" : "light"));
  };

  return (
    <ConfigProvider theme={getThemeTokens(themeMode)}>
      <AntApp>
        <PageShell>
          <TopBar>
            <Flex justify="space-between" align="center" style={{ height: "100%" }}>
              <Text style={{ color: "#ffffff", fontSize: 18 }}>New Transcription</Text>
              <Space wrap>
                <HealthStatus />
                <ThemeToggle type="button" onClick={toggleTheme}>
                  {themeMode === "light" ? "Dark" : "Light"}
                </ThemeToggle>
              </Space>
            </Flex>
          </TopBar>
          <Layout>
            <WorkArea>
              <Routes>
                <Route path="/" element={<TranscriptionPage />} />
                <Route path="/transcription/:id" element={<HistoryDetailPage />} />
              </Routes>
            </WorkArea>
          </Layout>
        </PageShell>
      </AntApp>
    </ConfigProvider>
  );
}
