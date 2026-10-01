import {
  Alert,
  Button,
  Card,
  Collapse,
  Descriptions,
  Empty,
  Space,
  Statistic,
  Tag,
  Typography,
} from 'antd';
import { useCallback, useEffect, useState } from 'react';
import {
  downloadReferenceExport,
  generateReferenceReport,
  getReferenceReport,
  getReportingDispatchStatus,
  ReferenceReportingError,
  reportingExperienceMode,
} from './referenceReportsApi';
import type { ReportingDispatchStatus } from './referenceReportsApi';
import type { TrustedFinancialReport } from './types';

const { Paragraph, Text, Title } = Typography;
const lastPersistedReportKey = 'monergy.d11.lastReportId';

interface ReportsExperienceApi {
  generate: typeof generateReferenceReport;
  get: typeof getReferenceReport;
  status: typeof getReportingDispatchStatus;
}

interface ReportsExperienceProps {
  experienceMode?: typeof reportingExperienceMode;
  api?: ReportsExperienceApi;
}

const defaultApi: ReportsExperienceApi = {
  generate: generateReferenceReport,
  get: getReferenceReport,
  status: getReportingDispatchStatus,
};

function formatValue(value: number, unit: string) {
  if (unit === 'INR')
    return new Intl.NumberFormat('en-IN', {
      style: 'currency',
      currency: 'INR',
      maximumFractionDigits: 0,
    }).format(value);
  if (unit === 'RATIO') return `${String(Math.round(value * 100))}%`;
  return `${String(value)} ${unit}`;
}

