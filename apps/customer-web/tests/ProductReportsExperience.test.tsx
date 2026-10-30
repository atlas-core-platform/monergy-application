import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import ProductReportsExperience from '../src/product/ProductReportsExperience';
import type { TrustedFinancialReport } from '../src/reports/types';

const session = {
  tenantId: 'T001',
  actorId: 'A100',
  authenticationContextId: 'customer-session',
  subjectVersion: 2,
  expiresAt: '2027-01-01T00:00:00Z',
};

const report: TrustedFinancialReport = {
  reportId: 'report-customer-basic-001',
  customerId: 'reference-customer',
  state: 'GENERATED' as const,
  generatedAt: '2026-10-16T09:00:00Z',
  items: [
    {
      label: 'Employment income',
      value: 125000,
      unit: 'USD',
      source: {
        sourceType: 'FinancialFact',
        sourceId: 'ff-income',
        authoritativeOwner: 'Financial Profile Service',
        evidenceReferenceId: 'evidence-income',
        financialProvenanceReferenceId: 'provenance-income',
        calculationLineageReferenceId: null,
      },
    },
    {
      label: 'Rent',
      value: 2500,
      unit: 'USD',
      source: {
        sourceType: 'FinancialFact',
        sourceId: 'ff-expense',
        authoritativeOwner: 'Financial Profile Service',
        evidenceReferenceId: 'evidence-expense',
        financialProvenanceReferenceId: 'provenance-expense',
        calculationLineageReferenceId: null,
      },
    },
  ],
  sourceFinancialReferences: ['ff-expense', 'ff-income'],
  evidenceReferences: ['evidence-expense', 'evidence-income'],
  financialProvenanceReferences: ['provenance-expense', 'provenance-income'],
  calculationLineageReferences: [],
  aiResponseTraceReference: null,
  auditCompatibilityReferenceId: 'audit-compatible-report-customer-basic-001',
  export: {
    fileName: 'trusted-financial-report-report-customer-basic-001.json',
    mediaType: 'application/json',
    content: '{"reportId":"report-customer-basic-001"}',
    sha256: 'ABC123',
  },
};

