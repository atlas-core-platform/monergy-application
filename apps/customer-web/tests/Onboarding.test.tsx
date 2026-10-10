import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { LocalTenantSetup } from '../src/access/TenantSetup';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { TenantSetupState } from '../src/access/onboarding';

const session = {
  tenantId: 'T001',
  actorId: 'A900',
  authenticationContextId: 'fixture-session',
  subjectVersion: 1,
  expiresAt: '2027-01-01T00:00:00Z',
};
const ready: TenantSetupState = {
  enabled: true,
  tenantId: 'T001',
  actorId: 'A900',
  policyVersion: 7,
  organizationName: 'Example organization',
  countryCode: 'IN',
  timeZone: 'Asia/Kolkata',
  initialAdministratorEmail: 'admin@example.test',
  state: 'ReadyForAdmin',
  customerRelationships: true,
};
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});
function mount(api: AccessApi) {
  render(
    <FoundationProvider appearance="midnight">
      <Context
        value={{
          status: 'authorized',
          session,
          api,
          message: '',
          recovery: null,
          connect: () => Promise.resolve(),
          retry: () => Promise.resolve(),
          signOut: () => Promise.resolve(),
          cancel: () => undefined,
        }}
      >
        <LocalTenantSetup>
          <p>Protected workspace</p>
        </LocalTenantSetup>
      </Context>
    </FoundationProvider>,
  );
}

it('requires both reviews and waits for server activation before showing the workspace', async () => {
  const api = new AccessApi(session);
  const owner = vi.spyOn(api, 'owner').mockResolvedValue(ready);
  vi.spyOn(api, 'all').mockResolvedValue([
    { id: 'role', label: 'Financial Advisor', permissionIds: ['permission'] },
  ]);
  const save = vi.spyOn(api, 'setup').mockImplementation(() => {
    owner.mockResolvedValue({ ...ready, state: 'Active', receiptReference: 'setup-receipt' });
    return Promise.resolve({ state: 'Active' });
  });
  mount(api);
  const user = userEvent.setup();
  const complete = await screen.findByRole('button', { name: 'Complete setup' });
  expect(complete).toBeDisabled();
  expect(screen.queryByText('Protected workspace')).not.toBeInTheDocument();
  await user.click(screen.getByLabelText('I have reviewed the organization details'));
  expect(complete).toBeDisabled();
  await user.click(screen.getByLabelText('I understand how roles and customer access work'));
  await user.click(complete);
  await waitFor(() => {
    expect(save).toHaveBeenCalledTimes(1);
  });
  expect(save.mock.calls[0]?.[1]).toMatchObject({
    expectedPolicyVersion: 7,
    organizationReviewed: true,
    accessReviewed: true,
  });
  expect(await screen.findByText('Protected workspace')).toBeVisible();
});

it('does not accept setup from another tenant or an active response without a receipt', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'owner').mockResolvedValue({ ...ready, tenantId: 'T002', state: 'Active' });
  mount(api);
  expect(await screen.findByRole('alert')).toBeVisible();
  expect(screen.queryByText('Protected workspace')).not.toBeInTheDocument();
});

it('preserves a policy conflict until an explicit refresh and does not repeat the write', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'owner').mockResolvedValue(ready);
  vi.spyOn(api, 'all').mockResolvedValue([]);
  const save = vi
    .spyOn(api, 'setup')
    .mockRejectedValue(new AccessApiError(409, 'TENANT_SETUP_POLICY_CHANGED'));
  mount(api);
  const user = userEvent.setup();
  await user.click(await screen.findByLabelText('I have reviewed the organization details'));
  await user.click(screen.getByLabelText('I understand how roles and customer access work'));
  await user.click(screen.getByRole('button', { name: 'Complete setup' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('workspace changed');
  expect(save).toHaveBeenCalledTimes(1);
  await user.click(screen.getByRole('button', { name: 'Refresh setup' }));
  expect(await screen.findByRole('button', { name: 'Complete setup' })).toBeDisabled();
  expect(save).toHaveBeenCalledTimes(1);
});

it('can resume activation from an existing receipt without creating another review', async () => {
  const api = new AccessApi(session);
  const owner = vi
    .spyOn(api, 'owner')
    .mockResolvedValue({ ...ready, receiptReference: 'saved-receipt' });
  vi.spyOn(api, 'all').mockResolvedValue([]);
  const save = vi.spyOn(api, 'setup').mockImplementation(() => {
    owner.mockResolvedValue({ ...ready, state: 'Active', receiptReference: 'saved-receipt' });
    return Promise.resolve({ state: 'Active' });
  });
  mount(api);
  await userEvent.setup().click(await screen.findByRole('button', { name: 'Resume activation' }));
  expect(save).toHaveBeenCalledWith('/activate', {});
  expect(await screen.findByText('Protected workspace')).toBeVisible();
});
