import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
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
        },
      },
    ],
  });

  mount(api);

  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(screen.getByText('$125,000.00')).toBeVisible();
  expect(screen.getByText('Source: customer-statement.txt')).toBeVisible();
  expect(screen.getByRole('link', { name: 'Open source document customer-statement.txt' })).toHaveAttribute(
    'href',
    '/evidence?documentVersion=document-version-001',
  );
  expect(screen.getByText('Current revision')).toBeVisible();
  expect(screen.queryByText('ff-income')).not.toBeInTheDocument();

  await userEvent.setup().click(screen.getByText('Technical details'));
  expect(screen.getByText('ff-income')).toBeVisible();
  expect(screen.getByText('document-version-001')).toBeVisible();
  expect(screen.getByText('source-fact-001')).toBeVisible();
});