const reviewDay = '2026-09-30';
const netLine = { lineId: 'a'.repeat(32), side: 'ASSET', kind: 'CASH', name: 'Cash', amount: '1000.00', currency: 'USD', valueDate: reviewDay, sourceNote: 'Customer reviewed', record: null };
const netPosition = { positionId: 'net-position', operationId: 'b'.repeat(32), customerId: 'reference-customer', position: { asOfDate: reviewDay, currency: 'USD', lines: [netLine], assetsComplete: true, liabilitiesComplete: true, replacesPositionId: null }, lines: [{ line: netLine, source: null }], recordedBy: 'A100', recordedAt: '2026-10-02T00:00:00Z' };
const netWorth = { calculationId: 'net-worth-1', operationId: 'c'.repeat(32), customerId: 'reference-customer', position: netPosition, ruleId: 'monergy.net-worth', ruleVersion: '1.0.0', definitionHash: '1'.repeat(64), numericSemantics: 'Exact customer-reviewed net worth.', state: 'CALCULATED', totalAssets: '1000.00', totalLiabilities: '0.00', netWorth: '1000.00', issues: [], recordedBy: 'A100', recordedAt: '2026-10-02T00:00:00Z' };
const allocation = { calculationId: 'allocation-1', operationId: 'd'.repeat(32), customerId: 'reference-customer', sourceNetWorth: netWorth, categories: [{ category: 'CASH', amount: '1000.00', percentage: '100.00' }], ruleId: 'monergy.asset-allocation', ruleVersion: '1.0.0', definitionHash: '2'.repeat(64), numericSemantics: 'Exact customer-reviewed asset allocation.', state: 'CALCULATED', totalAssets: '1000.00', issues: [], recordedBy: 'A100', recordedAt: '2026-10-02T00:00:00Z' };
const incomeLine = { lineId: 'e'.repeat(32), side: 'INCOME', kind: 'SALARY', name: 'Salary', amount: '10000.00', currency: 'USD', valueDate: reviewDay, sourceNote: 'Customer reviewed', record: null, member: 'Self' };
const expenseLine = { lineId: 'f'.repeat(32), side: 'EXPENSE', kind: 'HOUSEHOLD', name: 'Household', amount: '5000.00', currency: 'USD', valueDate: reviewDay, sourceNote: 'Customer reviewed', record: null, member: 'Self' };
const cashPosition = { positionId: 'cash-position', operationId: '3'.repeat(32), customerId: 'reference-customer', position: { asOfDate: reviewDay, currency: 'USD', lines: [incomeLine, expenseLine], incomeComplete: true, expensesComplete: true, replacesPositionId: null, household: 'Self' }, lines: [{ line: incomeLine, source: null }, { line: expenseLine, source: null }], recordedBy: 'A100', recordedAt: '2026-10-02T00:00:00Z' };
const cashflow = { calculationId: 'cashflow-1', operationId: '4'.repeat(32), customerId: 'reference-customer', position: cashPosition, ruleId: 'monergy.monthly-cashflow', ruleVersion: '1.0.0', definitionHash: '5'.repeat(64), numericSemantics: 'Exact customer-reviewed cash flow.', state: 'CALCULATED', totalIncome: '10000.00', totalExpenses: '5000.00', balance: '5000.00', issues: [], recordedBy: 'A100', recordedAt: '2026-10-02T00:00:00Z' };
const cashflowRate = { calculationId: 'cashflow-rate-1', operationId: '6'.repeat(32), customerId: 'reference-customer', sourceMonthlyCashflow: cashflow, ruleId: 'monergy.monthly-net-cashflow-rate', ruleVersion: '1.0.0', definitionHash: '7'.repeat(64), numericSemantics: 'Exact customer-reviewed rate.', state: 'CALCULATED', monthlyIncome: '10000.00', monthlyExpenses: '5000.00', netCashflow: '5000.00', ratePercent: '50.00', issues: [], recordedBy: 'A100', recordedAt: '2026-10-02T00:00:00Z' };
const loan = { loanRecordId: 'loan-1', operationId: '8'.repeat(32), customerId: 'reference-customer', loan: { name: 'Home loan', lender: 'Bank', loanType: 'HOME', sanctionedAmount: 100000, outstandingBalance: 80000, interestRatePercent: 8, emi: 2500, tenureMonths: 60, startDate: '2026-01-01', endDate: '2030-12-31', balanceAsOfDate: reviewDay, sourceFinancialFactId: 'fact-loan', sourceRevision: 1 }, currency: 'USD', sourceProvenanceId: 'provenance-loan', documentVersionId: 'version-loan', evidenceId: 'evidence-loan', recordedBy: 'A100', recordedAt: '2026-10-02T00:00:00Z', revision: 2, lifecycleStatus: 'ACTIVE', lifecycleAsOfDate: reviewDay };
const emiShare = { calculationId: 'emi-share-1', operationId: '9'.repeat(32), customerId: 'reference-customer', sourceMonthlyCashflow: cashflow, sourceLoans: [loan], activeLoansComplete: true, ruleId: 'monergy.customer-reviewed-monthly-emi-share', ruleVersion: '1.0.0', definitionHash: 'a'.repeat(64), numericSemantics: 'Exact customer-reviewed EMI share.', state: 'CALCULATED', monthlyIncome: '10000.00', monthlyEmi: '2500.00', ratePercent: '25.00', issues: [], recordedBy: 'A100', recordedAt: '2026-10-02T00:00:00Z' };
const coverage = { calculationId: 'coverage-1', operationId: 'b'.repeat(32), customerId: 'reference-customer', sourceMonthlyCashflow: cashflow, sourceMonthlyEmiShare: emiShare, debtServiceIncludedInExpenses: true, ruleId: 'monergy.customer-reviewed-monthly-debt-service-coverage', ruleVersion: '1.0.0', definitionHash: 'c'.repeat(64), numericSemantics: 'Exact descriptive customer-reviewed coverage.', state: 'CALCULATED', monthlyIncome: '10000.00', monthlyExpenses: '5000.00', monthlyEmi: '2500.00', monthlyCashflowBalance: '5000.00', cashAvailableForDebtService: '7500.00', coverageMultiple: '3.00', issues: [], recordedBy: 'A100', recordedAt: '2026-10-02T00:00:00Z' };
const page = (items: unknown[]) => ({ items, hasMore: false, nextBeforeId: null });

