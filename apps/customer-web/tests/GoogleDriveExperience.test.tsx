import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import GoogleDriveExperience from '../src/product/GoogleDriveExperience';

const session = { tenantId: 'T001', actorId: 'owner-001', authenticationContextId: 'session-1', subjectVersion: 1, expiresAt: '2099-01-01T00:00:00Z' };
const baseline = { tenantId: 'T001', customerId: 'customer-001', provider: 'Google Drive', status: 'Connected', connectionId: 'a'.repeat(32), version: 3, folderName: 'Statements', refreshedAt: null, imports: [], dataDurability: 'Temporary evidence storage' };
const file = { id: 'file-1', name: 'Statement.txt', mimeType: 'text/plain', version: '15', size: 3, canDownload: true, trashed: false, parents: ['folder-1'], md5Checksum: 'a'.repeat(32) };
const receipt = { fileId: file.id, fileVersion: file.version, name: file.name, documentId: 'document-1', documentVersionId: 'version-1', sha256: 'b'.repeat(64), importedAt: '2026-10-21T10:00:00Z' };
function mount(api: AccessApi, customerId = 'customer-001') {
  const element = (current: AccessApi, customer: string) => <FoundationProvider appearance="midnight"><Context value={{
    status: 'authorized', session, api: current, authorization: { area: 'product', context: { tenantId: 'T001', actorId: session.actorId,
      customerId: customer, customerName: 'Customer', capabilities: ['integration-gateway.connection.manage'], environment: 'Test', dataDurability: 'Temporary data', productionAccepted: false } },
    message: '', recovery: null, connect: () => Promise.resolve(), retry: () => Promise.resolve(), signOut: () => Promise.resolve(), cancel: () => undefined,
  }}><GoogleDriveExperience /></Context></FoundationProvider>;
  const result = render(element(api, customerId));
  return { ...result, change: (next: AccessApi, customer: string) => { result.rerender(element(next, customer)); } };
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('shows missing organization configuration without requesting credentials from the customer', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'googleDrive').mockResolvedValue({ ...baseline, status: 'Not configured', connectionId: null, version: 0, folderName: null });
  mount(api); expect(await screen.findByText(/organization has not configured/)).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Prepare connection' })).not.toBeInTheDocument();
  expect(screen.queryByLabelText(/secret|token|password/i)).not.toBeInTheDocument();
});
it('requires explicit broad Google permission and only opens the Google authorization origin', async () => {
  const api = new AccessApi(session); const owner = vi.spyOn(api, 'googleDrive').mockResolvedValue({ ...baseline, status: 'Configured', connectionId: null, version: 0, folderName: null });
  mount(api); const connect = await screen.findByRole('button', { name: 'Prepare connection' });
  expect(connect).toBeDisabled(); expect(screen.getByText(/view and download all files/)).toBeVisible();
  await userEvent.click(screen.getByRole('checkbox', { name: /I allow Google Drive access/ }));
  owner.mockImplementation(action => Promise.resolve(action === 'authorize' ? { authorizationUrl: 'https://accounts.google.com/o/oauth2/v2/auth?state=test' } : { ...baseline, status: 'Waiting permission' }));
  await userEvent.click(connect);
  expect(await screen.findByRole('link', { name: 'Continue to Google' })).toHaveAttribute('rel', 'noopener noreferrer');
  expect(owner).toHaveBeenCalledWith('authorize', { acceptReadAccess: true }, expect.any(AbortSignal));
});
it('imports the selected exact provider version and links the Evidence version', async () => {
  const api = new AccessApi(session); let imported = false;
  const owner = vi.spyOn(api, 'googleDrive').mockImplementation(action => {
    if (action === 'files') return Promise.resolve({ items: [file, { ...file, id: 'google-doc', name: 'Google Doc', mimeType: 'application/vnd.google-apps.document' }], nextPageToken: null });
    if (action === 'import') { imported = true; return Promise.resolve(receipt); }
    return Promise.resolve({ ...baseline, imports: imported ? [receipt] : [] });
  });
  mount(api); await screen.findByText('Connected');
  await userEvent.click(screen.getByRole('button', { name: 'Refresh files' }));
  await userEvent.click(await screen.findByRole('button', { name: 'Import Statement.txt' }));
  expect(owner).toHaveBeenCalledWith('import', { version: 3, id: 'file-1', fileVersion: '15' }, expect.any(AbortSignal));
  expect(await screen.findByRole('link', { name: 'Open document' })).toHaveAttribute('href', '/evidence?version=version-1');
  expect(await screen.findByText(/Statement.txt imported/)).toBeVisible();
});
it('confirms disconnect and distinguishes local stop from unconfirmed Google revocation', async () => {
  const api = new AccessApi(session); const owner = vi.spyOn(api, 'googleDrive').mockResolvedValue(baseline);
  mount(api); const button = await screen.findByRole('button', { name: 'Disconnect' }); expect(button).toBeDisabled();
  await userEvent.click(screen.getByRole('checkbox', { name: 'Stop this connection.' }));
  owner.mockResolvedValue({ ...baseline, version: 4, status: 'Disconnected; Google revocation unconfirmed', imports: [receipt] });
  await userEvent.click(button);
  expect(await screen.findByText(/Revocation could not be confirmed/)).toBeVisible();
  expect(screen.getByRole('link', { name: 'Open document' })).toBeVisible();
  expect(owner).toHaveBeenCalledWith('disconnect', { version: 3 }, expect.any(AbortSignal));
});
it('clears prior customer results and rejects a stale response after a context change', async () => {
  const api = new AccessApi(session); let resolve: (value: unknown) => void = () => undefined;
  const pending = vi.spyOn(api, 'googleDrive').mockReturnValue(new Promise(done => { resolve = done; }));
  const view = mount(api); await waitFor(() => { expect(pending).toHaveBeenCalled(); });
  const next = new AccessApi(session); vi.spyOn(next, 'googleDrive').mockResolvedValue({ ...baseline, customerId: 'customer-002', status: 'Not configured', imports: [] });
  view.change(next, 'customer-002'); await screen.findByText('Not configured');
  await act(async () => { resolve({ ...baseline, imports: [receipt] }); await Promise.resolve(); });
  expect(screen.queryByRole('link', { name: 'Open document' })).not.toBeInTheDocument();
});
it('fails closed on mismatched owner context and conceals service details', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'googleDrive').mockResolvedValue({ ...baseline, tenantId: 'T002' });
  mount(api); expect(await screen.findByText(/Connection status is unavailable/)).toBeVisible();
  expect(screen.queryByText('Statements')).not.toBeInTheDocument();
});
