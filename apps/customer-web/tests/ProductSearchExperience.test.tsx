import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, render, screen, within } from '@testing-library/react';
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

const fullSearchCapabilities = [
  'search.query.execute', 'evidence.document.read', 'financial-profile.profile.read',
];

function mount(api: AccessApi, capabilities = fullSearchCapabilities) {
  return render(element(api, capabilities));
}
function element(api: AccessApi, capabilities = fullSearchCapabilities, customerId = 'reference-customer') {
  return (
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
              customerId,
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
    </FoundationProvider>
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
  });
  mount(api);

  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'salary');
  await user.click(screen.getByRole('button', { name: 'Search' }));

  expect(await screen.findByText('2 results for “salary”')).toBeVisible();
  expect(product).toHaveBeenCalledWith(
    'v1/search',
    'POST',
    { query: 'salary', scope: 'all' },
    expect.any(AbortSignal),
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

it('opens an exact customer-reviewed tax record without implying assessed or accepted tax status', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    query: 'income tax',
    matchMode: 'Lexical',
    items: [{
      searchRecordId: 'search-financial-tax-record-tax-record-001',
      resultType: 'Financial',
      title: 'Income tax return',
      summary: 'Customer-reviewed tax record for India: Income tax; paid 14000.00 USD; tax year 2025-26. This is not assessed tax liability, an accepted filing, payment confirmation, refund entitlement or tax advice.',
      authoritativeOwner: 'Financial Profile Service',
      representationState: 'DERIVED_REBUILDABLE',
      score: 1,
      source: {
        sourceObjectType: 'TaxRecord',
        sourceObjectId: 'tax-record-001',
        sourceReferenceId: 'ff-tax',
        provenanceReferenceId: 'provenance-tax',
        calculationLineageReferenceId: null,
      },
    }],
  });
  mount(api);

  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'income tax');
  await user.click(screen.getByRole('button', { name: 'Search' }));

  expect(await screen.findByText('1 result for “income tax”')).toBeVisible();
  expect(screen.getByText(/not assessed tax liability/i)).toBeVisible();
  expect(screen.getByRole('link', { name: 'View source & history' })).toHaveAttribute(
    'href',
    '/finances?type=TAX&taxRecord=tax-record-001',
  );
  expect(screen.queryByText(/accepted by a tax authority|refund approved/i)).not.toBeInTheDocument();
});

it('lets the customer bound the query to Financial information', async () => {
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product').mockResolvedValue({
    query: 'rent',
    matchMode: 'Lexical',
    items: [],
  });
  mount(api);

  const user = userEvent.setup();
  await user.click(screen.getByText('Financial information', { selector: 'span' }));
  await user.type(screen.getByLabelText('Search query'), 'rent');
  await user.click(screen.getByRole('button', { name: 'Search' }));

  expect(await screen.findByText('No trusted financial information matched this query.')).toBeVisible();
  expect(product).toHaveBeenCalledWith(
    'v1/search',
    'POST',
    { query: 'rent', scope: 'financial' },
    expect.any(AbortSignal),
  );
});

it('does not expose a search action before the Customer role receives Search access', () => {
  const api = new AccessApi(session);
  mount(api, ['financial-profile.profile.read']);

  expect(screen.getByText("Search isn't included in your current access")).toBeVisible();
  expect(screen.getByLabelText('Search query')).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Search' })).toBeDisabled();
});

it('preserves Document search and exposes canonical Financial information Request Access', async () => {
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product').mockResolvedValue({ query: 'statement', matchMode: 'Lexical', items: [] });
  vi.spyOn(api, 'request').mockResolvedValue({
    tenantId: 'T001', actorId: 'A100', policyVersion: 8, subjectVersion: 2,
    items: [{ requestId: null, target: 'customer.financial-information', targetLabel: 'Financial information',
      grantExplanation: 'Allows this customer to view and update their financial information.',
      state: 'NotRequested', hasAccess: false, canRequest: true, createdAt: null, decisionAt: null,
      fulfilledAt: null, decisionReason: null }],
  });
  mount(api, ['search.query.execute', 'evidence.document.read']);

  expect(screen.getByText('Financial search access required')).toBeVisible();
  expect(await screen.findByRole('button', { name: 'Request access' })).toBeVisible();
  expect(screen.getByText('All', { selector: 'span' }).closest('label')).toHaveClass('ant-radio-button-wrapper-disabled');
  expect(screen.getByText('Financial information', { selector: 'span' }).closest('label')).toHaveClass('ant-radio-button-wrapper-disabled');
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'statement');
  await user.click(screen.getByRole('button', { name: 'Search' }));
  expect(product).toHaveBeenCalledWith('v1/search', 'POST', { query: 'statement', scope: 'documents' }, expect.any(AbortSignal));
});