function mockReportLifecycle(api: AccessApi, generated = report) {
  let job: { jobId: string; reportId: string; state: string; attempt: number;
    failureCode: null; retryable: boolean; updatedAt: string } | null = null;
  return vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (path === 'v1/reports' && method === 'POST') {
      const operationId = (body as { operationId: string }).operationId;
      job = { jobId: `job-report-${operationId}`, reportId: generated.reportId,
        state: 'Scheduled', attempt: 0, failureCode: null, retryable: false,
        updatedAt: '2026-10-29T09:00:00Z' };
      return Promise.resolve(job);
    }
    if (path.startsWith('v1/reports/jobs/') && method === 'GET' && job)
      return Promise.resolve({ ...job, state: 'Completed', attempt: 1,
        updatedAt: '2026-10-29T09:00:01Z' });
    if (path.startsWith('v1/reports/') && method === 'GET') return Promise.resolve(generated);
    return Promise.reject(new Error(`Unexpected product path ${method ?? 'GET'} ${path}`));
  });
}

function mount(api: AccessApi, capabilities = ['reporting.report.generate', 'reporting.report.read']) {
  render(
    <FoundationProvider appearance="midnight">
      <Context
        value={{
          status: 'authorized',
          session,
          api,
          authorization: {
            area: 'product',
            context: {
              tenantId: 'T001',
              actorId: 'A100',
              customerId: 'reference-customer',
              customerName: 'Reference Customer',
              customerDetail: 'Primary UAT customer',
              capabilities,
              environment: 'Test environment',
              dataDurability: 'Temporary data',
              productionAccepted: false,
            },
          },
          message: '',
          recovery: null,
          connect: () => Promise.resolve(),
          retry: () => Promise.resolve(),
          signOut: () => Promise.resolve(),
          cancel: () => undefined,
        }}
      >
        <ProductReportsExperience />
      </Context>
    </FoundationProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  window.history.replaceState(null, '', '/reports');
});

it('includes a customer-reviewed tax record with exact source history and exports governed content', async () => {
  const taxItem = {
    label: 'Income tax return — tax paid', value: 14000, unit: 'USD',
    source: {
      sourceType: 'TaxRecord', sourceId: 'tax-record-001',
      authoritativeOwner: 'Financial Profile Service', evidenceReferenceId: 'evidence-tax',
      financialProvenanceReferenceId: 'provenance-tax', calculationLineageReferenceId: null,
      financialFactReferenceId: 'ff-tax', financialFactRevision: 1,
      documentVersionReferenceId: 'document-version-tax',
    },
  };
  const content = JSON.stringify({ reportId: 'report-tax', items: [taxItem] });
  const api = new AccessApi(session);
  mockReportLifecycle(api, {
    ...report, reportId: 'report-tax', items: [taxItem],
    sourceFinancialReferences: ['tax-record-001'], evidenceReferences: ['evidence-tax'],
    financialProvenanceReferences: ['provenance-tax'],
    export: { ...report.export, content },
  });
  const createObjectURL = vi.fn<(blob: Blob) => string>().mockReturnValue('blob:tax-report');
  Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectURL });
  Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: vi.fn() });
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
  mount(api);
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Generate basic summary' }));
  expect(await screen.findByText('Income tax return — tax paid')).toBeVisible();
  expect(screen.getByText('$14,000.00')).toBeVisible();
  expect(screen.getByText(/do not establish assessed tax liability, an accepted filing, payment confirmation, refund entitlement or tax advice/i)).toBeVisible();
  expect(screen.getByText('REVIEWED TAX RECORD')).toBeVisible();
  expect(screen.getByRole('link', { name: 'View tax record & history' }))
    .toHaveAttribute('href', '/finances?type=TAX&taxRecord=tax-record-001');
  await user.click(screen.getByRole('button', { name: 'Technical details' }));
  expect(screen.getByText('ff-tax · revision 1')).toBeVisible();
  expect(screen.getByText('document-version-tax')).toBeVisible();
  await user.click(screen.getByRole('button', { name: 'Export basic summary' }));
  const blob = createObjectURL.mock.calls[0]?.[0];
  expect(blob).toBeInstanceOf(Blob);
  if (!blob) throw new Error('Reporting export did not create a file');
  const text = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      if (typeof reader.result === 'string') resolve(reader.result);
      else reject(new Error('Export did not contain text'));
    };
    reader.onerror = () => { reject(new Error('Export could not be read')); };
    reader.readAsText(blob);
  });
  expect(text).toBe(content);
});

