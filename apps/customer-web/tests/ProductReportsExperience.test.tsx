import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import ProductReportsExperience from '../src/product/ProductReportsExperience';

const session = {
  tenantId: 'T001',
  actorId: 'A100',
  authenticationContextId: 'customer-session',
  subjectVersion: 2,
  expiresAt: '2027-01-01T00:00:00Z',
};

const report = {
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

it('includes a reviewed Tax source figure and exports exact Reporting content with provenance', async () => {
  const taxItem = {
    label: 'Tax source figure', value: 14000, unit: 'USD',
    source: {
      sourceType: 'FinancialFact', sourceId: 'ff-tax',
      authoritativeOwner: 'Financial Profile Service', evidenceReferenceId: 'evidence-tax',
      financialProvenanceReferenceId: 'provenance-tax', calculationLineageReferenceId: null,
    },
  };
  const content = JSON.stringify({ reportId: 'report-tax', items: [taxItem] });
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    ...report, reportId: 'report-tax', items: [taxItem],
    sourceFinancialReferences: ['ff-tax'], evidenceReferences: ['evidence-tax'],
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
  expect(await screen.findByText('Tax source figure')).toBeVisible();
  expect(screen.getByText('$14,000.00')).toBeVisible();
  expect(screen.getByText(/not assessed tax owed, accepted filings or refunds/i)).toBeVisible();
  expect(screen.getByRole('link', { name: 'View source & history' }))
    .toHaveAttribute('href', '/finances?fact=ff-tax');
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
  const product = vi.spyOn(api, 'product').mockResolvedValue(report);
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
    undefined,
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

it('reopens a customer report only through the product report-read boundary', async () => {
  window.history.replaceState(null, '', '/reports?report=report-customer-basic-001');
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product').mockResolvedValue(report);

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
  const product = vi.spyOn(api, 'product').mockResolvedValue(report);
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
  vi.spyOn(api, 'product').mockResolvedValue(report);
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
  vi.spyOn(api, 'product').mockResolvedValue({
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
  vi.spyOn(api, 'product').mockResolvedValue({
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
  vi.spyOn(api, 'product').mockResolvedValue({
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
