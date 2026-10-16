import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import ConsentExperience from '../src/product/ConsentExperience';

const session = {
  tenantId: 'T001',
  actorId: 'owner-001',
  authenticationContextId: 'customer-session',
  subjectVersion: 2,
  expiresAt: '2027-01-01T00:00:00Z',
};

function mount(api: AccessApi, customerId = 'customer-001') {
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
              actorId: 'owner-001',
              customerId,
              customerName: 'Current Customer',
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
        <ConsentExperience />
      </Context>
    </FoundationProvider>,
  );
}
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

it('shows delegated permissions and expiry/revocation without claiming connected providers', async () => {
  const api = new AccessApi(session);
  const consent = vi.spyOn(api, 'customerConsent').mockResolvedValue({
    version: 4,
    items: [
      { id: 'grant-active', customerId: 'customer-001', actorId: 'recipient-001', purpose: 'customer-advice', capabilityIds: ['financial-profile.profile.read'], expiresAt: '2099-01-01T00:00:00Z', revokedAt: null },
      { id: 'grant-expired', customerId: 'customer-001', actorId: 'recipient-002', purpose: 'customer-advice', capabilityIds: ['financial-profile.profile.read'], expiresAt: '2001-01-01T00:00:00Z', revokedAt: null },
      { id: 'grant-revoked', customerId: 'customer-001', actorId: 'recipient-003', purpose: 'customer-advice', capabilityIds: ['evidence.document.read'], expiresAt: '2099-01-01T00:00:00Z', revokedAt: '2026-10-01T00:00:00Z' },
    ],
  } as never);
  mount(api);
  expect(await screen.findByText('Showing 3 most recent recorded permissions.')).toBeVisible();
  const section = screen.getByRole('region', { name: 'Recorded sharing permissions' });
  expect(within(section).getByText('Active')).toBeVisible();
  expect(within(section).getByText('Expired')).toBeVisible();
  expect(within(section).getByText('Revoked')).toBeVisible();
  expect(consent).toHaveBeenCalledWith('customer-001', expect.any(AbortSignal));
  expect(screen.queryByText('recipient-001')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /revoke/i })).not.toBeInTheDocument();
  expect(screen.getByText(/Provider connection and refresh permissions are separate/i)).toBeVisible();
});

it('does not fabricate a zero state when consent owner is unavailable', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'customerConsent').mockRejectedValue(new Error('unavailable'));
  mount(api);
  expect(await screen.findByText('Sharing permissions unavailable')).toBeVisible();
  expect(screen.queryByText('No recorded delegated permissions')).not.toBeInTheDocument();
});

it('rejects other-customer records instead of displaying their permissions', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'customerConsent').mockResolvedValue({
    version: 1,
    items: [{ id: 'other-grant', customerId: 'other-customer', actorId: 'someone', purpose: 'customer-advice', capabilityIds: [], expiresAt: '2099-01-01T00:00:00Z', revokedAt: null }],
  } as never);
  mount(api);
  expect(await screen.findByText('Sharing permissions unavailable')).toBeVisible();
  expect(screen.queryByText('other-grant')).not.toBeInTheDocument();
});

it('distinguishes known empty consent history from missing owner data', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'customerConsent').mockResolvedValue({ version: 1, items: [] } as never);
  mount(api);
  expect(await screen.findByText('No recorded delegated permissions')).toBeVisible();
  await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh permissions' }));
  expect(api.customerConsent).toHaveBeenCalledTimes(2);
});