it('includes an exact-current Customer-defined goal snapshot without advice or achievement claims', async () => {
  const goalItem = {
    label: 'Emergency fund — customer-defined target', value: 25000, unit: 'USD',
    source: {
      sourceType: 'FinancialGoal', sourceId: 'financial-goal-001',
      authoritativeOwner: 'Financial Profile Service', evidenceReferenceId: null,
      financialProvenanceReferenceId: null, calculationLineageReferenceId: null,
      financialGoal: {
        revision: 3, operationId: 'd'.repeat(32), targetDate: '2027-12-31',
        lifecycleStatus: 'PAUSED' as const, lifecycleAsOfDate: '2026-10-20',
        priorityLevel: 'HIGH' as const, priorityAsOfDate: '2026-10-21',
        recordedAt: '2026-10-29T10:00:00Z',
      },
    },
  };
  const content = JSON.stringify({ reportId: 'report-goal', items: [goalItem] });
  const api = new AccessApi(session);
  mockReportLifecycle(api, {
    ...report, reportId: 'report-goal', items: [goalItem],
    sourceFinancialReferences: ['financial-goal-001'], evidenceReferences: [],
    financialProvenanceReferences: [],
    export: { ...report.export, content },
  });
  const createObjectURL = vi.fn<(blob: Blob) => string>().mockReturnValue('blob:goal-report');
  Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectURL });
  Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: vi.fn() });
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
  mount(api);
  const user = userEvent.setup();

  await user.click(screen.getByRole('button', { name: 'Generate basic summary' }));
  expect(await screen.findByText('Emergency fund — customer-defined target')).toBeVisible();
  expect(screen.getByText('$25,000.00')).toBeVisible();
  expect(screen.getByText('CUSTOMER-DEFINED GOAL')).toBeVisible();
  expect(screen.getByText(/do not establish a required contribution, savings allocation, recommendation, eligibility or independently verified achievement/i)).toBeVisible();
  expect(screen.getByRole('link', { name: 'View goal & history' }))
    .toHaveAttribute('href', '/finances?type=GOAL&goal=financial-goal-001');
  await user.click(screen.getByRole('button', { name: 'Technical details' }));
  expect(screen.getByText('3')).toBeVisible();
  expect(screen.getByText('d'.repeat(32))).toBeVisible();
  expect(screen.getByText('Paused · 2026-10-20')).toBeVisible();
  expect(screen.getByText('High · 2026-10-21')).toBeVisible();
  expect(screen.getAllByText('Not applicable')).toHaveLength(2);
  await user.click(screen.getByRole('button', { name: 'Export basic summary' }));
  const blob = createObjectURL.mock.calls[0]?.[0];
  expect(blob).toBeInstanceOf(Blob);
  if (!blob) throw new Error('Reporting export did not create a file');
  const text = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      if (typeof reader.result === 'string') resolve(reader.result);
      else reject(new Error('Export did not contain text'));
    };
    reader.onerror = () => { reject(new Error('Export could not be read')); };
    reader.readAsText(blob);
  });
  expect(text).toBe(content);
});

it('generates an authorized source-connected basic summary without scores or advice', async () => {
  const api = new AccessApi(session);
  const product = mockReportLifecycle(api);
  mount(api);

  await userEvent.setup().click(screen.getByRole('button', { name: 'Generate basic summary' }));

  expect(await screen.findByRole('heading', { name: 'Included in this summary' })).toBeVisible();
  expect(screen.getByText('Employment income')).toBeVisible();
  expect(screen.getByText('$125,000.00')).toBeVisible();
  expect(screen.getByText('Rent')).toBeVisible();
  expect(screen.getByText('$2,500.00')).toBeVisible();
  expect(product).toHaveBeenCalledWith(
    'v1/reports',
    'POST',
    expect.objectContaining({ operationId: expect.stringMatching(/^[0-9a-f]{32}$/) }),
    expect.any(AbortSignal),
  );
  expect(screen.getAllByRole('link', { name: 'View source & history' })[0]).toHaveAttribute(
    'href',
    '/finances?fact=ff-income',
  );
  expect(screen.queryByText(/financial health score/i)).not.toBeInTheDocument();
  for (const item of screen.getAllByRole('article')) {
    expect(within(item).queryByText(/recommendation/i)).not.toBeInTheDocument();
  }
  expect(screen.queryByText(/savings ratio/i)).not.toBeInTheDocument();
});