export default function ReportsExperience({
  experienceMode = reportingExperienceMode,
  api = defaultApi,
}: ReportsExperienceProps = {}) {
  const [report, setReport] = useState<TrustedFinancialReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [dispatch, setDispatch] = useState<ReportingDispatchStatus | null>(null);

  const refreshDispatch = useCallback(
    async (signal?: AbortSignal) => {
      if (experienceMode !== 'PERSISTED_REPORTING') return;
      try {
        setDispatch(await api.status(signal));
      } catch {
        if (!signal?.aborted) {
          setDispatch({
            state: 'UNKNOWN',
            pendingEvents: -1,
            lastFailureCode: 'audit.status.unknown',
          });
        }
      }
    },
    [api, experienceMode],
  );

  const reopen = useCallback(async () => {
    const reportId = globalThis.localStorage.getItem(lastPersistedReportKey);
    if (!reportId) {
      setFailure(
        'report.local.previous-unavailable: No persisted report has been generated in this browser.',
      );
      return;
    }
    setLoading(true);
    setFailure(null);
    try {
      const recovered = await api.get(reportId);
      setReport(recovered);
      void refreshDispatch();
    } catch (error) {
      setReport(null);
      setFailure(
        error instanceof ReferenceReportingError
          ? `${error.failure.code}: ${error.message}`
          : 'Persisted report retrieval failed safely.',
      );
    } finally {
      setLoading(false);
    }
  }, [api, refreshDispatch]);

  useEffect(() => {
    if (
      experienceMode === 'PERSISTED_REPORTING' &&
      globalThis.localStorage.getItem(lastPersistedReportKey)
    ) {
      const timer = globalThis.setTimeout(() => {
        void reopen();
      }, 0);
      return () => {
        globalThis.clearTimeout(timer);
      };
    }
    return undefined;
  }, [experienceMode, reopen]);

  useEffect(() => {
    if (
      experienceMode !== 'PERSISTED_REPORTING' ||
      dispatch === null ||
      dispatch.state === 'DELIVERED'
    ) {
      return undefined;
    }
    const controller = new AbortController();
    const timer = globalThis.setTimeout(() => {
      void refreshDispatch(controller.signal);
    }, 500);
    return () => {
      controller.abort();
      globalThis.clearTimeout(timer);
    };
  }, [dispatch, experienceMode, refreshDispatch]);

  async function generate() {
    setLoading(true);
    setFailure(null);
    try {
      const generated = await api.generate();
      setReport(generated);
      if (experienceMode === 'PERSISTED_REPORTING') {
        globalThis.localStorage.setItem(lastPersistedReportKey, generated.reportId);
        void refreshDispatch();
      }
    } catch (error) {
      setReport(null);
      setFailure(
        error instanceof ReferenceReportingError
          ? `${error.failure.code}: ${error.message}`
          : 'Report generation failed safely.',
      );
    } finally {
      setLoading(false);
    }
  }

  return (
    <main className="min-h-screen bg-canvas px-4 py-6 font-sans text-ink sm:px-8 lg:px-12">
      <div className="mx-auto max-w-6xl space-y-6">
        <header className="rounded-panel bg-surface p-6 shadow-panel sm:p-8">
          <Space wrap>
            <Tag color="blue">
              {experienceMode === 'PERSISTED_REPORTING'
                ? 'SERVICE-OWNED SOURCES · LOCAL ONLY'
                : 'REFERENCE · LOCAL / CI ONLY'}
            </Tag>
            <Tag color="purple">
              {experienceMode === 'PERSISTED_REPORTING'
                ? 'MWP-03-D11 · SYNTHETIC'
                : 'MWP-03-D08 · SIMULATOR'}
            </Tag>
          </Space>
          <Title level={1} className="mt-4">
            Trusted financial report
          </Title>
          <Paragraph className="max-w-3xl text-base text-muted">
            Generate a basic authorized report, inspect its authoritative source references, and
            export the same deterministic content.{' '}
            {experienceMode === 'PERSISTED_REPORTING'
              ? 'This LOCAL profile reads persisted service-owned synthetic facts and engineering calculation results.'
              : 'This reference capability uses bounded simulator sources.'}{' '}
            It is not a formal report pack or financial advice.
          </Paragraph>
          <Space wrap>
            <Button type="primary" size="large" loading={loading} onClick={() => void generate()}>
              {report ? 'Refresh basic report' : 'Generate basic report'}
            </Button>
            {experienceMode === 'PERSISTED_REPORTING' && (
              <Button size="large" disabled={loading} onClick={() => void reopen()}>
                Reopen persisted report
              </Button>
            )}
            <Button href="/">Back to foundation</Button>
          </Space>
        </header>

        <section
          aria-live="polite"
          aria-busy={loading}
          aria-label="Trusted financial report result"
        >
          {loading && (
            <Card>
              <Text>Generating the authorized reference report…</Text>
            </Card>
          )}
          {failure && (
            <Alert type="error" showIcon title="Report failed safely" description={failure} />
          )}
          {!loading && !failure && !report && (
            <Card>
              <Empty description="Generate a basic report to inspect authorized financial references." />
            </Card>
          )}
          {report && !loading && (
            <Space orientation="vertical" size="large" className="w-full">
              <Card title="Financial snapshot" extra={<Tag color="success">{report.state}</Tag>}>
                <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                  {report.items.map((item) => (
                    <Statistic
                      key={item.source.sourceId}
                      title={item.label}
                      value={formatValue(item.value, item.unit)}
                    />
                  ))}
                </div>
              </Card>

              <Card title="Source drill-down">
                <Collapse
                  items={report.items.map((item) => ({
                    key: item.source.sourceId,
                    label: `${item.label} · ${item.source.authoritativeOwner}`,
                    children: (
                      <Descriptions bordered size="small" column={{ xs: 1, md: 2 }}>
                        <Descriptions.Item label="Source">{item.source.sourceId}</Descriptions.Item>
                        <Descriptions.Item label="Authority">
                          {item.source.authoritativeOwner}
                        </Descriptions.Item>
                        <Descriptions.Item label="Evidence">
                          {item.source.evidenceReferenceId ?? 'Not applicable'}
                        </Descriptions.Item>
                        <Descriptions.Item label="Financial provenance">
                          {item.source.financialProvenanceReferenceId ?? 'Not applicable'}
                        </Descriptions.Item>
                        <Descriptions.Item label="Calculation lineage">
                          {item.source.calculationLineageReferenceId ?? 'Not applicable'}
                        </Descriptions.Item>
                      </Descriptions>
                    ),
                  }))}
                />
              </Card>

              <Card title="Report provenance & audit">
                <Descriptions bordered size="small" column={{ xs: 1, md: 2 }}>
                  <Descriptions.Item label="Report ID">{report.reportId}</Descriptions.Item>
                  <Descriptions.Item label="Generated">
                    {new Date(report.generatedAt).toLocaleString()}
                  </Descriptions.Item>
                  <Descriptions.Item label="Audit compatibility">
                    {report.auditCompatibilityReferenceId}
                  </Descriptions.Item>
                  <Descriptions.Item label="Export SHA-256">
                    {report.export.sha256}
                  </Descriptions.Item>
                  <Descriptions.Item label="AI trace">
                    {report.aiResponseTraceReference ?? 'Not applicable — no AI execution'}
                  </Descriptions.Item>
                </Descriptions>
                {experienceMode === 'PERSISTED_REPORTING' && dispatch && (
                  <Alert
                    className="mt-4"
                    type={dispatch.state === 'DELIVERED' ? 'success' : 'warning'}
                    showIcon
                    title={`Audit delivery: ${dispatch.state}`}
                    description={
                      dispatch.state === 'DELIVERED'
                        ? 'The Reporting outbox has no pending CID-053 event.'
                        : `${String(dispatch.pendingEvents)} event(s) remain pending. ${dispatch.lastFailureCode ?? ''}`
                    }
                  />
                )}
                <Button
                  className="mt-4"
                  onClick={() => {
                    downloadReferenceExport(report);
                  }}
                >
                  Export basic report
                </Button>
              </Card>
            </Space>
          )}
        </section>
      </div>
    </main>
  );
}
