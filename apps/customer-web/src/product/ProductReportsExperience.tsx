import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Collapse, Descriptions, Skeleton, Tag } from 'antd';
import { useCallback, useEffect, useRef, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { RequestAccessAction } from '../access/RequestAccess';
import { useWorkspaceSession } from '../access/workspaceSession';
import type { ReportGenerationJob, TrustedFinancialReport } from '../reports/types';
import type { FinancialReviewReceiptSelection } from '../reports/types';
import { getAssetAllocations, validAssetAllocation, validNetWorthPage } from './netWorthApi';
import {
  getMonthlyDebtServiceCoverages,
  getMonthlyEmiShares,
  getMonthlyNetCashflowRates,
  validMonthlyCashflowPage,
  validMonthlyDebtServiceCoverage,
  validMonthlyEmiShare,
  validMonthlyNetCashflowRate,
} from './monthlyCashflowApi';
import {
  cancelBasicFinancialReportJob,
  getBasicFinancialReport,
  retryBasicFinancialReportJob,
  scheduleBasicFinancialReport,
  waitForBasicFinancialReport,
} from './productApi';
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
    : `${new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)}${unit === '%' ? '%' : ` ${unit}`}`;

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
  const canCalculate = capabilities.has('financial-rules.calculation.execute');
  const [report, setReport] = useState<TrustedFinancialReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [cancelBusy, setCancelBusy] = useState(false);
  const [cancelError, setCancelError] = useState('');
  const [retryBusy, setRetryBusy] = useState(false);
  const [retryError, setRetryError] = useState('');
  const [job, setJob] = useState<ReportGenerationJob | null>(null);
  const [error, setError] = useState('');
  const [requestedReportId] = useState(reportFromLocation);
  const [reviewSelection, setReviewSelection] = useState<FinancialReviewReceiptSelection | null>(null);
  const [reviewChecked, setReviewChecked] = useState(false);
  const generation = useRef<AbortController | null>(null);
  const cancellationOperation = useRef('');
  const retryOperation = useRef('');

  useEffect(() => () => { generation.current?.abort(); }, []);

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

  useEffect(() => {
    if (!api || !canGenerate || !canCalculate || !product?.customerId) {
      return;
    }
    const controller = new AbortController();
    void Promise.all([
      getAssetAllocations(api, undefined, controller.signal),
      getMonthlyNetCashflowRates(api, controller.signal),
      getMonthlyEmiShares(api, controller.signal),
      getMonthlyDebtServiceCoverages(api, controller.signal),
    ]).then(([allocations, rates, shares, coverages]) => {
      if (!validNetWorthPage(allocations, item => validAssetAllocation(item, product.customerId), item => item.calculationId) ||
          !validMonthlyCashflowPage(rates, item => validMonthlyNetCashflowRate(item, product.customerId), item => item.calculationId) ||
          !validMonthlyCashflowPage(shares, item => validMonthlyEmiShare(item, product.customerId), item => item.calculationId) ||
          !validMonthlyCashflowPage(coverages, item => validMonthlyDebtServiceCoverage(item, product.customerId), item => item.calculationId))
        throw new Error('Invalid calculation history');
      let selection: FinancialReviewReceiptSelection | null = null;
      for (const coverage of coverages.items.filter(item => item.state === 'CALCULATED')) {
        const share = shares.items.find(item =>
          item.calculationId === coverage.sourceMonthlyEmiShare.calculationId &&
          JSON.stringify(item) === JSON.stringify(coverage.sourceMonthlyEmiShare));
        const rate = rates.items.find(item => item.state === 'CALCULATED' &&
          item.sourceMonthlyCashflow.calculationId === coverage.sourceMonthlyCashflow.calculationId);
        const allocation = allocations.items.find(item => item.state === 'CALCULATED' &&
          item.sourceNetWorth.position.position.asOfDate === coverage.sourceMonthlyCashflow.position.position.asOfDate &&
          item.sourceNetWorth.position.position.currency === coverage.sourceMonthlyCashflow.position.position.currency);
        if (share && rate && allocation) {
          selection = {
            assetAllocationCalculationId: allocation.calculationId,
            monthlyNetCashflowRateCalculationId: rate.calculationId,
            monthlyEmiShareCalculationId: share.calculationId,
            monthlyDebtServiceCoverageCalculationId: coverage.calculationId,
          };
          break;
        }
      }
      if (!controller.signal.aborted && selection) {
        setReviewSelection({
          ...selection,
        });
      }
    }).catch(() => undefined).finally(() => {
      if (!controller.signal.aborted) setReviewChecked(true);
    });
    return () => { controller.abort(); };
  }, [api, canCalculate, canGenerate, product?.customerId]);

  const generate = async (includeFinancialReview = false) => {
    if (!api || !canGenerate || busy) return;
    generation.current?.abort();
    const controller = new AbortController();
    generation.current = controller;
    setBusy(true);
    setError('');
    setCancelError('');
    setRetryError('');
    cancellationOperation.current = '';
    retryOperation.current = '';
    setJob(null);
    try {
      const scheduled = await scheduleBasicFinancialReport(
        api,
        globalThis.crypto.randomUUID().replaceAll('-', ''),
        includeFinancialReview ? reviewSelection ?? undefined : undefined,
        controller.signal,
      );
      setJob(scheduled);
      const generated = await waitForBasicFinancialReport(
        api, scheduled, setJob, controller.signal);
      setReport(generated);
      window.history.replaceState(
        null,
        '',
        '/reports?report=' + encodeURIComponent(generated.reportId),
      );
    } catch (failure) {
      if (controller.signal.aborted) return;
      setReport(null);
      setError(message(failure));
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  };

  const cancelGeneration = async () => {
    if (!api || !job || cancelBusy || !['Scheduled', 'Running'].includes(job.state)) return;
    const operationId = cancellationOperation.current ||
      globalThis.crypto.randomUUID().replaceAll('-', '');
    cancellationOperation.current = operationId;
    setCancelBusy(true);
    setCancelError('');
    try {
      const cancelled = await cancelBasicFinancialReportJob(api, job.jobId, operationId);
      setJob(cancelled);
      if (cancelled.state === 'Cancelled') {
        generation.current?.abort();
        setBusy(false);
      }
    } catch (failure) {
      if (failure instanceof AccessApiError) {
        setCancelError(failure.status === 403
          ? "You no longer have access to cancel this report request."
          : failure.status === 409
            ? "This report request can no longer be cancelled."
            : "We couldn't cancel this report request. Its current status has not changed.");
      } else {
        setCancelError("We couldn't cancel this report request. Its current status has not changed.");
      }
    } finally {
      setCancelBusy(false);
    }
  };

  const retryGeneration = async () => {
    if (!api || !job || retryBusy || job.state !== 'Failed' || !job.retryable) return;
    generation.current?.abort();
    const controller = new AbortController();
    generation.current = controller;
    const operationId = retryOperation.current ||
      globalThis.crypto.randomUUID().replaceAll('-', '');
    retryOperation.current = operationId;
    setRetryBusy(true);
    setBusy(true);
    setError('');
    setRetryError('');
    let latest: ReportGenerationJob | null = null;
    try {
      const scheduled = await retryBasicFinancialReportJob(
        api, job.jobId, operationId, controller.signal);
      latest = scheduled;
      setJob(scheduled);
      const generated = await waitForBasicFinancialReport(
        api, scheduled, value => { latest = value; setJob(value); }, controller.signal);
      setReport(generated);
      window.history.replaceState(
        null,
        '',
        '/reports?report=' + encodeURIComponent(generated.reportId),
      );
    } catch (failure) {
      if (controller.signal.aborted) return;
      if (latest?.state === 'Failed') {
        retryOperation.current = '';
        setError(message(failure));
        return;
      }
      if (failure instanceof AccessApiError) {
        setRetryError(failure.status === 403
          ? "You no longer have access to retry this report request."
          : failure.status === 409
            ? "This report request is no longer eligible for retry. Refresh before starting a new summary."
            : "We couldn't confirm the retry. Use Retry report request again to safely replay the same operation.");
      } else {
        setRetryError("We couldn't confirm the retry. Use Retry report request again to safely replay the same operation.");
      }
    } finally {
      if (!controller.signal.aborted) {
        setRetryBusy(false);
        setBusy(false);
      }
    }
  };

  return (
    <main className="product-page product-report-page">
      <header className="product-heading">
        <div>
          <p className="workspace-eyebrow">BASIC FINANCIAL SUMMARY</p>
          <h1>Bring your trusted financial information into one exportable view.</h1>
          <p>
            This summary contains supported financial information already in your Financial Profile
            and can include one exact, coherent set of your completed Phase 2 calculation receipts.
            Every item stays connected to its authoritative source. It is not a formal report
            pack, financial-health score, recommendation, or advice. Included Loan source amounts do not establish verified outstanding balances, EMI, or eligibility. Asset source figures are document-reported values, not independently verified valuations or net-worth totals. Investment source figures are document-stated amounts, not live securities holdings, current NAV, market prices or portfolio returns. Insurance source figures are not verified policy benefits, claims, premiums or cash values. Tax source figures are document-stated amounts, not assessed tax owed, accepted filings or refunds.
          </p>
        </div>
        <Tag className="product-environment">
          {(product?.environment ?? 'Test environment').toUpperCase()} ·{' '}
          {(product?.dataDurability ?? 'Temporary data').toUpperCase()}
        </Tag>
      </header>

      {!canGenerate && (
        <RequestAccessAction
          target="customer.reports"
          requiredCapabilities={['reporting.report.generate']}
          title="Basic reports aren't included in your current access"
          description="Your organization can review the recommended Customer access update before enabling this journey."
        />
      )}
      {error && <Alert showIcon type="error" title={error} />}
      {cancelError && <Alert showIcon type="error" title={cancelError} />}
      {retryError && <Alert showIcon type="error" title={retryError} />}
      {job && !report && (!error || job.state === 'Failed') && <Alert showIcon
        type={job.state === 'Failed' ? 'error' : 'info'}
        title={job.state === 'Running' ? 'Your financial summary is being prepared.' :
          job.state === 'CancellationRequested' ? 'Cancellation requested.' :
            job.state === 'Cancelled' ? 'Your financial summary request was cancelled.' :
              job.state === 'Failed' ? 'Your financial summary could not be completed.' :
              'Your financial summary is scheduled.'}
        description={<div>
          <p>Background status: {job.state}. {job.state === 'CancellationRequested'
            ? 'The current attempt may still finish; Monergy will keep the final outcome visible.'
            : job.state === 'Cancelled'
              ? 'No report was generated by this request.'
              : job.state === 'Failed'
                ? job.retryable
                  ? 'The original report intent is unchanged. You can retry it after current access is checked again.'
                  : 'This request cannot be retried safely. Start a new summary only after reviewing the failure.'
              : 'You can keep this page open while Reporting completes the governed request.'}</p>
          {canGenerate && ['Scheduled', 'Running'].includes(job.state) &&
            <Button danger loading={cancelBusy} disabled={cancelBusy} onClick={() => void cancelGeneration()}>
              Cancel report request
            </Button>}
          {canGenerate && job.state === 'Failed' && job.retryable &&
            <Button loading={retryBusy} disabled={retryBusy} onClick={() => void retryGeneration()}>
              Retry report request
            </Button>}
        </div>} />}

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
                    {item.source.sourceType === 'FinancialFact' ? (
                      <Button
                        type="link"
                        href={'/finances?fact=' + encodeURIComponent(item.source.sourceId)}
                      >
                        View source &amp; history
                      </Button>
                    ) : (
                      <Button type="link" href="/finances">
                        View reviewed calculations
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
                            {item.source.sourceType === 'FinancialCalculation' && <>
                              <Descriptions.Item label="Rule">{item.source.ruleId} · {item.source.ruleVersion}</Descriptions.Item>
                              <Descriptions.Item label="Definition">{item.source.definitionHash}</Descriptions.Item>
                              <Descriptions.Item label="Source calculations">{item.source.sourceCalculationIds?.join(', ')}</Descriptions.Item>
                            </>}
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
                Values are copied from your current authoritative Financial Profile and, when you
                select the Phase 2 review, from four exact Financial Rules receipts. Reporting does
                not recalculate, score, forecast, or invoke AI. The exported file contains the same
                governed content and exact receipt provenance shown here.
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
          <Button type="primary" loading={busy} onClick={() => void generate(false)}>
            Generate basic summary
          </Button>
          {reviewSelection && <Button loading={busy} onClick={() => void generate(true)}>
            Generate Phase 2 financial review
          </Button>}
          {canCalculate && reviewChecked && !reviewSelection && <Alert showIcon type="info"
            title="Complete one matching set of reviewed calculations to add the Phase 2 financial review."
            description="Asset allocation, monthly net cash-flow rate, monthly EMI share and monthly debt-service coverage must use one review date and currency." />}
        </section>
      ) : null}

      {report && (
        <div className="product-report-refresh">
          <Button loading={busy} disabled={!canGenerate} onClick={() => void generate(false)}>
            Refresh basic summary
          </Button>
          {reviewSelection && <Button loading={busy} disabled={!canGenerate} onClick={() => void generate(true)}>
            Refresh Phase 2 financial review
          </Button>}
          <a href="/finances">View financial information</a>
        </div>
      )}
    </main>
  );
}