it('shows the governed background lifecycle and prevents duplicate generation while scheduled', async () => {
  const api = new AccessApi(session);
  let scheduled: Record<string, unknown> = {};
  let complete: (value: Record<string, unknown>) => void = (value) => {
    throw new Error(`Report status request was not started: ${JSON.stringify(value)}`);
  };
  vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (path === 'v1/reports' && method === 'POST') {
      const operationId = (body as { operationId: string }).operationId;
      scheduled = { jobId: `job-report-${operationId}`, reportId: report.reportId,
        state: 'Scheduled', attempt: 0, failureCode: null, retryable: false,
        updatedAt: '2026-10-29T09:00:00Z' };
      return Promise.resolve(scheduled);
    }
    if (path.startsWith('v1/reports/jobs/') && scheduled.jobId)
      return new Promise<Record<string, unknown>>((resolve) => { complete = resolve; });
    if (path === `v1/reports/${report.reportId}`) return Promise.resolve(report);
    return Promise.reject(new Error(`Unexpected product path ${method ?? 'GET'} ${path}`));
  });
  mount(api);

  await userEvent.setup().click(screen.getByRole('button', { name: 'Generate basic summary' }));
  expect(await screen.findByText(/Background status: Scheduled/i)).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Generate basic summary' })).not.toBeInTheDocument();
  act(() => { complete({ ...scheduled, state: 'Completed', attempt: 1 }); });
  expect(await screen.findByText('Employment income')).toBeVisible();
});

it('cancels a scheduled report idempotently and prevents duplicate cancellation clicks', async () => {
  const api = new AccessApi(session);
  let scheduled: Record<string, unknown> = {};
  let finishCancellation: (value: Record<string, unknown>) => void = () => {
    throw new Error('Cancellation request was not started');
  };
  const product = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (path === 'v1/reports' && method === 'POST') {
      const operationId = (body as { operationId: string }).operationId;
      scheduled = { jobId: `job-report-${operationId}`, reportId: report.reportId,
        state: 'Scheduled', attempt: 0, failureCode: null, retryable: false,
        updatedAt: '2026-10-29T09:00:00Z' };
      return Promise.resolve(scheduled);
    }
    if (path.endsWith('/cancellations') && method === 'POST')
      return new Promise<Record<string, unknown>>((resolve) => { finishCancellation = resolve; });
    if (path.startsWith('v1/reports/jobs/') && method === 'GET')
      return new Promise(() => undefined);
    return Promise.reject(new Error(`Unexpected product path ${method ?? 'GET'} ${path}`));
  });
  mount(api);
  const user = userEvent.setup();

  await user.click(screen.getByRole('button', { name: 'Generate basic summary' }));
  const cancel = await screen.findByRole('button', { name: 'Cancel report request' });
  await user.click(cancel);
  await user.click(cancel);
  const calls = product.mock.calls.filter(([path, method]) =>
    path.endsWith('/cancellations') && method === 'POST');
  expect(calls).toHaveLength(1);
  expect(calls[0]?.[2]).toEqual({ operationId: expect.stringMatching(/^[0-9a-f]{32}$/) });
  act(() => { finishCancellation({ ...scheduled, state: 'Cancelled' }); });
  expect(await screen.findByText('Your financial summary request was cancelled.')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Cancel report request' })).not.toBeInTheDocument();
});

it('retries one failed retryable report operation and prevents duplicate retry clicks', async () => {
  const api = new AccessApi(session);
  let scheduled: Record<string, unknown> = {};
  let statusChecks = 0;
  let finishRetry: (value: Record<string, unknown>) => void = () => {
    throw new Error('Retry request was not started');
  };
  const product = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (path === 'v1/reports' && method === 'POST') {
      const operationId = (body as { operationId: string }).operationId;
      scheduled = { jobId: `job-report-${operationId}`, reportId: report.reportId,
        state: 'Scheduled', attempt: 0, failureCode: null, retryable: false,
        updatedAt: '2026-10-29T09:00:00Z' };
      return Promise.resolve(scheduled);
    }
    if (path.endsWith('/retries') && method === 'POST')
      return new Promise<Record<string, unknown>>((resolve) => { finishRetry = resolve; });
    if (path.startsWith('v1/reports/jobs/') && method === 'GET') {
      statusChecks += 1;
      return Promise.resolve(statusChecks === 1
        ? { ...scheduled, state: 'Failed', attempt: 1, failureCode: 'reporting.owner.temporarily-unavailable', retryable: true }
        : { ...scheduled, state: 'Completed', attempt: 2, failureCode: null, retryable: false });
    }
    if (path === `v1/reports/${report.reportId}` && method === 'GET') return Promise.resolve(report);
    return Promise.reject(new Error(`Unexpected product path ${method ?? 'GET'} ${path}`));
  });
  mount(api);
  const user = userEvent.setup();

  await user.click(screen.getByRole('button', { name: 'Generate basic summary' }));
  expect(await screen.findByText('Your financial summary could not be completed.')).toBeVisible();
  expect(screen.getByText(/original report intent is unchanged/i)).toBeVisible();
  const retry = screen.getByRole('button', { name: 'Retry report request' });
  await user.click(retry);
  await user.click(retry);
  const calls = product.mock.calls.filter(([path, method]) =>
    path.endsWith('/retries') && method === 'POST');
  expect(calls).toHaveLength(1);
  expect(calls[0]?.[2]).toEqual({ operationId: expect.stringMatching(/^[0-9a-f]{32}$/) });
  act(() => { finishRetry({ ...scheduled, state: 'Scheduled', failureCode: null, retryable: false }); });
  expect(await screen.findByText('Employment income')).toBeVisible();
});

