import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import FinancialProfileExperience from '../src/product/FinancialProfileExperience';

const session = {
  tenantId: 'T001',
  actorId: 'A100',
  authenticationContextId: 'customer-session',
  subjectVersion: 2,
  expiresAt: '2027-01-01T00:00:00Z',
};

function mount(api: AccessApi) {
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
              capabilities: ['financial-profile.profile.read'],
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
        <FinancialProfileExperience />
      </Context>
    </FoundationProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  window.history.replaceState(null, '', '/');
});

it('shows trusted financial information with source history and hides raw ids by default', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 2,
    updatedAt: '2026-10-15T12:00:00Z',
    dataDurability: 'Temporary data',
    facts: [
      {
        financialFactId: 'ff-income',
        factType: 'INCOME',
        label: 'Employment income',
        value: 125000,
        currency: 'USD',
        effectiveDate: '2026-10-01',
        revision: 2,
        source: {
          sourceName: 'customer-statement.txt',
          documentVersion: 'document-version-001',
          recordedAt: '2026-10-15T12:00:00Z',
          technicalSourceFactId: 'source-fact-001',
          technicalProvenanceId: 'provenance-base-001',
        },
      },
    ],
  });

  mount(api);

  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(screen.getByText('$125,000.00')).toBeVisible();
  expect(screen.getByText('Source: customer-statement.txt')).toBeVisible();
  expect(screen.getByText('Current revision')).toBeVisible();
  expect(screen.queryByText('ff-income')).not.toBeInTheDocument();

  await userEvent.setup().click(screen.getByText('Technical details'));
  expect(screen.getByText('ff-income')).toBeVisible();
  expect(screen.getByText('document-version-001')).toBeVisible();
  expect(screen.getByText('source-fact-001')).toBeVisible();
});

it('opens Passport category deep links and switches between supported financial categories', async () => {
  window.history.replaceState(null, '', '/finances?type=EXPENSE');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 2,
    updatedAt: '2026-10-15T12:00:00Z',
    dataDurability: 'Temporary data',
    facts: [
      {
        financialFactId: 'ff-income',
        factType: 'INCOME',
        label: 'Employment income',
        value: 125000,
        currency: 'USD',
        effectiveDate: '2026-10-01',
        revision: 1,
        source: {
          sourceName: 'income-statement.txt',
          documentVersion: 'document-version-001',
          recordedAt: '2026-10-15T12:00:00Z',
          technicalSourceFactId: 'source-fact-income',
          technicalProvenanceId: 'provenance-income',
        },
      },
      {
        financialFactId: 'ff-expense',
        factType: 'EXPENSE',
        label: 'Rent',
        value: 2500,
        currency: 'USD',
        effectiveDate: '2026-10-01',
        revision: 1,
        source: {
          sourceName: 'expense-statement.txt',
          documentVersion: 'document-version-002',
          recordedAt: '2026-10-14T12:00:00Z',
          technicalSourceFactId: 'source-fact-expense',
          technicalProvenanceId: 'provenance-expense',
        },
      },
    ],
  });

  mount(api);

  expect(await screen.findByText('Rent')).toBeVisible();
  expect(screen.queryByText('Employment income')).not.toBeInTheDocument();

  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Income' }));
  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(screen.queryByText('Rent')).not.toBeInTheDocument();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=INCOME');

  await user.click(screen.getByRole('button', { name: 'All' }));
  expect(screen.getByText('Employment income')).toBeVisible();
  expect(screen.getByText('Rent')).toBeVisible();
  expect(window.location.pathname + window.location.search).toBe('/finances');
});

