import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { LocalTenantSetup } from '../src/access/TenantSetup';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import type { AccessRecord } from '../src/access/accessApi';
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
  accessPack: {
    installedVersion: '2.0',
    installedIdentity: 'monergy-default-access-pack/2.0',
    installedAt: '2026-10-15T00:00:00Z',
    policyVersion: 7,
    ready: true,
  },
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

function mockSetupCollections(api: AccessApi, roles: AccessRecord[] = []) {
  vi.spyOn(api, 'all').mockImplementation((area) =>
    Promise.resolve(area === 'roles' ? roles : []),
  );
  return vi.spyOn(api, 'request').mockImplementation((path, method) => {
    if (path === 'administration/customer-relationships?limit=100' && method === 'GET')
      return Promise.resolve({ policyVersion: 7, items: [] });
    return Promise.reject(new Error(`Unexpected request ${method ?? 'GET'} ${path}`));
  });
}

it('requires both reviews and waits for server activation before showing the workspace', async () => {
  const api = new AccessApi(session);
  const owner = vi.spyOn(api, 'owner').mockResolvedValue(ready);
  mockSetupCollections(api, [
    {
      id: 'role',
      code: 'financial-advisor',
      label: 'Financial Advisor / Relationship Manager',
      permissionIds: ['permission'],
    },
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


it('requires an explicit owner adoption before a v1 organization can complete setup', async () => {
  const api = new AccessApi(session);
  const v1 = {
    ...ready,
    accessPack: {
      installedVersion: '1.0',
      installedIdentity: 'monergy-default-access-pack/1.0',
      installedAt: '2026-10-14T00:00:00Z',
      policyVersion: 7,
      ready: false,
    },
  };
  const owner = vi.spyOn(api, 'owner').mockResolvedValue(v1);
  let currentPolicyVersion = 7;
  vi.spyOn(api, 'all').mockImplementation(() => Promise.resolve([]));
  const request = vi.spyOn(api, 'request').mockImplementation((path, method) => {
    if (path === 'administration/customer-relationships?limit=100' && method === 'GET')
      return Promise.resolve({ policyVersion: currentPolicyVersion, items: [] });
    if (path === 'administration/default-access-pack/plan' && method === 'GET')
      return Promise.resolve({
        installedVersion: '1.0',
        targetVersion: '2.0',
        policyVersion: 7,
        planHash: 'a'.repeat(64),
        alreadyInstalled: false,
        changes: [{ kind: 'ADD_ROLE', objectId: 'default-role-dsa-case-manager', before: null, after: 'configured' }],
        conflicts: [],
      });
    if (path === 'administration/default-access-pack/adopt' && method === 'POST') {
      currentPolicyVersion = 8;
      owner.mockResolvedValue({
        ...ready,
        policyVersion: 8,
        accessPack: {
          ...ready.accessPack!,
          policyVersion: 8,
        },
      });
      return Promise.resolve({
        packVersion: '2.0',
        policyVersion: 8,
        operationId: 'setup-pack-fixture',
        planHash: 'a'.repeat(64),
        replayed: false,
      });
    }
    return Promise.reject(new Error(`Unexpected request ${method ?? 'GET'} ${path}`));
  });

  mount(api);
  const user = userEvent.setup();
  const adopt = await screen.findByRole('button', { name: 'Apply recommended access update' });
  expect(screen.getByRole('button', { name: 'Complete setup' })).toBeDisabled();
  await user.click(adopt);
  await waitFor(() => {
    expect(request).toHaveBeenCalledWith(
      'administration/default-access-pack/adopt',
      'POST',
      expect.objectContaining({
        expectedPolicyVersion: 7,
        planHash: 'a'.repeat(64),
      }),
    );
  });
  expect(await screen.findByText('Recommended access model ready')).toBeVisible();
});

it('does not overwrite a customized v1 access model', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'owner').mockResolvedValue({
    ...ready,
    state: 'Active',
    receiptReference: 'existing-setup',
    accessPack: {
      installedVersion: '1.0',
      installedIdentity: 'monergy-default-access-pack/1.0',
      installedAt: '2026-10-14T00:00:00Z',
      policyVersion: 7,
      ready: false,
    },
  });
  vi.spyOn(api, 'all').mockImplementation(() => Promise.resolve([]));
  const request = vi.spyOn(api, 'request').mockImplementation((path, method) => {
    if (path === 'administration/customer-relationships?limit=100' && method === 'GET')
      return Promise.resolve({ policyVersion: 7, items: [] });
    if (path === 'administration/default-access-pack/plan' && method === 'GET')
      return Promise.resolve({
        installedVersion: '1.0',
        targetVersion: '2.0',
        policyVersion: 7,
        planHash: 'b'.repeat(64),
        alreadyInstalled: false,
        changes: [],
        conflicts: ['SEEDED_ROLE_CUSTOMIZED:default-role-customer'],
      });
    return Promise.reject(new Error(`Unexpected request ${method ?? 'GET'} ${path}`));
  });

  mount(api);
  const user = userEvent.setup();
  expect(await screen.findByText('Your customized access needs review')).toBeVisible();
  expect(
    screen.queryByRole('button', { name: 'Apply recommended access update' }),
  ).not.toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: 'Continue with current access' }));
  expect(await screen.findByText('Protected workspace')).toBeVisible();
  expect(request).toHaveBeenCalledTimes(1);
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
  mockSetupCollections(api);
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
  mockSetupCollections(api);
  const save = vi.spyOn(api, 'setup').mockImplementation(() => {
    owner.mockResolvedValue({ ...ready, state: 'Active', receiptReference: 'saved-receipt' });
    return Promise.resolve({ state: 'Active' });
  });
  mount(api);
  await userEvent.setup().click(await screen.findByRole('button', { name: 'Resume activation' }));
  expect(save).toHaveBeenCalledWith('/activate', {});
  expect(await screen.findByText('Protected workspace')).toBeVisible();
});
