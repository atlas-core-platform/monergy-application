import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, render, screen, within } from '@testing-library/react';
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
  const view = (currentApi: AccessApi, currentCustomer: string) => (
    <FoundationProvider appearance="midnight">
      <Context
        value={{
          status: 'authorized',
          session,
          api: currentApi,
          authorization: {
            area: 'product',
            context: {
              tenantId: 'T001',
              actorId: 'owner-001',
              customerId: currentCustomer,
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
    </FoundationProvider>
  );
  const mounted = render(view(api, customerId));
  return {
    ...mounted,
    changeCustomer: (nextApi: AccessApi, nextCustomer: string) => {
      mounted.rerender(view(nextApi, nextCustomer));
    },
  };
}
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

it('shows recorded permissions and expiry/revocation without claiming effective access or connected providers', async () => {
  const api = new AccessApi(session);
  const consent = vi.spyOn(api, 'customerConsent').mockResolvedValue({
    version: 4,
    items: [
      {
        id: 'grant-active',
        customerId: 'customer-001',
        actorId: 'recipient-001',
        purpose: 'customer-advice',
        capabilityIds: ['financial-profile.profile.read'],
        expiresAt: '2099-01-01T00:00:00Z',
        revokedAt: null,
      },
      {
        id: 'grant-expired',
        customerId: 'customer-001',
        actorId: 'recipient-002',
        purpose: 'customer-advice',
        capabilityIds: ['financial-profile.profile.read'],
        expiresAt: '2001-01-01T00:00:00Z',
        revokedAt: null,
      },
      {
        id: 'grant-revoked',
        customerId: 'customer-001',
        actorId: 'recipient-003',
        purpose: 'customer-advice',
        capabilityIds: ['evidence.document.read'],
        expiresAt: '2099-01-01T00:00:00Z',
        revokedAt: '2026-10-01T00:00:00Z',
      },
    ],
  });
  mount(api);
  expect(await screen.findByText('Showing 3 most recent recorded permissions.')).toBeVisible();
  const section = screen.getByRole('region', { name: 'Recorded sharing permissions' });
  expect(within(section).getByText('Unexpired record')).toBeVisible();
  expect(within(section).getByText('Expired')).toBeVisible();
  expect(within(section).getByText('Revoked')).toBeVisible();
  expect(consent).toHaveBeenCalledWith('customer-001', expect.any(AbortSignal));
  expect(screen.queryByText('recipient-001')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /revoke/i })).not.toBeInTheDocument();
  expect(screen.getByText(/Unexpired does not confirm effective access/i)).toBeVisible();
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
    items: [
      {
        id: 'other-grant',
        customerId: 'other-customer',
        actorId: 'someone',
        purpose: 'customer-advice',
        capabilityIds: [],
        expiresAt: '2099-01-01T00:00:00Z',
        revokedAt: null,
      },
    ],
  });
  mount(api);
  expect(await screen.findByText('Sharing permissions unavailable')).toBeVisible();
  expect(screen.queryByText('other-grant')).not.toBeInTheDocument();
});

it('distinguishes known empty consent history from missing owner data', async () => {
  const api = new AccessApi(session);
  const consent = vi.spyOn(api, 'customerConsent').mockResolvedValue({ version: 1, items: [] });
  mount(api);
  expect(await screen.findByText('No recorded delegated permissions')).toBeVisible();
  await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh permissions' }));
  expect(consent).toHaveBeenCalledTimes(2);
});

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<T>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}

const priorOverview = {
  version: 1,
  items: [
    {
      id: 'old-grant',
      customerId: 'customer-001',
      actorId: 'prior-recipient',
      purpose: 'customer-advice',
      capabilityIds: ['financial-profile.profile.read'],
      expiresAt: '2099-01-01T00:00:00Z',
      revokedAt: null,
    },
  ],
};

it.each(['resolve', 'reject'] as const)(
  'ignores a late manual refresh %s after changing the customer even if transport ignores abort',
  async (outcome) => {
    const api = new AccessApi(session);
    const late = deferred<typeof priorOverview>();
    const next = deferred<{ version: number; items: never[] }>();
    const consent = vi
      .spyOn(api, 'customerConsent')
      .mockResolvedValueOnce(priorOverview)
      .mockImplementationOnce(() => late.promise)
      .mockImplementationOnce(() => next.promise);
    const mounted = mount(api);
    expect(await screen.findByText('Unexpired record')).toBeVisible();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh permissions' }));
    const previousSignal = consent.mock.calls.at(1)?.[1];
    mounted.changeCustomer(api, 'customer-002');
    expect(previousSignal?.aborted).toBe(true);
    await act(async () => {
      if (outcome === 'resolve') late.resolve(priorOverview);
      else late.reject(new Error('old request failed'));
      await late.promise.catch(() => undefined);
    });
    expect(screen.queryByText('Unexpired record')).not.toBeInTheDocument();
    expect(screen.queryByText('Sharing permissions unavailable')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh permissions' })).toBeDisabled();
    await act(async () => {
      next.resolve({ version: 2, items: [] });
      await next.promise;
    });
    expect(await screen.findByText('No recorded delegated permissions')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Refresh permissions' })).toBeEnabled();
  },
);

it('discards pending manual refresh when the API session changes for the same customer', async () => {
  const oldApi = new AccessApi(session);
  const newApi = new AccessApi({ ...session, authenticationContextId: 'new-session' });
  const late = deferred<typeof priorOverview>();
  const consent = vi
    .spyOn(oldApi, 'customerConsent')
    .mockResolvedValueOnce(priorOverview)
    .mockImplementationOnce(() => late.promise);
  vi.spyOn(newApi, 'customerConsent').mockResolvedValue({ version: 2, items: [] });
  const mounted = mount(oldApi);
  await screen.findByText('Unexpired record');
  await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh permissions' }));
  mounted.changeCustomer(newApi, 'customer-001');
  await screen.findByText('No recorded delegated permissions');
  expect(consent.mock.calls.at(1)?.[1]?.aborted).toBe(true);
  await act(async () => {
    late.resolve(priorOverview);
    await late.promise;
  });
  expect(screen.queryByText('Unexpired record')).not.toBeInTheDocument();
  expect(screen.getByText('No recorded delegated permissions')).toBeVisible();
});

it('aborts a manual refresh when leaving the consent view', async () => {
  const api = new AccessApi(session);
  const late = deferred<typeof priorOverview>();
  const consent = vi
    .spyOn(api, 'customerConsent')
    .mockResolvedValueOnce(priorOverview)
    .mockImplementationOnce(() => late.promise);
  const mounted = mount(api);
  await screen.findByText('Unexpired record');
  await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh permissions' }));
  mounted.unmount();
  expect(consent.mock.calls.at(1)?.[1]?.aborted).toBe(true);
  await act(async () => {
    late.resolve(priorOverview);
    await late.promise;
  });
});
