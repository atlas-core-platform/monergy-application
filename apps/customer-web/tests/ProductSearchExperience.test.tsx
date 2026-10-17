import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import ProductSearchExperience from '../src/product/ProductSearchExperience';

const session = {
  tenantId: 'T001',
  actorId: 'A100',
  authenticationContextId: 'customer-session',
  subjectVersion: 2,
  expiresAt: '2027-01-01T00:00:00Z',
};

function mount(api: AccessApi, capabilities = ['search.query.execute']) {
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
        <ProductSearchExperience />
      </Context>
    </FoundationProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

it('searches current Documents and Financial information through the product boundary', async () => {
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product').mockResolvedValue({
    query: 'salary',
    matchMode: 'Lexical',
    items: [
      {
        searchRecordId: 'search-document-document-version-001',
        resultType: 'Document',
        title: 'salary-statement.txt',
        summary: 'salary-statement.txt Customer upload text/plain version 1',
        authoritativeOwner: 'Evidence Service',
        representationState: 'DERIVED_REBUILDABLE',
        score: 1,
        source: {
          sourceObjectType: 'DocumentVersion',
          sourceObjectId: 'document-version-001',
          sourceReferenceId: 'evidence-001',
          provenanceReferenceId: null,
          calculationLineageReferenceId: null,
        },
      },
      {
        searchRecordId: 'search-financial-ff-income',
        resultType: 'Financial',
        title: 'Employment income',
        summary: 'Employment income INCOME 125000 USD effective 2026-10-01',
        authoritativeOwner: 'Financial Profile Service',
        representationState: 'DERIVED_REBUILDABLE',
        score: 0.8,
        source: {
          sourceObjectType: 'FinancialFact',
          sourceObjectId: 'ff-income',
          sourceReferenceId: 'financial-profile-reference-customer',
          provenanceReferenceId: 'provenance-income',
          calculationLineageReferenceId: null,
        },
      },
    ],
  } as never);
  mount(api);

  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'salary');
  await user.click(screen.getByRole('button', { name: 'Search' }));

  expect(await screen.findByText('2 results for “salary”')).toBeVisible();
  expect(product).toHaveBeenCalledWith(
    'v1/search',
    'POST',
    { query: 'salary', scope: 'all' },
    undefined,
  );
  expect(screen.getByRole('link', { name: 'Open document' })).toHaveAttribute(
    'href',
    '/evidence?version=document-version-001',
  );
  expect(screen.getByRole('link', { name: 'View source & history' })).toHaveAttribute(
    'href',
    '/finances?fact=ff-income',
  );
  expect(screen.queryByText(/calculation/i)).not.toBeInTheDocument();
  expect(screen.queryByText(/semantic/i)).not.toBeInTheDocument();
});

it('lets the customer bound the query to Financial information', async () => {
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product').mockResolvedValue({
    query: 'rent',
    matchMode: 'Lexical',
    items: [],
  } as never);
  mount(api);

  const user = userEvent.setup();
  await user.click(screen.getByRole('radio', { name: 'Financial information' }));
  await user.type(screen.getByLabelText('Search query'), 'rent');
  await user.click(screen.getByRole('button', { name: 'Search' }));

  expect(await screen.findByText('No trusted financial information matched this query.')).toBeVisible();
  expect(product).toHaveBeenCalledWith(
    'v1/search',
    'POST',
    { query: 'rent', scope: 'financial' },
    undefined,
  );
});

it('does not expose a search action before the Customer role receives Search access', () => {
  const api = new AccessApi(session);
  mount(api, ['financial-profile.profile.read']);

  expect(screen.getByText("Search isn't included in your current access")).toBeVisible();
  expect(screen.getByLabelText('Search query')).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Search' })).toBeDisabled();
});


it('searches trusted Loan source facts with provenance without implying verified outstanding balances', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    query: 'loan',
    matchMode: 'Lexical',
    items: [{
      searchRecordId: 'search-financial-ff-loan',
      resultType: 'Financial',
      title: 'Loan source amount',
      summary: 'Loan source amount (not outstanding balance) 85000 USD effective 2026-10-01',
      authoritativeOwner: 'Financial Profile Service',
      representationState: 'DERIVED_REBUILDABLE',
      score: 1,
      source: {
        sourceObjectType: 'FinancialFact',
        sourceObjectId: 'ff-loan',
        sourceReferenceId: 'financial-profile-reference-customer',
        provenanceReferenceId: 'provenance-loan',
        calculationLineageReferenceId: null,
      },
    }],
  } as never);
  mount(api);
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'loan');
  await user.click(screen.getByRole('button', { name: 'Search' }));
  expect(await screen.findByText('1 result for “loan”')).toBeVisible();
  expect(screen.getByText(/not outstanding balance/i)).toBeVisible();
  expect(screen.getByRole('link', { name: 'View source & history' })).toHaveAttribute(
    'href',
    '/finances?fact=ff-loan',
  );
  expect(screen.queryByText(/eligible to borrow|monthly EMI/i)).not.toBeInTheDocument();
});