it('does not offer retry for a non-retryable failed report job', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (path === 'v1/reports' && method === 'POST') {
      const operationId = (body as { operationId: string }).operationId;
      return Promise.resolve({ jobId: `job-report-${operationId}`, reportId: report.reportId,
        state: 'Scheduled', attempt: 0, failureCode: null, retryable: false,
        updatedAt: '2026-10-29T09:00:00Z' });
    }
    if (path.startsWith('v1/reports/jobs/') && method === 'GET')
      return Promise.resolve({ jobId: path.split('/').at(-1), reportId: report.reportId,
        state: 'Failed', attempt: 1, failureCode: 'reporting.authorization.denied', retryable: false,
        updatedAt: '2026-10-29T09:00:01Z' });
    return Promise.reject(new Error(`Unexpected product path ${method ?? 'GET'} ${path}`));
  });
  mount(api);

  await userEvent.setup().click(screen.getByRole('button', { name: 'Generate basic summary' }));
  expect(await screen.findByText('Your financial summary could not be completed.')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Retry report request' })).not.toBeInTheDocument();
});

it('generates a Phase 2 review from exact matching receipt identifiers without posting figures', async () => {
  const phase2Report = {
    ...report,
    reportId: 'report-phase2',
    items: [{
      label: 'Monthly debt-service coverage', value: 3, unit: 'x',
      source: { sourceType: 'FinancialCalculation', sourceId: 'coverage-1',
        authoritativeOwner: 'Financial Rules Service', evidenceReferenceId: null,
        financialProvenanceReferenceId: null, calculationLineageReferenceId: 'coverage-1',
        ruleId: coverage.ruleId, ruleVersion: '1.0.0', definitionHash: coverage.definitionHash,
        numericSemantics: coverage.numericSemantics, sourceCalculationIds: ['cashflow-1', 'emi-share-1'] },
    }],
    sourceFinancialReferences: ['coverage-1'], evidenceReferences: [],
    financialProvenanceReferences: [], calculationLineageReferences: ['coverage-1'],
  };
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product').mockImplementation((path, method) => {
    if (method === 'POST' && path === 'v1/reports') return Promise.resolve({
      jobId: `job-report-${'1'.repeat(32)}`, reportId: phase2Report.reportId,
      state: 'Scheduled', attempt: 0, failureCode: null, retryable: false,
      updatedAt: '2026-10-29T09:00:00Z',
    });
    if (path.startsWith('v1/reports/jobs/')) return Promise.resolve({
      jobId: `job-report-${'1'.repeat(32)}`, reportId: phase2Report.reportId,
      state: 'Completed', attempt: 1, failureCode: null, retryable: false,
      updatedAt: '2026-10-29T09:00:01Z',
    });
    if (path === 'v1/reports/report-phase2') return Promise.resolve(phase2Report);
    if (path.endsWith('asset-allocations')) return Promise.resolve(page([allocation]));
    if (path.endsWith('net-cashflow-rates')) return Promise.resolve(page([cashflowRate]));
    if (path.endsWith('emi-shares')) return Promise.resolve(page([emiShare]));
    if (path.endsWith('debt-service-coverages')) return Promise.resolve(page([coverage]));
    return Promise.reject(new Error('Unexpected path ' + path));
  });
  mount(api, ['reporting.report.generate', 'reporting.report.read', 'financial-rules.calculation.execute']);

  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Generate Phase 2 financial review' }));
  expect(await screen.findByText('Monthly debt-service coverage')).toBeVisible();
  expect(screen.getByText('3.00 x')).toBeVisible();
  const write = product.mock.calls.find(([path, method]) => path === 'v1/reports' && method === 'POST');
  expect(write?.[2]).toEqual({
    operationId: expect.stringMatching(/^[0-9a-f]{32}$/),
    financialReview: {
      assetAllocationCalculationId: 'allocation-1',
      monthlyNetCashflowRateCalculationId: 'cashflow-rate-1',
      monthlyEmiShareCalculationId: 'emi-share-1',
      monthlyDebtServiceCoverageCalculationId: 'coverage-1',
    },
  });
  expect(JSON.stringify(write?.[2])).not.toContain('10000.00');
  await user.click(screen.getByRole('button', { name: 'Technical details' }));
  expect(screen.getByText(coverage.definitionHash)).toBeVisible();
});

