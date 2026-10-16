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
  'job-management.job.read',
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
    if (path === 'v1/processing-activity')
      return Promise.resolve({ dataDurability: 'Temporary data', items: [] }) as never;
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
    if (path === 'v1/processing-activity')
      return Promise.resolve({ dataDurability: 'Temporary data', items: [] }) as never;
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
    if (path === 'v1/processing-activity')
      return Promise.resolve({ dataDurability: 'Temporary data', items: [] }) as never;
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
    if (path === 'v1/processing-activity')
      return Promise.resolve({ dataDurability: 'Temporary data', items: [] }) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api);

  expect(
    await screen.findByText('No attention items are visible in the currently loaded supported state'),
  ).toBeVisible();
  expect(
    screen.getByText(/This does not mean your overall finances are complete/),
  ).toBeVisible();
  expect(screen.getByText(/not a lifetime processing-history claim/)).toBeVisible();
  expect(screen.getByText('2 of 2')).toBeVisible();
  expect(screen.getByText('Recent processing attention').parentElement).toHaveTextContent('0');
});

it('does not present a clear state when only one bounded input is authorized', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/financial-profile') return Promise.resolve(profile([income, expense])) as never;
    if (path === 'v1/processing-activity')
      return Promise.resolve({ dataDurability: 'Temporary data', items: [] }) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api, ['financial-profile.profile.read']);

  expect(
    await screen.findByText('Current attention could not be fully determined'),
  ).toBeVisible();
  expect(
    screen.queryByText('No attention items are visible in the currently loaded supported state'),
  ).not.toBeInTheDocument();
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
    if (path === 'v1/processing-activity')
      return Promise.resolve({ dataDurability: 'Temporary data', items: [] }) as never;
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
    screen.queryByText('No attention items are visible in the currently loaded supported state'),
  ).not.toBeInTheDocument();
  expect(screen.getAllByText('—').length).toBeGreaterThanOrEqual(2);
});


it('aggregates durable processing attention without turning jobs into tasks', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          { documentVersionId: 'document-version-001' },
          { documentVersionId: 'document-version-002' },
        ],
      }) as never;
    if (path === 'v1/financial-profile') return Promise.resolve(profile([income, expense])) as never;
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-running',
            documentVersionId: 'document-version-001',
            state: 'Running',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
          {
            jobId: 'job-processing-scheduled',
            documentVersionId: 'document-version-002',
            state: 'Scheduled',
            attempt: 0,
            updatedAt: '2026-10-16T08:59:00Z',
          },
          {
            jobId: 'job-processing-failed',
            documentVersionId: 'document-version-003',
            state: 'Failed',
            attempt: 2,
            updatedAt: '2026-10-16T08:58:00Z',
          },
          {
            jobId: 'job-processing-completed',
            documentVersionId: 'document-version-004',
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T08:57:00Z',
          },
          {
            jobId: 'job-processing-cancelled',
            documentVersionId: 'document-version-005',
            state: 'Cancelled',
            attempt: 0,
            updatedAt: '2026-10-16T08:56:00Z',
          },
        ],
      }) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api);

  expect(await screen.findByText('2 documents are still being processed.')).toBeVisible();
  expect(screen.getByText("1 document processing run couldn't complete.")).toBeVisible();
  expect(screen.getByText('Recent processing attention').parentElement).toHaveTextContent('3');
  expect(screen.getByText('2 items from supported journeys')).toBeVisible();
  const links = screen.getAllByRole('link', { name: 'View processing activity' });
  expect(links).toHaveLength(2);
  expect(links[0]).toHaveAttribute('href', '/evidence');
  expect(screen.queryByText(/completed processing needs attention/i)).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument();
});

it('never turns unavailable processing activity into an all-clear Action Centre', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          { documentVersionId: 'document-version-001' },
          { documentVersionId: 'document-version-002' },
        ],
      }) as never;
    if (path === 'v1/financial-profile') return Promise.resolve(profile([income, expense])) as never;
    if (path === 'v1/processing-activity') return Promise.reject(new Error('job owner unavailable')) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api);

  expect(
    await screen.findByText("We couldn't refresh document processing attention."),
  ).toBeVisible();
  expect(screen.getByText('Current attention could not be fully determined')).toBeVisible();
  expect(screen.getByText('Recent processing attention').parentElement).toHaveTextContent('—');
  expect(
    screen.queryByText('No attention items are visible in the currently loaded supported state'),
  ).not.toBeInTheDocument();
});

it('does not infer processing all-clear when job read is outside the current access package', async () => {
  const api = new AccessApi(session);
  const request = vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          { documentVersionId: 'document-version-001' },
          { documentVersionId: 'document-version-002' },
        ],
      }) as never;
    if (path === 'v1/financial-profile') return Promise.resolve(profile([income, expense])) as never;
    throw new Error('unexpected endpoint');
  });

  mount(api, [
    'evidence.document.read',
    'evidence.document.upload',
    'financial-profile.profile.read',
  ]);

  expect(
    await screen.findByText("Recent processing attention isn't included in your current access"),
  ).toBeVisible();
  expect(screen.getByText('Current attention could not be fully determined')).toBeVisible();
  expect(
    request.mock.calls.some(([path]) => path === 'v1/processing-activity'),
  ).toBe(false);
});
