import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import ConnectionsOverviewExperience from '../src/product/ConnectionsOverviewExperience';

const session = { tenantId: 'T001', actorId: 'owner-001', authenticationContextId: 'session-1', subjectVersion: 1, expiresAt: '2099-01-01T00:00:00Z' };
const overview = { tenantId: 'T001', customerId: 'customer-001', generatedAt: '2026-10-30T06:15:00Z', connections: [
  { providerCode: 'google-drive', provider: 'Google Drive', state: 'Connected', permissionState: 'Current', healthState: 'Status available', managePath: '/connections/google-drive', checkedAt: '2026-10-30T06:15:00Z', lastConfirmedAt: '2026-10-30T06:00:00Z' },
  { providerCode: 'onedrive', provider: 'OneDrive', state: 'Not configured', permissionState: 'Not requested', healthState: 'Not configured', managePath: '/connections/onedrive', checkedAt: '2026-10-30T06:15:00Z', lastConfirmedAt: null },
] };
function mount(api: AccessApi, customerId = 'customer-001') {
  const element = (current: AccessApi, customer: string) => <FoundationProvider appearance="midnight"><Context value={{
    status: 'authorized', session, api: current, authorization: { area: 'product', context: { tenantId: 'T001', actorId: session.actorId,
      customerId: customer, customerName: 'Customer', capabilities: ['integration-gateway.connection.manage'], environment: 'Test', dataDurability: 'Temporary data', productionAccepted: false } },
    message: '', recovery: null, connect: () => Promise.resolve(), retry: () => Promise.resolve(), signOut: () => Promise.resolve(), cancel: () => undefined,
  }}><ConnectionsOverviewExperience /></Context></FoundationProvider>;
  const result = render(element(api, customerId));
  return { ...result, change: (next: AccessApi, customer: string) => { result.rerender(element(next, customer)); } };
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('shows canonical safe provider states and exact management links', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'connectionsOverview').mockResolvedValue(overview);
  mount(api);
  expect(await screen.findByText('Connected', { selector: '.ant-tag' })).toBeVisible();
  expect(screen.getByText('Current', { selector: '.ant-tag' })).toBeVisible();
  expect(screen.getByText('Not requested', { selector: '.ant-tag' })).toBeVisible();
  expect(screen.getByText('Status available', { selector: '.ant-tag' })).toBeVisible();
  expect(screen.getByText(/not a live provider availability test/i)).toBeVisible();
  expect(screen.getAllByText(/Status checked/)).toHaveLength(2);
  expect(screen.getByText(/checked again before provider operations/)).toBeVisible();
  expect(screen.getByText(/organization has not configured this provider/)).toBeVisible();
  expect(screen.getByRole('link', { name: 'Manage Google Drive' })).toHaveAttribute('href', '/connections/google-drive');
  expect(screen.getByRole('link', { name: 'Manage OneDrive' })).toHaveAttribute('href', '/connections/onedrive');
  expect(screen.queryByText(/token|client secret|folder id/i)).not.toBeInTheDocument();
});

it('refreshes one current-session overview without posting provider identifiers', async () => {
  const api = new AccessApi(session); const owner = vi.spyOn(api, 'connectionsOverview').mockResolvedValue(overview);
  mount(api); await screen.findByText('Connected', { selector: '.ant-tag' });
  await userEvent.click(screen.getByRole('button', { name: 'Refresh connections' }));
  expect(owner).toHaveBeenLastCalledWith(expect.any(AbortSignal));
});

it('clears stale provider states when the customer context changes', async () => {
  const api = new AccessApi(session); let resolve: (value: unknown) => void = () => undefined;
  const pending = vi.spyOn(api, 'connectionsOverview').mockReturnValue(new Promise(done => { resolve = done; }));
  const view = mount(api); await waitFor(() => { expect(pending).toHaveBeenCalled(); });
  const next = new AccessApi(session); vi.spyOn(next, 'connectionsOverview').mockResolvedValue({ ...overview, customerId: 'customer-002', connections: overview.connections.map(item => ({ ...item, state: 'Not configured', permissionState: 'Not requested', healthState: 'Not configured', lastConfirmedAt: null })) });
  view.change(next, 'customer-002'); expect((await screen.findAllByText('Not configured', { selector: '.ant-tag' }))).toHaveLength(4);
  await act(async () => { resolve(overview); await Promise.resolve(); });
  expect(screen.queryByText('Connected', { selector: '.ant-tag' })).not.toBeInTheDocument();
});

it('fails closed on mismatched tenant or unrecognized provider state', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'connectionsOverview').mockResolvedValue({ ...overview, tenantId: 'T002' });
  mount(api); expect(await screen.findByText(/Connection status is unavailable/)).toBeVisible();
  expect(screen.queryByRole('link', { name: /Manage/ })).not.toBeInTheDocument();
});

it('rejects a permission state that is inconsistent with the owner-confirmed connection state', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'connectionsOverview').mockResolvedValue({ ...overview,
    connections: overview.connections.map(item => item.providerCode === 'google-drive' ? { ...item, permissionState: 'Not requested' } : item) });
  mount(api); expect(await screen.findByText(/Connection status is unavailable/)).toBeVisible();
  expect(screen.queryByRole('link', { name: /Manage/ })).not.toBeInTheDocument();
});

it('fails closed when operational health contradicts the owner-confirmed connection state', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'connectionsOverview').mockResolvedValue({ ...overview,
    connections: overview.connections.map(item => item.providerCode === 'google-drive' ? { ...item, healthState: 'Status unavailable' } : item) });
  mount(api); expect(await screen.findByText(/Connection status is unavailable/)).toBeVisible();
  expect(screen.queryByRole('link', { name: /Manage/ })).not.toBeInTheDocument();
});