it('shows a truthful empty state for a Passport category with no trusted items', async () => {
  window.history.replaceState(null, '', '/finances?type=EXPENSE');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 1,
    updatedAt: '2026-10-15T12:00:00Z',
    dataDurability: 'Temporary data',
    facts: [
      {
        financialFactId: 'ff-income',
        factType: 'INCOME',
        label: 'Employment income',
        value: 125000,
        currency: 'USD',
        effectiveDate: '2026-10-01',
        revision: 1,
        source: {
          sourceName: 'income-statement.txt',
          documentVersion: 'document-version-001',
          recordedAt: '2026-10-15T12:00:00Z',
          technicalSourceFactId: 'source-fact-income',
          technicalProvenanceId: 'provenance-income',
        },
      },
    ],
  });

  mount(api);

  expect(
    await screen.findByRole('heading', { name: 'No trusted expense information yet.' }),
  ).toBeVisible();
  expect(screen.queryByText('Employment income')).not.toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Review documents' })).toHaveAttribute('href', '/evidence');
  expect(screen.getByRole('button', { name: 'Show all financial information' })).toBeVisible();
});

it('opens a bounded trusted-fact drill-down with revision and source history', async () => {
  const api = new AccessApi(session);
  const current = {
    financialFactId: 'ff-income',
    factType: 'INCOME',
    label: 'Employment income',
    value: 125000,
    currency: 'USD',
    effectiveDate: '2026-10-01',
    revision: 2,
    source: {
      sourceName: 'income-current.txt',
      documentVersion: 'document-version-002',
      recordedAt: '2026-10-15T12:00:00Z',
      technicalSourceFactId: 'source-fact-002',
      technicalProvenanceId: 'provenance-002',
    },
  };
  const revisionOne = {
    ...current,
    value: 120000,
    revision: 1,
    source: {
      sourceName: 'income-original.txt',
      documentVersion: 'document-version-001',
      recordedAt: '2026-10-14T12:00:00Z',
      technicalSourceFactId: 'source-fact-001',
      technicalProvenanceId: 'provenance-001',
    },
  };

  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 2,
        updatedAt: current.source.recordedAt,
        dataDurability: 'Temporary data',
        facts: [current],
      }) as never;
    if (path === 'v1/financial-profile/facts/ff-income')
      return Promise.resolve({ current, history: [current, revisionOne] }) as never;
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api);
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'View source & history' }));

  const drawer = await screen.findByRole('dialog', { name: 'Financial information details' });
  expect(drawer).toHaveTextContent('Employment income');
  expect(drawer).toHaveTextContent('$125,000.00');
  expect(drawer).toHaveTextContent('Revision 2');
  expect(drawer).toHaveTextContent('Revision 1');
  expect(drawer).toHaveTextContent('$120,000.00');
  expect(drawer).toHaveTextContent('income-current.txt');
  expect(drawer).toHaveTextContent('income-original.txt');

  const sourceLinks = within(drawer).getAllByRole('link', { name: 'View this source document' });
  expect(sourceLinks[0]).toHaveAttribute(
    'href',
    '/evidence?version=document-version-002',
  );
  expect(sourceLinks[1]).toHaveAttribute(
    'href',
    '/evidence?version=document-version-001',
  );

  await user.click(within(drawer).getByText('Technical details'));
  expect(drawer).toHaveTextContent('provenance-002');
  expect(drawer).toHaveTextContent('source-fact-001');
  expect(drawer).toHaveTextContent('document-version-001');
});

it('opens a Passport deep link directly to the requested trusted fact', async () => {
  window.history.replaceState(null, '', '/finances?fact=ff-income');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    const fact = {
      financialFactId: 'ff-income',
      factType: 'INCOME',
      label: 'Employment income',
      value: 125000,
      currency: 'USD',
      effectiveDate: '2026-10-01',
      revision: 1,
      source: {
        sourceName: 'income-statement.txt',
        documentVersion: 'document-version-001',
        recordedAt: '2026-10-15T12:00:00Z',
        technicalSourceFactId: 'source-fact-001',
        technicalProvenanceId: 'provenance-001',
      },
    };
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 1,
        updatedAt: fact.source.recordedAt,
        dataDurability: 'Temporary data',
        facts: [fact],
      }) as never;
    if (path === 'v1/financial-profile/facts/ff-income')
      return Promise.resolve({ current: fact, history: [fact] }) as never;
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api);

  const drawer = await screen.findByRole('dialog', { name: 'Financial information details' });
  expect(drawer).toHaveTextContent('Employment income');
  expect(drawer).toHaveTextContent('1 revision');
});

