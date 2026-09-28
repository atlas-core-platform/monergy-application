import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import axe from 'axe-core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import SearchExperience from '../src/search/SearchExperience';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
function renderSearch() {
  return render(
    <FoundationProvider>
      <SearchExperience />
    </FoundationProvider>,
  );
}
const response = {
  outcome: 'Success',
  error: null,
  data: {
    query: 'salary',
    matchMode: 'Semantic',
    items: [
      {
        searchRecordId: 'document-1',
        resultType: 'Document',
        title: 'Salary evidence',
        summary: 'Approved evidence metadata',
        authoritativeOwner: 'Evidence Service',
        representationState: 'DERIVED_REBUILDABLE',
        score: 1,
        source: {
          sourceObjectType: 'DocumentVersion',
          sourceObjectId: 'version-1',
          sourceReferenceId: 'evidence-1',
          provenanceReferenceId: 'evidence-provenance-1',
          calculationLineageReferenceId: null,
        },
      },
      {
        searchRecordId: 'calculation-1',
        resultType: 'Calculation',
        title: 'Savings ratio',
        summary: 'Deterministic calculation',
        authoritativeOwner: 'Financial Rules Service',
        representationState: 'DERIVED_REBUILDABLE',
        score: 0.8,
        source: {
          sourceObjectType: 'FinancialCalculation',
          sourceObjectId: 'calculation-1',
          sourceReferenceId: 'result-1',
          provenanceReferenceId: 'financial-provenance-1',
          calculationLineageReferenceId: 'lineage-1',
        },
      },
    ],
  },
};

describe('D07 authorized search experience', () => {
  it('shows the form, truthful reference label and initial empty state accessibly', async () => {
    const { container } = renderSearch();
    expect(screen.getByText('REFERENCE · LOCAL / CI ONLY')).toBeVisible();
    expect(screen.getByLabelText('Search query')).toBeVisible();
    expect(
      screen.getByText('Enter a query to inspect authorized source references.'),
    ).toBeVisible();
    expect((await axe.run(container, { resultTypes: ['violations'] })).violations).toHaveLength(0);
  });

  it('renders document and calculation results with provenance and lineage', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(response), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderSearch();
    await user.type(screen.getByLabelText('Search query'), 'salary');
    await user.click(screen.getByRole('switch', { name: 'Semantic reference matching' }));
    await user.click(screen.getByRole('button', { name: 'Search authorized references' }));
    expect(await screen.findByRole('status')).toHaveTextContent('2 authorized results · Semantic');
    expect(screen.getByText('Document')).toBeVisible();
    expect(screen.getByText('Calculation')).toBeVisible();
    expect(screen.getAllByText('DERIVED · REBUILDABLE')).toHaveLength(2);
    expect(screen.getByText('evidence-provenance-1')).toBeVisible();
    expect(screen.getByText('lineage-1')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith(
      '/contracts/cid-042/v1',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('renders an executed-empty state', async () => {
    const user = userEvent.setup();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            outcome: 'Success',
            error: null,
            data: { query: 'missing', matchMode: 'Lexical', items: [] },
          }),
          { status: 200 },
        ),
      ),
    );
    renderSearch();
    await user.type(screen.getByLabelText('Search query'), 'missing');
    await user.click(screen.getByRole('button', { name: 'Search authorized references' }));
    expect(await screen.findByText('No authorized results matched this query.')).toBeVisible();
  });

  it('surfaces access-safe failures without stale results', async () => {
    const user = userEvent.setup();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            outcome: 'Rejected',
            data: null,
            error: {
              code: 'search.authorization.denied',
              message: 'Current authorization is required.',
              retryable: false,
            },
          }),
          { status: 403 },
        ),
      ),
    );
    renderSearch();
    await user.type(screen.getByLabelText('Search query'), 'salary');
    await user.click(screen.getByRole('button', { name: 'Search authorized references' }));
    expect(await screen.findByText('Search failed safely')).toBeVisible();
    expect(screen.getByText(/search\.authorization\.denied/)).toBeVisible();
    expect(screen.queryByText('DERIVED · REBUILDABLE')).not.toBeInTheDocument();
  });
});