it('reopens a customer report only through the product report-read boundary', async () => {
  window.history.replaceState(null, '', '/reports?report=report-customer-basic-001');
  const api = new AccessApi(session);
  const product = mockReportLifecycle(api);

  mount(api);

  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(product).toHaveBeenCalledWith(
    'v1/reports/report-customer-basic-001',
    'GET',
    undefined,
    expect.any(AbortSignal),
  );
});

it('keeps report read usable and requests the missing generate action', async () => {
  window.history.replaceState(null, '', '/reports?report=report-customer-basic-001');
  const api = new AccessApi(session);
  const product = mockReportLifecycle(api);
  const item = { requestId: null, target: 'customer.reports', targetLabel: 'Reports',
    grantExplanation: 'Allows governed report generation.', state: 'NotRequested',
    hasAccess: false, canRequest: true, createdAt: null, decisionAt: null,
    fulfilledAt: null, decisionReason: null };
  const request = vi.spyOn(api, 'request').mockImplementation((path, method = 'GET', body) => {
    if (method === 'GET') return Promise.resolve({ tenantId: 'T001', actorId: 'A100',
      policyVersion: 8, subjectVersion: 2, items: [item] });
    expect(path).toBe('access-requests/customer.reports/submit');
    expect(body).toEqual({ operationId: expect.stringMatching(/^[0-9a-f]{32}$/) });
    return Promise.resolve({ ...item, requestId: 'a'.repeat(32), state: 'Submitted',
      canRequest: false, createdAt: '2026-10-28T00:00:00Z' });
  });

  mount(api, ['reporting.report.read']);
  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(product).toHaveBeenCalledWith('v1/reports/report-customer-basic-001', 'GET', undefined,
    expect.any(AbortSignal));
  expect(screen.queryByRole('button', { name: 'Generate basic summary' })).not.toBeInTheDocument();
  await userEvent.setup().click(await screen.findByRole('button', { name: 'Request access' }));
  expect(request.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(1);
});

it('exports exactly the governed report payload returned by Reporting', async () => {
  const api = new AccessApi(session);
  mockReportLifecycle(api);
  const createObjectURL = vi.fn().mockReturnValue('blob:customer-report');
  const revokeObjectURL = vi.fn();
  Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectURL });
  Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectURL });
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
  mount(api);

  await userEvent.setup().click(screen.getByRole('button', { name: 'Generate basic summary' }));
  const summary = await screen.findByLabelText('Basic financial summary');
  expect(within(summary).getAllByText('2')).toHaveLength(2);

  await userEvent.setup().click(screen.getByRole('button', { name: 'Export basic summary' }));
  expect(createObjectURL).toHaveBeenCalledOnce();
  expect(revokeObjectURL).toHaveBeenCalledWith('blob:customer-report');
});

it('keeps the journey unavailable when the current access pack has not granted Reporting', () => {
  const api = new AccessApi(session);
  mount(api, ['financial-profile.profile.read']);

  expect(
    screen.getByText("Basic reports aren't included in your current access"),
  ).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Generate basic summary' })).not.toBeInTheDocument();
});


