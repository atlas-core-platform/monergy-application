import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import axe from 'axe-core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ReportsExperience from '../src/reports/ReportsExperience';

const report = {
  reportId: 'report-reference-1',
  customerId: 'reference-customer',
  state: 'GENERATED',
  generatedAt: '2026-09-01T00:00:00Z',
  items: [
    {
      label: 'Monthly income',
      value: 125000,
      unit: 'INR',
      source: {
        sourceType: 'FinancialFact',
        sourceId: 'fact-1',
        authoritativeOwner: 'Financial Profile Service',
        evidenceReferenceId: 'evidence-1',
        financialProvenanceReferenceId: 'provenance-1',
        calculationLineageReferenceId: null,
      },
    },
    {
      label: 'Savings ratio',
      value: 0.36,
      unit: 'RATIO',
      source: {
        sourceType: 'FinancialCalculation',
        sourceId: 'calculation-1',
        authoritativeOwner: 'Financial Rules Service',
        evidenceReferenceId: null,
        financialProvenanceReferenceId: 'provenance-1',
        calculationLineageReferenceId: 'lineage-1',
      },
    },
  ],
  sourceFinancialReferences: ['fact-1', 'calculation-1'],
  evidenceReferences: ['evidence-1'],
  financialProvenanceReferences: ['provenance-1'],
  calculationLineageReferences: ['lineage-1'],
  aiResponseTraceReference: null,
  auditCompatibilityReferenceId: 'audit-compatible-report-reference-1',
  export: {
    fileName: 'trusted-financial-report.json',
    mediaType: 'application/json',
    content: '{"reportId":"report-reference-1"}',
    sha256: 'ABC123',
  },
};

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});
function renderReports() {
  return render(
    <FoundationProvider>
      <ReportsExperience />
    </FoundationProvider>,
  );
}
function successfulFetch() {
  return vi.fn().mockImplementation(() =>
    Promise.resolve(
      new Response(JSON.stringify({ outcome: 'Success', error: null, data: report }), {
        status: 200,
      }),
    ),
  );
}

describe('D08 trusted reporting experience', () => {
  it('shows truthful label and empty state accessibly', async () => {
    const { container } = renderReports();
    expect(screen.getByText('REFERENCE · LOCAL / CI ONLY')).toBeVisible();
    expect(
      screen.getByText('Generate a basic report to inspect authorized financial references.'),
    ).toBeVisible();
    expect((await axe.run(container, { resultTypes: ['violations'] })).violations).toHaveLength(0);
  });

  it('generates, reloads, and displays authoritative values', async () => {
    const user = userEvent.setup();
    const fetchMock = successfulFetch();
    vi.stubGlobal('fetch', fetchMock);
    renderReports();
    await user.click(screen.getByRole('button', { name: 'Generate basic report' }));
    expect(await screen.findByText('Financial snapshot')).toBeVisible();
    expect(screen.getByText('₹1,25,000')).toBeVisible();
    expect(screen.getByText('36%')).toBeVisible();
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      '/contracts/cid-051/v1',
      expect.objectContaining({ method: 'POST' }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/contracts/cid-052/v1',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('drills down to evidence, provenance and calculation lineage', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', successfulFetch());
    renderReports();
    await user.click(screen.getByRole('button', { name: 'Generate basic report' }));
    await user.click(await screen.findByText('Monthly income · Financial Profile Service'));
    expect(await screen.findByText('evidence-1')).toBeVisible();
    expect(screen.getByText('provenance-1')).toBeVisible();
    await user.click(screen.getByText('Savings ratio · Financial Rules Service'));
    expect(await screen.findByText('lineage-1')).toBeVisible();
  });

  it('exports the exact governed report content', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', successfulFetch());
    const createObjectURL = vi.fn().mockReturnValue('blob:report');
    const revokeObjectURL = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectURL });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectURL });
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    renderReports();
    await user.click(screen.getByRole('button', { name: 'Generate basic report' }));
    await user.click(await screen.findByRole('button', { name: 'Export basic report' }));
    expect(createObjectURL).toHaveBeenCalledOnce();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:report');
  });

  it('shows a classified safe failure and no stale report', async () => {
    const user = userEvent.setup();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            outcome: 'Rejected',
            data: null,
            error: {
              code: 'report.authorization.denied',
              message: 'Current authorization is required.',
              retryable: false,
            },
          }),
          { status: 403 },
        ),
      ),
    );
    renderReports();
    await user.click(screen.getByRole('button', { name: 'Generate basic report' }));
    expect(await screen.findByText('Report failed safely')).toBeVisible();
    expect(screen.getByText(/report\.authorization\.denied/)).toBeVisible();
    expect(screen.queryByText('Financial snapshot')).not.toBeInTheDocument();
  });
});
