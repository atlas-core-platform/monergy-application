import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { LocalTenantSetup } from '../src/access/TenantSetup';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { SetupAccessPolicyState, TenantSetupState } from '../src/access/onboarding';

const session = {
  tenantId: 'T001',
  actorId: 'A900',
  authenticationContextId: 'fixture-session',
  subjectVersion: 1,
  expiresAt: '2027-01-01T00:00:00Z',
};

const accessPolicy = (state: SetupAccessPolicyState) => ({
  state,
  changeCount: state === 'UpdateAvailable' ? 9 : 0,
  roleChangeCount: state === 'UpdateAvailable' ? 8 : 0,
  permissionChangeCount: state === 'UpdateAvailable' ? 1 : 0,
  conflictCount: state === 'Customized' || state === 'Blocked' ? 1 : 0,
});

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
  accessPolicy: accessPolicy('Ready'),
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
          authorization: { area: 'administration', authority: 'TenantAdmin', policyVersion: 7 },
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

it('requires current organization and access review before showing the workspace', async () => {
  const api = new AccessApi(session);
  const owner = vi.spyOn(api, 'owner').mockResolvedValue(ready);
  vi.spyOn(api, 'all').mockResolvedValue([
    { id: 'customer', label: 'Customer', permissionIds: ['read', 'upload', 'process'] },
    { id: 'advisor', label: 'Financial Advisor / Relationship Manager', permissionIds: [] },
  ]);
  const save = vi.spyOn(api, 'setup').mockImplementation(() => {
    owner.mockResolvedValue({ ...ready, state: 'Active', receiptReference: 'setup-receipt' });
    return Promise.resolve({ state: 'Active' });
  });

  mount(api);
  const user = userEvent.setup();
  expect(await screen.findByText('Recommended access is ready')).toBeVisible();
  expect(screen.getByText('Access ready')).toBeVisible();
  expect(screen.getByText('Role configured')).toBeVisible();

  const complete = await screen.findByRole('button', { name: 'Complete organization setup' });
  expect(complete).toBeDisabled();
  expect(screen.queryByText('Protected workspace')).not.toBeInTheDocument();

  await user.click(screen.getByLabelText('I have reviewed the organization details'));
  expect(complete).toBeDisabled();
  await user.click(screen.getByLabelText('I have reviewed how roles and customer access work'));
  await user.click(complete);

  await waitFor(() => {
    expect(save).toHaveBeenCalledTimes(1);
  });
  expect(save.mock.calls[0]?.[0]).toBe('');
  expect(save.mock.calls[0]?.[1]).toMatchObject({
    expectedPolicyVersion: 7,
    organizationReviewed: true,
    accessReviewed: true,
  });
  expect(await screen.findByText('Protected workspace')).toBeVisible();
});

it('explicitly applies a clean recommended-access update before setup can continue', async () => {
  const api = new AccessApi(session);
  const owner = vi.spyOn(api, 'owner').mockResolvedValue({
    ...ready,
    accessPolicy: accessPolicy('UpdateAvailable'),
  });
  vi.spyOn(api, 'all').mockResolvedValue([]);
  const save = vi.spyOn(api, 'setup').mockImplementation((path) => {
    if (path === '/access-policy/adopt') {
      owner.mockResolvedValue({
        ...ready,
        policyVersion: 8,
        accessPolicy: accessPolicy('Ready'),
      });
      return Promise.resolve({ state: 'Ready' });
    }
    return Promise.resolve({ state: 'ReadyForAdmin' });
  });

  mount(api);
  const user = userEvent.setup();
  const apply = await screen.findByRole('button', { name: 'Apply recommended access' });
  expect(screen.getByLabelText('I have reviewed how roles and customer access work')).toBeDisabled();

  await user.click(apply);
  await waitFor(() => {
    expect(save).toHaveBeenCalledWith(
      '/access-policy/adopt',
      expect.objectContaining({ requestId: expect.any(String) }),
    );
  });
  expect(await screen.findByText('Recommended access is ready')).toBeVisible();
});

it('preserves customized access and allows deliberate review without replacing it', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'owner').mockResolvedValue({
    ...ready,
    accessPolicy: accessPolicy('Customized'),
  });
  vi.spyOn(api, 'all').mockResolvedValue([
    { id: 'custom-role', label: 'Customer service specialist', permissionIds: ['custom'] },
  ]);
  const save = vi.spyOn(api, 'setup').mockResolvedValue({ state: 'Active' });

  mount(api);
  const user = userEvent.setup();
  expect(
    await screen.findByText('Your organization uses a customized access model'),
  ).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Apply recommended access' })).not.toBeInTheDocument();

  await user.click(screen.getByLabelText('I have reviewed the organization details'));
  await user.click(screen.getByLabelText('I have reviewed how roles and customer access work'));
  await user.click(screen.getByRole('button', { name: 'Complete organization setup' }));
  expect(save).toHaveBeenCalledTimes(1);
});

it('blocks activation for an unsupported access-policy state', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'owner').mockResolvedValue({
    ...ready,
    accessPolicy: accessPolicy('Blocked'),
  });
  vi.spyOn(api, 'all').mockResolvedValue([]);
  const save = vi.spyOn(api, 'setup');

  mount(api);
  expect(await screen.findByText('Access setup needs attention')).toBeVisible();
  expect(screen.getByLabelText('I have reviewed how roles and customer access work')).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Complete organization setup' })).toBeDisabled();
  expect(save).not.toHaveBeenCalled();
});

it('does not accept setup from another organization or an active response without a receipt', async () => {
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
  await user.click(screen.getByLabelText('I have reviewed how roles and customer access work'));
  await user.click(screen.getByRole('button', { name: 'Complete organization setup' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('workspace changed');
  expect(save).toHaveBeenCalledTimes(1);

  await user.click(screen.getByRole('button', { name: 'Refresh setup' }));
  expect(await screen.findByRole('button', { name: 'Complete organization setup' })).toBeDisabled();
  expect(save).toHaveBeenCalledTimes(1);
});

it('can resume activation from a current review receipt without creating another review', async () => {
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
  await userEvent
    .setup()
    .click(await screen.findByRole('button', { name: 'Resume activation' }));
  expect(save).toHaveBeenCalledWith('/activate', {});
  expect(await screen.findByText('Protected workspace')).toBeVisible();
});