it('preserves Financial-information search and exposes canonical Documents Request Access', async () => {
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product').mockResolvedValue({ query: 'income', matchMode: 'Lexical', items: [] });
  vi.spyOn(api, 'request').mockResolvedValue({
    tenantId: 'T001', actorId: 'A100', policyVersion: 8, subjectVersion: 2,
    items: [{ requestId: null, target: 'customer.documents', targetLabel: 'Documents',
      grantExplanation: 'Allows this customer to view and add their Documents.',
      state: 'NotRequested', hasAccess: false, canRequest: true, createdAt: null, decisionAt: null,
      fulfilledAt: null, decisionReason: null }],
  });
  mount(api, ['search.query.execute', 'financial-profile.profile.read']);

  expect(screen.getByText('Document search access required')).toBeVisible();
  expect(await screen.findByRole('button', { name: 'Request access' })).toBeVisible();
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'income');
  await user.click(screen.getByRole('button', { name: 'Search' }));
  expect(product).toHaveBeenCalledWith('v1/search', 'POST', { query: 'income', scope: 'financial' }, expect.any(AbortSignal));
});

it('does not call the protected Search route when neither source permission is current', () => {
  const api = new AccessApi(session);
  const product = vi.spyOn(api, 'product');
  vi.spyOn(api, 'request').mockResolvedValue({
    tenantId: 'T001', actorId: 'A100', policyVersion: 8, subjectVersion: 2,
    items: [
      { requestId: null, target: 'customer.documents', targetLabel: 'Documents', grantExplanation: 'Allows Documents.',
        state: 'NotRequested', hasAccess: false, canRequest: true, createdAt: null, decisionAt: null, fulfilledAt: null, decisionReason: null },
      { requestId: null, target: 'customer.financial-information', targetLabel: 'Financial information', grantExplanation: 'Allows financial information.',
        state: 'NotRequested', hasAccess: false, canRequest: true, createdAt: null, decisionAt: null, fulfilledAt: null, decisionReason: null },
    ],
  });
  mount(api, ['search.query.execute']);

  expect(screen.getByText('Document search access required')).toBeVisible();
  expect(screen.getByText('Financial search access required')).toBeVisible();
  expect(screen.getByLabelText('Search query')).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Search' })).toBeDisabled();
  expect(product).not.toHaveBeenCalled();
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
  });
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


it('finds Asset source figures and links to exact trusted fact without verified valuation claims', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    query: 'asset',
    matchMode: 'Lexical',
    items: [{
      searchRecordId: 'search-financial-ff-asset',
      resultType: 'Financial',
      title: 'Document-stated asset figure',
      summary: 'Asset source figure (not verified valuation) 900000 USD effective 2026-10-01',
      authoritativeOwner: 'Financial Profile Service',
      representationState: 'DERIVED_REBUILDABLE',
      score: 1,
      source: {
        sourceObjectType: 'FinancialFact',
        sourceObjectId: 'ff-asset',
        sourceReferenceId: 'financial-profile-reference-customer',
        provenanceReferenceId: 'provenance-asset',
        calculationLineageReferenceId: null,
      },
    }],
  });
  mount(api);
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'asset');
  await user.click(screen.getByRole('button', { name: 'Search' }));
  expect(await screen.findByText('1 result for “asset”')).toBeVisible();
  expect(screen.getByText(/not verified valuation/i)).toBeVisible();
  expect(screen.getByRole('link', { name: 'View source & history' })).toHaveAttribute('href', '/finances?fact=ff-asset');
  expect(screen.queryByText(/verified net worth|certified asset/i)).not.toBeInTheDocument();
});


it('finds a reviewed Investment source amount with exact source history but no live market-value claim', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    query: 'investment',
    matchMode: 'Lexical',
    items: [{
      searchRecordId: 'search-financial-ff-investment',
      resultType: 'Financial',
      title: 'Document-stated investment figure',
      summary: 'Investment source figure (not live market value) 500 USD effective 2026-10-01',
      authoritativeOwner: 'Financial Profile Service',
      representationState: 'DERIVED_REBUILDABLE',
      score: 1,
      source: {
        sourceObjectType: 'FinancialFact',
        sourceObjectId: 'ff-investment',
        sourceReferenceId: 'financial-profile-reference-customer',
        provenanceReferenceId: 'provenance-investment',
        calculationLineageReferenceId: null,
      },
    }],
  });
  mount(api);
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'investment');
  await user.click(screen.getByRole('button', { name: 'Search' }));
  expect(await screen.findByText('1 result for “investment”')).toBeVisible();
  expect(screen.getByText(/not live market value/i)).toBeVisible();
  expect(screen.getByRole('link', { name: 'View source & history' })).toHaveAttribute('href', '/finances?fact=ff-investment');
  expect(within(screen.getByRole('article')).queryByText(/portfolio return|NAV|verified current price/i)).not.toBeInTheDocument();
});


