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

it('generates an authorized source-connected basic summary without scores or advice', async () => {
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product').mockResolvedValue(report as never);
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
  expect(screen.queryByText(/recommendation/i)).not.toBeInTheDocument();
  expect(screen.queryByText(/savings ratio/i)).not.toBeInTheDocument();
});

it('reopens a customer report only through the product report-read boundary', async () => {
  window.history.replaceState(null, '', '/reports?report=report-customer-basic-001');
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product').mockResolvedValue(report as never);

  mount(api);

  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(product).toHaveBeenCalledWith(
    'v1/reports/report-customer-basic-001',
    'GET',
    undefined,
    expect.any(AbortSignal),
  );
});

it('exports exactly the governed report payload returned by Reporting', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue(report as never);
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
  } as never);

  mount(api);
  await userEvent.setup().click(screen.getByRole('button', { name: 'Generate basic summary' }));

  expect(await screen.findByText('Document-stated property figure')).toBeVisible();
  expect(screen.getByText('$900,000.00')).toBeVisible();
  expect(screen.getByText(/Asset source figures are document-reported values, not independently verified valuations/i)).toBeVisible();
  const sources = screen.getAllByRole('link', { name: 'View source & history' });
  expect(sources[2]).toHaveAttribute('href', '/finances?fact=ff-asset');
  expect(screen.queryByText(/verified net worth|approved collateral valuation/i)).not.toBeInTheDocument();
});
