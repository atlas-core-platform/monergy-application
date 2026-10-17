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


it('requires recognition of exact recipient and grant before owner-backed revocation', async () => {
  const api = new AccessApi(session);
  const before = {
    version: 4,
    items: [{
      id: 'grant-001', customerId: 'customer-001', actorId: 'recipient-001',
      purpose: 'customer-advice', capabilityIds: ['financial-profile.profile.read'],
      expiresAt: '2099-01-01T00:00:00Z', revokedAt: null,
    }],
  };
  const after = {
    version: 5,
    items: [{ ...before.items[0], revokedAt: '2026-10-17T07:00:00Z' }],
  };
  const list = vi.spyOn(api, 'customerConsent')
    .mockResolvedValueOnce(before)
    .mockResolvedValueOnce(after);
  const revoked = vi.spyOn(api, 'revokeCustomerConsent').mockResolvedValue({
    id: 'grant-001', customerId: 'customer-001', actorId: 'recipient-001',
    operation: 'Revoked', version: 5, evidenceReference: 'consent-evidence-001',
  });
  mount(api);
  const user = userEvent.setup();
  await screen.findByText('Unexpired record');
  await user.click(screen.getByRole('button', { name: 'Stop sharing' }));
  const dialog = await screen.findByRole('dialog', { name: 'Confirm stopping a sharing permission' });
  expect(within(dialog).getByText(/recipient-001/)).toBeVisible();
  expect(within(dialog).getByText(/grant-001/)).toBeVisible();
  const confirm = within(dialog).getByRole('button', { name: 'Stop sharing' });
  expect(confirm).toBeDisabled();
  expect(revoked).not.toHaveBeenCalled();
  await user.click(within(dialog).getByRole('checkbox', { name: /I recognize this recipient reference/i }));
  expect(confirm).toBeEnabled();
  await user.click(confirm);
  expect(await screen.findByText('Revoked')).toBeVisible();
  expect(revoked).toHaveBeenCalledWith(
    'customer-001', 'grant-001', 4, expect.stringMatching(/^[a-f0-9]{32}$/),
    expect.any(AbortSignal),
  );
  expect(list).toHaveBeenCalledTimes(2);
  expect(screen.queryByRole('button', { name: 'Stop sharing' })).not.toBeInTheDocument();
});

it('does not infer successful revocation after an unavailable or conflicting owner response', async () => {
  const api = new AccessApi(session);
  const list = vi.spyOn(api, 'customerConsent').mockResolvedValue({
    version: 7,
    items: [{
      id: 'grant-conflict', customerId: 'customer-001', actorId: 'recipient-007',
      purpose: 'customer-advice', capabilityIds: ['evidence.document.read'],
      expiresAt: '2099-01-01T00:00:00Z', revokedAt: null,
    }],
  });
  const revoke = vi.spyOn(api, 'revokeCustomerConsent').mockRejectedValue(new Error('lost acknowledgement'));
  mount(api);
  const user = userEvent.setup();
  await screen.findByText('Unexpired record');
  await user.click(screen.getByRole('button', { name: 'Stop sharing' }));
  const dialog = await screen.findByRole('dialog', { name: 'Confirm stopping a sharing permission' });
  await user.click(within(dialog).getByRole('checkbox', { name: /I recognize this recipient reference/i }));
  await user.click(within(dialog).getByRole('button', { name: 'Stop sharing' }));
  expect(await screen.findByText('Revocation needs verification')).toBeVisible();
  expect(screen.queryByText('Unexpired record')).not.toBeInTheDocument();
  expect(revoke).toHaveBeenCalledTimes(1);
  await user.click(screen.getByRole('button', { name: 'Refresh permissions' }));
  expect(list).toHaveBeenCalledTimes(2);
  // After an unknown outcome Monergy does not automatically retry a mutating request.
  expect(revoke).toHaveBeenCalledTimes(1);
});

