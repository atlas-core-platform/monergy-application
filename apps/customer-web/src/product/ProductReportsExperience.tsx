import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Collapse, Descriptions, Skeleton, Tag } from 'antd';
import { useCallback, useEffect, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import type { TrustedFinancialReport } from '../reports/types';
import { generateBasicFinancialReport, getBasicFinancialReport } from './productApi';
import './product.css';

const reportFromLocation = () => new URLSearchParams(window.location.search).get('report') ?? '';

const formatDateTime = (value: string) =>
  new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value));

const formatValue = (value: number, unit: string) =>
  /^[A-Z]{3}$/.test(unit)
    ? new Intl.NumberFormat(undefined, { style: 'currency', currency: unit }).format(value)
    : `${String(value)} ${unit}`;

function download(report: TrustedFinancialReport) {
  const blob = new Blob([report.export.content], { type: report.export.mediaType });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = report.export.fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}

export default function ProductReportsExperience() {
  const { api, authorization } = useWorkspaceSession();
  const product = authorization?.area === 'product' ? authorization.context : null;
  const capabilities = new Set(product?.capabilities ?? []);
  const canGenerate = capabilities.has('reporting.report.generate');
  const canRead = capabilities.has('reporting.report.read');
  const [report, setReport] = useState<TrustedFinancialReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [requestedReportId] = useState(reportFromLocation);

  const message = useCallback((failure: unknown) => {
    if (failure instanceof AccessApiError) {
      if (failure.status === 403)
        return "Basic reports aren't included in your current access.";
      if (failure.status === 404)
        return "This report isn't available in your current financial workspace.";
      if (failure.status === 503)
        return "We couldn't reach the reporting service. Your financial information has not changed.";
    }
    return "We couldn't load the basic financial summary. Try again.";
  }, []);

  useEffect(() => {
    if (!api || !canRead || !requestedReportId) return;
    const controller = new AbortController();
    void Promise.resolve().then(() => {
      if (controller.signal.aborted) return null;
      setBusy(true);
      setError('');
      return getBasicFinancialReport(api, requestedReportId, controller.signal);
    }).then((result) => {
      if (!controller.signal.aborted && result !== null) setReport(result);
    }).catch((failure: unknown) => {
      if (!controller.signal.aborted) setError(message(failure));
    }).finally(() => {
      if (!controller.signal.aborted) setBusy(false);
    });
    return () => { controller.abort(); };
  }, [api, canRead, message, requestedReportId]);

  const generate = async () => {
    if (!api || !canGenerate || busy) return;
    setBusy(true);
    setError('');
    try {
      const generated = await generateBasicFinancialReport(
        api,
        globalThis.crypto.randomUUID().replaceAll('-', ''),
      );
      setReport(generated);
      window.history.replaceState(
        null,
        '',
        '/reports?report=' + encodeURIComponent(generated.reportId),
      );
    } catch (failure) {
      setReport(null);
      setError(message(failure));
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="product-page product-report-page">
      <header className="product-heading">
        <div>
          <p className="workspace-eyebrow">BASIC FINANCIAL SUMMARY</p>
          <h1>Bring your trusted financial information into one exportable view.</h1>
          <p>
            This summary contains the currently supported financial information already in your
            Financial Profile and keeps each item connected to its source. It is not a formal report
            pack, financial-health score, recommendation, or advice. Included Loan source amounts do not establish verified outstanding balances, EMI, or eligibility. Asset source figures are document-reported values, not independently verified valuations or net-worth totals. Investment source figures are document-stated amounts, not live securities holdings, current NAV, market prices or portfolio returns. Insurance source figures are not verified policy benefits, claims, premiums or cash values. Tax source figures are document-stated amounts, not assessed tax owed, accepted filings or refunds.
          </p>
        </div>
        <Tag className="product-environment">
          {(product?.environment ?? 'Test environment').toUpperCase()} ·{' '}
          {(product?.dataDurability ?? 'Temporary data').toUpperCase()}
        </Tag>
      </header>

      {!canGenerate && (
        <Alert
          showIcon
          type="warning"
          title="Basic reports aren't included in your current access"
          description="Your organization can review the recommended Customer access update before enabling this journey."
        />
      )}
      {error && <Alert showIcon type="error" title={error} />}

      {busy && !report ? (
        <section className="product-panel">
          <Skeleton active paragraph={{ rows: 5 }} />
        </section>
      ) : report ? (
        <>
          <section className="product-report-summary" aria-label="Basic financial summary">
            <div className="product-panel product-summary-card">
              <span className="product-icon">
                <WorkspaceIcon name="reports" size={20} />
              </span>
              <div>
                <span>Trusted items</span>
                <strong>{report.items.length}</strong>
              </div>
            </div>
            <div className="product-panel product-summary-card">
              <span className="product-icon">
                <WorkspaceIcon name="evidence" size={20} />
              </span>
              <div>
                <span>Source records</span>
                <strong>{report.financialProvenanceReferences.length}</strong>
              </div>
            </div>
            <div className="product-panel product-summary-card">
              <span className="product-icon">
                <WorkspaceIcon name="check" size={20} />
              </span>
              <div>
                <span>Generated</span>
                <strong>{formatDateTime(report.generatedAt)}</strong>
              </div>
            </div>
          </section>

          <section className="product-panel product-report-content">
            <div className="product-section-heading">
              <div>
                <p className="workspace-eyebrow">CURRENT TRUSTED INFORMATION</p>
                <h2>Included in this summary</h2>
              </div>
              <Button
                onClick={() => {
                  download(report);
                }}
              >
                Export basic summary
              </Button>
            </div>
            <div className="product-report-items">
              {report.items.map((item) => (
                <article className="product-report-item" key={item.source.sourceId}>
                  <div>
                    <Tag>{item.source.sourceType === 'FinancialFact' ? 'FINANCIAL INFORMATION' : item.source.sourceType}</Tag>
                    <h3>{item.label}</h3>
                    <strong>{formatValue(item.value, item.unit)}</strong>
                  </div>
                  <div className="product-report-item-actions">
                    {item.source.sourceType === 'FinancialFact' && (
                      <Button
                        type="link"
                        href={'/finances?fact=' + encodeURIComponent(item.source.sourceId)}
                      >
                        View source &amp; history
                      </Button>
                    )}
                  </div>
                  <Collapse
                    ghost
                    items={[
                      {
                        key: 'details',
                        label: 'Technical details',
                        children: (
                          <Descriptions column={1} size="small">
                            <Descriptions.Item label="Financial fact">
                              {item.source.sourceId}
                            </Descriptions.Item>
                            <Descriptions.Item label="Financial provenance">
                              {item.source.financialProvenanceReferenceId ?? 'Not applicable'}
                            </Descriptions.Item>
                            <Descriptions.Item label="Evidence">
                              {item.source.evidenceReferenceId ?? 'Not applicable'}
                            </Descriptions.Item>
                          </Descriptions>
                        ),
                      },
                    ]}
                  />
                </article>
              ))}
            </div>
          </section>

          <section className="product-panel product-report-boundary">
            <WorkspaceIcon name="shield" size={20} />
            <div>
              <p className="workspace-eyebrow">REPORT BOUNDARY</p>
              <h2>Source-connected summary, not a financial conclusion.</h2>
              <p>
                Values are copied from your current authoritative Financial Profile without new
                aggregation, scoring, forecasting, or AI interpretation. The exported file contains
                the same governed report content shown here.
              </p>
            </div>
          </section>
        </>
      ) : canGenerate ? (
        <section className="product-panel product-empty-financial">
          <span className="product-empty-icon">
            <WorkspaceIcon name="reports" size={30} />
          </span>
          <p className="workspace-eyebrow">READY WHEN YOU ARE</p>
          <h2>Create a basic summary from your trusted financial information.</h2>
          <p>
            Monergy will ask the Reporting owner to read your current authorized Financial Profile.
            Nothing in your financial information is changed by generating or exporting this summary.
          </p>
          <Button type="primary" loading={busy} onClick={() => void generate()}>
            Generate basic summary
          </Button>
        </section>
      ) : null}

      {report && (
        <div className="product-report-refresh">
          <Button loading={busy} disabled={!canGenerate} onClick={() => void generate()}>
            Refresh basic summary
          </Button>
          <a href="/finances">View financial information</a>
        </div>
      )}
    </main>
  );
}