it('includes a trusted Asset source figure as a report item without a valuation or net-worth claim', async () => {
  const api = new AccessApi(session);
  const assetItem = {
    label: 'Document-stated property figure',
    value: 900000,
    unit: 'USD',
    source: {
      sourceType: 'FinancialFact',
      sourceId: 'ff-asset',
      authoritativeOwner: 'Financial Profile Service',
      evidenceReferenceId: 'evidence-asset',
      financialProvenanceReferenceId: 'provenance-asset',
      calculationLineageReferenceId: null,
    },
  };
  mockReportLifecycle(api, {
    ...report,
    items: [...report.items, assetItem],
    sourceFinancialReferences: [...report.sourceFinancialReferences, 'ff-asset'],
    evidenceReferences: [...report.evidenceReferences, 'evidence-asset'],
    financialProvenanceReferences: [...report.financialProvenanceReferences, 'provenance-asset'],
  });

  mount(api);
  await userEvent.setup().click(screen.getByRole('button', { name: 'Generate basic summary' }));

  expect(await screen.findByText('Document-stated property figure')).toBeVisible();
  expect(screen.getByText('$900,000.00')).toBeVisible();
  expect(screen.getByText(/Asset source figures are document-reported values, not independently verified valuations/i)).toBeVisible();
  const sources = screen.getAllByRole('link', { name: 'View source & history' });
  expect(sources[2]).toHaveAttribute('href', '/finances?fact=ff-asset');
  expect(screen.queryByText(/verified net worth|approved collateral valuation/i)).not.toBeInTheDocument();
});


it('includes a trusted Investment source item without implying live holdings or NAV', async () => {
  const api = new AccessApi(session);
  const sourceItem = {
    label: 'Document-stated investment figure',
    value: 500,
    unit: 'USD',
    source: {
      sourceType: 'FinancialFact',
      sourceId: 'ff-investment',
      authoritativeOwner: 'Financial Profile Service',
      evidenceReferenceId: 'evidence-investment',
      financialProvenanceReferenceId: 'provenance-investment',
      calculationLineageReferenceId: null,
    },
  };
  mockReportLifecycle(api, {
    ...report,
    items: [...report.items, sourceItem],
    sourceFinancialReferences: [...report.sourceFinancialReferences, 'ff-investment'],
    evidenceReferences: [...report.evidenceReferences, 'evidence-investment'],
    financialProvenanceReferences: [...report.financialProvenanceReferences, 'provenance-investment'],
  });
  mount(api);
  await userEvent.setup().click(screen.getByRole('button', { name: 'Generate basic summary' }));
  expect(await screen.findByText('Document-stated investment figure')).toBeVisible();
  expect(screen.getByText('$500.00')).toBeVisible();
  expect(screen.getByText(/not live securities holdings, current NAV, market prices or portfolio returns/i)).toBeVisible();
  const links = screen.getAllByRole('link', { name: 'View source & history' });
  expect(links[2]).toHaveAttribute('href', '/finances?fact=ff-investment');
  expect(screen.queryByText(/verified brokerage balance|investment performance score/i)).not.toBeInTheDocument();
});


it('includes a trusted Insurance source figure without claiming verified coverage or benefit', async () => {
  const api = new AccessApi(session);
  const sourceItem = {
    label: 'Document-stated insurance figure',
    value: 100,
    unit: 'USD',
    source: {
      sourceType: 'FinancialFact',
      sourceId: 'ff-insurance',
      authoritativeOwner: 'Financial Profile Service',
      evidenceReferenceId: 'evidence-insurance',
      financialProvenanceReferenceId: 'provenance-insurance',
      calculationLineageReferenceId: null,
    },
  };
  mockReportLifecycle(api, {
    ...report,
    items: [...report.items, sourceItem],
    sourceFinancialReferences: [...report.sourceFinancialReferences, 'ff-insurance'],
    evidenceReferences: [...report.evidenceReferences, 'evidence-insurance'],
    financialProvenanceReferences: [...report.financialProvenanceReferences, 'provenance-insurance'],
  });
  mount(api);
  await userEvent.setup().click(screen.getByRole('button', { name: 'Generate basic summary' }));
  expect(await screen.findByText('Document-stated insurance figure')).toBeVisible();
  expect(screen.getByText('$100.00')).toBeVisible();
  expect(screen.getByText(/Insurance source figures are not verified policy benefits, claims, premiums or cash values/i)).toBeVisible();
  const links = screen.getAllByRole('link', { name: 'View source & history' });
  expect(links[2]).toHaveAttribute('href', '/finances?fact=ff-insurance');
  expect(screen.queryByText(/approved claim|certified policy sum insured/i)).not.toBeInTheDocument();
});