it('never offers revocation controls for an expired or already revoked record', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'customerConsent').mockResolvedValue({
    version: 2,
    items: [
      { id: 'expired', customerId: 'customer-001', actorId: 'one', purpose: 'customer-advice',
        capabilityIds: ['financial-profile.profile.read'], expiresAt: '2001-01-01T00:00:00Z', revokedAt: null },
      { id: 'revoked', customerId: 'customer-001', actorId: 'two', purpose: 'customer-advice',
        capabilityIds: ['financial-profile.profile.read'], expiresAt: '2099-01-01T00:00:00Z', revokedAt: '2026-10-15T00:00:00Z' },
    ],
  });
  mount(api);
  expect(await screen.findByText('Expired')).toBeVisible();
  expect(screen.getByText('Revoked')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Stop sharing' })).not.toBeInTheDocument();
});

it('rejects mismatched mutation receipts and does not claim a completed revocation', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'customerConsent').mockResolvedValue({
    version: 9,
    items: [{ id: 'grant-009', customerId: 'customer-001', actorId: 'recipient-009',
      purpose: 'customer-advice', capabilityIds: ['evidence.document.read'],
      expiresAt: '2099-01-01T00:00:00Z', revokedAt: null }],
  });
  vi.spyOn(api, 'revokeCustomerConsent').mockResolvedValue({
    id: 'another-grant', customerId: 'customer-001', actorId: 'recipient-009',
    operation: 'Revoked', version: 10, evidenceReference: 'other-evidence',
  });
  mount(api);
  const user = userEvent.setup();
  await screen.findByText('Unexpired record');
  await user.click(screen.getByRole('button', { name: 'Stop sharing' }));
  const dialog = await screen.findByRole('dialog', { name: 'Confirm stopping a sharing permission' });
  await user.click(within(dialog).getByRole('checkbox', { name: /I recognize this recipient reference/i }));
  await user.click(within(dialog).getByRole('button', { name: 'Stop sharing' }));
  expect(await screen.findByText('Revocation needs verification')).toBeVisible();
  expect(screen.queryByText('Unexpired record')).not.toBeInTheDocument();
});

it('displays the owner-resolved recipient label on a recorded grant and confirms the same recipient', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'customerConsent').mockResolvedValue({
    version: 3,
    items: [{
      id: 'consent-to-advisor', customerId: 'customer-001',
      actorId: 'ci-1234', recipientLabel: 'advisor@example.com',
      purpose: 'customer-advice',
      capabilityIds: ['financial-profile.profile.read'],
      expiresAt: '2099-01-01T00:00:00Z', revokedAt: null,
    }],
  });
  mount(api);
  const user = userEvent.setup();
  expect(await screen.findByText('advisor@example.com')).toBeVisible();
  await user.click(screen.getByRole('button', { name: 'Stop sharing' }));
  const dialog = await screen.findByRole('dialog', { name: 'Confirm stopping a sharing permission' });
  expect(within(dialog).getByText('advisor@example.com')).toBeVisible();
  expect(within(dialog).getByText(/ci-1234/)).toBeVisible();
  expect(within(dialog).getByText(/consent-to-advisor/)).toBeVisible();
  expect(within(dialog).queryByText('Recipient label not available')).not.toBeInTheDocument();
});

it('does not fabricate recipient identity when the C&I owner cannot resolve it', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'customerConsent').mockResolvedValue({
    version: 1,
    items: [{
      id: 'grant-no-recipient-label', customerId: 'customer-001',
      actorId: 'unknown-actor', recipientLabel: null,
      purpose: 'customer-advice',
      capabilityIds: ['financial-profile.profile.read'],
      expiresAt: '2099-01-01T00:00:00Z', revokedAt: null,
    }],
  });
  mount(api);
  expect(await screen.findByText(/Recipient label not available. Permission is recorded/)).toBeVisible();
  expect(screen.queryByText('advisor@example.com')).not.toBeInTheDocument();
});

it('rejects malformed recipient labels instead of presenting untrusted identity data', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'customerConsent').mockResolvedValue({
    version: 1,
    items: [{
      id: 'grant-malformed-label', customerId: 'customer-001',
      actorId: 'ci-001', recipientLabel: '',
      purpose: 'customer-advice',
      capabilityIds: ['financial-profile.profile.read'],
      expiresAt: '2099-01-01T00:00:00Z', revokedAt: null,
    }],
  });
  mount(api);
  expect(await screen.findByText('Sharing permissions unavailable')).toBeVisible();
  expect(screen.queryByText('Unexpired record')).not.toBeInTheDocument();
});