it('shows explicitly reviewed Loan facts with exact source provenance, not invented loan totals', async () => {
  window.history.replaceState(null, '', '/finances?type=LOAN');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 3,
    updatedAt: '2026-10-17T06:00:00Z',
    dataDurability: 'Temporary data',
    facts: [{
      financialFactId: 'ff-loan-source',
      factType: 'LOAN',
      label: 'Car loan principal stated in document',
      value: 400000,
      currency: 'INR',
      effectiveDate: '2026-10-16',
      revision: 1,
      source: {
        sourceName: 'loan-statement.txt',
        documentVersion: 'document-version-loan',
        recordedAt: '2026-10-17T06:00:00Z',
        technicalSourceFactId: 'source-fact-loan',
        technicalProvenanceId: 'provenance-loan',
      },
    }],
  });
  mount(api);
  expect(await screen.findByText('Car loan principal stated in document')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Loan information' })).toBeVisible();
  expect(screen.getByText('Source: loan-statement.txt')).toBeVisible();
  expect(screen.queryByText(/Total loan outstanding/i)).not.toBeInTheDocument();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=LOAN');
  await userEvent.setup().click(screen.getByRole('button', { name: 'Expenses' }));
  expect(await screen.findByRole('heading', { name: 'No trusted expense information yet.' })).toBeVisible();
  expect(screen.queryByText('Car loan principal stated in document')).not.toBeInTheDocument();
});

it('shows reviewed Asset source figures with exact source, not a verified valuation', async () => {
  window.history.replaceState(null, '', '/finances?type=ASSET');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 4,
    updatedAt: '2026-10-17T06:30:00Z',
    dataDurability: 'Temporary data',
    facts: [{
      financialFactId: 'ff-asset-source',
      factType: 'ASSET',
      label: 'Property value stated in document',
      value: 900000,
      currency: 'INR',
      effectiveDate: '2026-10-16',
      revision: 1,
      source: {
        sourceName: 'property-source.txt',
        documentVersion: 'document-version-asset',
        recordedAt: '2026-10-17T06:30:00Z',
        technicalSourceFactId: 'source-fact-asset',
        technicalProvenanceId: 'provenance-asset',
      },
    }],
  });
  mount(api);
  expect(await screen.findByText('Property value stated in document')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Asset source figures' })).toBeVisible();
  expect(screen.getByText('Source: property-source.txt')).toBeVisible();
  expect(screen.queryByText(/Total net worth|Verified valuation/i)).not.toBeInTheDocument();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=ASSET');
});

it('shows reviewed Investment source figures with exact source, not a market valuation', async () => {
  window.history.replaceState(null, '', '/finances?type=INVESTMENT');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 5,
    updatedAt: '2026-10-17T06:45:00Z',
    dataDurability: 'Temporary data',
    facts: [{
      financialFactId: 'ff-investment-source',
      factType: 'INVESTMENT',
      label: 'Investment amount stated in source document',
      value: 50000,
      currency: 'INR',
      effectiveDate: '2026-10-16',
      revision: 1,
      source: {
        sourceName: 'investment-source.txt',
        documentVersion: 'document-version-investment',
        recordedAt: '2026-10-17T06:45:00Z',
        technicalSourceFactId: 'source-fact-investment',
        technicalProvenanceId: 'provenance-investment',
      },
    }],
  });
  mount(api);
  expect(await screen.findByText('Investment amount stated in source document')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Investment source figures' })).toBeVisible();
  expect(screen.getByText('Source: investment-source.txt')).toBeVisible();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=INVESTMENT');
  expect(screen.queryByText(/verified NAV|live market value|portfolio return/i)).not.toBeInTheDocument();
});
