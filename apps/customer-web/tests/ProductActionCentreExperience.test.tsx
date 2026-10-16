import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import ProductActionCentreExperience from '../src/product/ProductActionCentreExperience';

const session = {
  tenantId: 'T001',
  actorId: 'A100',
  authenticationContextId: 'customer-session',
  subjectVersion: 2,
  expiresAt: '2027-01-01T00:00:00Z',
};

const capabilities = [
  'evidence.document.read',
  'evidence.document.upload',
  'financial-profile.profile.read',
];

function mount(api: AccessApi, granted = capabilities) {
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
              capabilities: granted,
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
        <ProductActionCentreExperience />
      </Context>
    </FoundationProvider>,
  );
}

const profile = (facts: unknown[]) => ({
  financialProfileId: 'financial-profile-reference-customer',
  revision: facts.length ? 1 : 0,
  updatedAt: facts.length ? '2026-10-16T09:00:00Z' : null,
  dataDurability: 'Temporary data',
  facts,
});

const income = {
  financialFactId: 'ff-income',
  factType: 'INCOME',
  label: 'Employment income',
  value: 125000,
  currency: 'USD',
  effectiveDate: '2026-10-01',
  revision: 1,
  source: {
    sourceName: 'income.txt',
    documentVersion: 'document-version-income',
    recordedAt: '2026-10-16T09:00:00Z',
    technicalSourceFactId: 'source-income',
    technicalProvenanceId: 'provenance-income',
  },
};

const expense = {
  financialFactId: 'ff-expense',
  factType: 'EXPENSE',
  label: 'Rent',
  value: 2500,
  currency: 'USD',
  effectiveDate: '2026-10-01',
  revision: 1,
  source: {
    sourceName: 'expense.txt',
    documentVersion: 'document-version-expense',
    recordedAt: '2026-10-16T08:00:00Z',
    technicalSourceFactId: 'source-expense',
    technicalProvenanceId: 'provenance-expense',
  },
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

it('shows a bounded add-document attention item when the customer has no Documents', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ dataDurability: 'Runtime only', items: [] }) as never;
    if (path === 'v1/financial-profile') return Promise.resolve(profile([])) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api);

  expect(await screen.findByText('No financial documents have been added yet.')).toBeVisible();
  expect(screen.getByRole('link', { name: 'Add document' })).toHaveAttribute('href', '/evidence');
  expect(screen.getByText('0 of 2')).toBeVisible();
  expect(screen.queryByText(/recommendation engine/i)).not.toBeInTheDocument();
});

it('shows review-documents attention without claiming extracted information is trusted', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Runtime only',
        items: [{ documentVersionId: 'document-version-001' }],
      }) as never;
    if (path === 'v1/financial-profile') return Promise.resolve(profile([])) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api);

  expect(
    await screen.findByText('Your Documents do not contain any trusted financial information yet.'),
  ).toBeVisible();
  expect(screen.getByRole('link', { name: 'Review Documents' })).toHaveAttribute('href', '/evidence');
  expect(screen.getByText(/Extracted information remains separate until you confirm it/)).toBeVisible();
});

it('shows only the missing supported category after trusted information exists', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Runtime only',
        items: [{ documentVersionId: 'document-version-001' }],
      }) as never;
    if (path === 'v1/financial-profile') return Promise.resolve(profile([income])) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api);

  expect(
    await screen.findByText('No trusted Expense information is currently represented.'),
  ).toBeVisible();
  expect(screen.queryByText('No trusted Income information is currently represented.')).not.toBeInTheDocument();
  expect(screen.getByText('1 of 2')).toBeVisible();
});

it('shows a truthful clear state when both currently supported categories are represented', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Runtime only',
        items: [
          { documentVersionId: 'document-version-001' },
          { documentVersionId: 'document-version-002' },
        ],
      }) as never;
    if (path === 'v1/financial-profile') return Promise.resolve(profile([income, expense])) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api);

  expect(
    await screen.findByText('Nothing currently needs attention in the supported journeys'),
  ).toBeVisible();
  expect(
    screen.getByText(/This does not mean your overall finances are complete/),
  ).toBeVisible();
  expect(screen.getByText('2 of 2')).toBeVisible();
});

it('never turns an unavailable Financial Profile into a false zero-information attention item', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Runtime only',
        items: [{ documentVersionId: 'document-version-001' }],
      }) as never;
    if (path === 'v1/financial-profile') return Promise.reject(new Error('owner unavailable')) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api);

  expect(
    await screen.findByText("We couldn't refresh your trusted financial information."),
  ).toBeVisible();
  expect(
    screen.queryByText('Your Documents do not contain any trusted financial information yet.'),
  ).not.toBeInTheDocument();
  expect(screen.getByText('Current attention could not be fully determined')).toBeVisible();
  expect(screen.getByText('Some current information is unavailable.')).toBeVisible();
  expect(
    screen.queryByText('Nothing currently needs attention in the supported journeys'),
  ).not.toBeInTheDocument();
  expect(screen.getAllByText('—').length).toBeGreaterThanOrEqual(2);
});
