import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import WorkspaceHome from '../src/workspace/WorkspaceHome';

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
              capabilities: [
                'evidence.document.read',
                'evidence.document.upload',
                'financial-profile.profile.read',
              ],
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
        <WorkspaceHome />
      </Context>
    </FoundationProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

it('shows a source-connected Financial Passport without inventing financial scores or advice', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/financial-profile')
      return Promise.resolve({
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
              sourceName: 'income-statement.txt',
              documentVersion: 'document-version-001',
              recordedAt: '2026-10-15T12:00:00Z',
              technicalSourceFactId: 'source-fact-001',
              technicalProvenanceId: 'provenance-income-001',
            },
          },
          {
            financialFactId: 'ff-expense',
            factType: 'EXPENSE',
            label: 'Rent',
            value: 2500,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            revision: 2,
            source: {
              sourceName: 'expense-statement.txt',
              documentVersion: 'document-version-002',
              recordedAt: '2026-10-14T12:00:00Z',
              technicalSourceFactId: 'source-fact-002',
              technicalProvenanceId: 'provenance-expense-002',
            },
          },
        ],
      }) as never;
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Runtime only',
        items: [
          { documentVersionId: 'document-version-001' },
          { documentVersionId: 'document-version-002' },
          { documentVersionId: 'document-version-003' },
        ],
      }) as never;
    throw new Error('unexpected product endpoint');
  });

  mount(api);

  expect(await screen.findByRole('heading', { name: 'Your source-connected snapshot' })).toBeVisible();
  expect(screen.getByRole('link', { name: /Open Action Centre/ })).toHaveAttribute('href', '/actions');
  expect(screen.getByText('Trusted information')).toBeVisible();
  expect(screen.getByText('Income items')).toBeVisible();
  expect(screen.getByText('Expense items')).toBeVisible();
  expect(screen.getByText('Employment income')).toBeVisible();
  expect(screen.getByText('income-statement.txt')).toBeVisible();
  expect(screen.getByText('Employment income').closest('a')).toHaveAttribute(
    'href',
    '/finances?fact=ff-income',
  );
  expect(screen.getByText('2 source documents')).toBeVisible();
  expect(screen.getByText(/2 of 2 currently supported categories represented/)).toBeVisible();
  expect(screen.getByText('Last trusted update 15 Oct 2026')).toBeVisible();
  const coverage = screen.getByLabelText('Financial Passport current coverage');
  expect(within(coverage).getByRole('link', { name: 'View Income financial information' })).toHaveAttribute(
    'href',
    '/finances?type=INCOME',
  );
  expect(within(coverage).getByRole('link', { name: 'View Income financial information' })).toHaveTextContent(
    '1 trusted item',
  );
  expect(within(coverage).getByRole('link', { name: 'View Expenses financial information' })).toHaveAttribute(
    'href',
    '/finances?type=EXPENSE',
  );
  expect(within(coverage).getByRole('link', { name: 'View Expenses financial information' })).toHaveTextContent(
    '1 trusted item',
  );
  expect(
    screen.getByText(/Coverage is limited to the Income and Expense information Monergy currently supports/),
  ).toBeVisible();
  expect(screen.queryByRole('heading', { name: /financial health score/i })).not.toBeInTheDocument();
  expect(screen.queryByText(/recommendation/i)).not.toBeInTheDocument();
});