it('finds Insurance source figures without asserting verified policy coverage', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    query: 'insurance',
    matchMode: 'Lexical',
    items: [{
      searchRecordId: 'search-financial-ff-insurance',
      resultType: 'Financial',
      title: 'Document-stated insurance figure',
      summary: 'Insurance source figure (not verified coverage or claim benefit) 100 USD effective 2026-10-01',
      authoritativeOwner: 'Financial Profile Service',
      representationState: 'DERIVED_REBUILDABLE',
      score: 1,
      source: {
        sourceObjectType: 'FinancialFact',
        sourceObjectId: 'ff-insurance',
        sourceReferenceId: 'financial-profile-reference-customer',
        provenanceReferenceId: 'provenance-insurance',
        calculationLineageReferenceId: null,
      },
    }],
  });
  mount(api);
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'insurance');
  await user.click(screen.getByRole('button', { name: 'Search' }));
  expect(await screen.findByText('1 result for “insurance”')).toBeVisible();
  expect(screen.getByText(/not verified coverage or claim benefit/i)).toBeVisible();
  expect(screen.getByRole('link', { name: 'View source & history' })).toHaveAttribute('href', '/finances?fact=ff-insurance');
  expect(screen.queryByText(/claim approved|policy benefit certified/i)).not.toBeInTheDocument();
});


it('searches a trusted Tax source figure without claiming assessed tax or refund', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    query: 'tax',
    matchMode: 'Lexical',
    items: [{
      searchRecordId: 'search-financial-ff-tax',
      resultType: 'Financial',
      title: 'Tax source figure',
      summary: 'Tax source figure (not assessed liability or refund) 14000 USD effective 2026-10-01',
      authoritativeOwner: 'Financial Profile Service',
      representationState: 'DERIVED_REBUILDABLE',
      score: 1,
      source: {
        sourceObjectType: 'FinancialFact',
        sourceObjectId: 'ff-tax',
        sourceReferenceId: 'financial-profile-reference-customer',
        provenanceReferenceId: 'provenance-tax',
        calculationLineageReferenceId: null,
      },
    }],
  });
  mount(api);
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'tax');
  await user.click(screen.getByRole('button', {name:'Search'}));
  expect(await screen.findByText('1 result for “tax”')).toBeVisible();
  expect(screen.getByText(/not assessed liability or refund/i)).toBeVisible();
  expect(screen.getByRole('link', {name:'View source & history'})).toHaveAttribute('href','/finances?fact=ff-tax');
});

it('offers natural-language retrieval only with all required source and AI permissions', () => {
  const api = new AccessApi(session);
  mount(api, ['search.query.execute', 'ai-intelligence.analysis.execute', 'financial-profile.profile.read', 'evidence.document.read']);
  expect(screen.getByRole('link', { name: 'Ask a financial question' })).toHaveAttribute('href', '/financial-qa');
  cleanup(); mount(api, ['search.query.execute']);
  expect(screen.queryByRole('link', { name: 'Ask a financial question' })).not.toBeInTheDocument();
});

it('aborts an old customer search and ignores its late response after a scope switch', async () => {
  const api = new AccessApi(session); let signal: AbortSignal | undefined;
  let finish: (value: unknown) => void = () => undefined;
  vi.spyOn(api, 'product').mockImplementation((_path, _method, _body, incoming) => {
    signal = incoming; return new Promise(resolve => { finish = resolve; });
  });
  const mounted = mount(api); const user = userEvent.setup();
  await user.type(screen.getByLabelText('Search query'), 'old private query');
  await user.click(screen.getByRole('button', { name: 'Search' }));
  mounted.rerender(element(new AccessApi(session), ['search.query.execute'], 'new-customer'));
  expect(signal?.aborted).toBe(true);
  await act(async () => { finish({ query: 'old private query', matchMode: 'Lexical', items: [] }); await Promise.resolve(); });
  expect(screen.queryByText(/results for/)).not.toBeInTheDocument();
  expect(screen.getByLabelText('Search query')).toHaveValue('');
});
