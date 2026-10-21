import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import OneDriveExperience from '../src/product/OneDriveExperience';

const session = { tenantId: 'T001', actorId: 'owner-001', authenticationContextId: 'session-1', subjectVersion: 1, expiresAt: '2099-01-01T00:00:00Z' };
const baseline = { tenantId: 'T001', customerId: 'customer-001', provider: 'OneDrive', status: 'Connected', connectionId: 'a'.repeat(32), version: 3, folderName: 'Statements', refreshedAt: null, imports: [], dataDurability: 'Temporary evidence storage' };
const file = { id: 'file-1', name: 'Statement.txt', mimeType: 'text/plain', version: 'a'.repeat(64), size: 3, canDownload: true, trashed: false, parents: ['folder-1'], eTag: 'a'.repeat(32) };
const receipt = { fileId: file.id, fileVersion: file.version, name: file.name, documentId: 'document-1', documentVersionId: 'version-1', sha256: 'b'.repeat(64), importedAt: '2026-10-21T10:00:00Z' };
function mount(api: AccessApi, customerId = 'customer-001') {
  const element = (current: AccessApi, customer: string) => <FoundationProvider appearance="midnight"><Context value={{
    status: 'authorized', session, api: current, authorization: { area: 'product', context: { tenantId: 'T001', actorId: session.actorId,
      customerId: customer, customerName: 'Customer', capabilities: ['integration-gateway.connection.manage'], environment: 'Test', dataDurability: 'Temporary data', productionAccepted: false } },
    message: '', recovery: null, connect: () => Promise.resolve(), retry: () => Promise.resolve(), signOut: () => Promise.resolve(), cancel: () => undefined,
  }}><OneDriveExperience /></Context></FoundationProvider>;
  const result = render(element(api, customerId));
  return { ...result, change: (next: AccessApi, customer: string) => { result.rerender(element(next, customer)); } };
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('shows missing organization configuration without requesting credentials from the customer', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'oneDrive').mockResolvedValue({ ...baseline, status: 'Not configured', connectionId: null, version: 0, folderName: null });
  mount(api); expect(await screen.findByText(/organization has not configured/)).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Prepare connection' })).not.toBeInTheDocument();
  expect(screen.queryByLabelText(/secret|token|password/i)).not.toBeInTheDocument();
});
it('requires explicit broad Microsoft permission and only opens the Microsoft authorization origin', async () => {
  const api = new AccessApi(session); const owner = vi.spyOn(api, 'oneDrive').mockResolvedValue({ ...baseline, status: 'Configured', connectionId: null, version: 0, folderName: null });
  mount(api); const connect = await screen.findByRole('button', { name: 'Prepare connection' });
  expect(connect).toBeDisabled(); expect(screen.getByText(/read access to your files/)).toBeVisible();
  await userEvent.click(screen.getByRole('checkbox', { name: /I allow OneDrive access/ }));
  owner.mockImplementation(action => Promise.resolve(action === 'authorize' ? { authorizationUrl: 'https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=test' } : { ...baseline, status: 'Waiting permission' }));
  await userEvent.click(connect);
  expect(await screen.findByRole('link', { name: 'Continue to Microsoft' })).toHaveAttribute('rel', 'noopener noreferrer');
  expect(owner).toHaveBeenCalledWith('authorize', { acceptReadAccess: true }, expect.any(AbortSignal));
});
it('imports the selected exact provider version and links the Evidence version', async () => {
  const api = new AccessApi(session); let imported = false;
  const owner = vi.spyOn(api, 'oneDrive').mockImplementation(action => {
    if (action === 'files') return Promise.resolve({ items: [file, { ...file, id: 'google-doc', name: 'Microsoft Doc', mimeType: 'application/vnd.google-apps.document' }], nextPageToken: null });
    if (action === 'import') { imported = true; return Promise.resolve(receipt); }
    return Promise.resolve({ ...baseline, imports: imported ? [receipt] : [] });
  });
  mount(api); await screen.findByText('Connected');
  await userEvent.click(screen.getByRole('button', { name: 'Refresh files' }));
  await userEvent.click(await screen.findByRole('button', { name: 'Import Statement.txt' }));
  expect(owner).toHaveBeenCalledWith('import', { version: 3, id: 'file-1', fileVersion: 'a'.repeat(64) }, expect.any(AbortSignal));
  expect(await screen.findByRole('link', { name: 'Open document' })).toHaveAttribute('href', '/evidence?version=version-1');
  expect(await screen.findByText(/Statement.txt imported/)).toBeVisible();
});
it('confirms disconnect and distinguishes local stop from unconfirmed Microsoft revocation', async () => {
  const api = new AccessApi(session); const owner = vi.spyOn(api, 'oneDrive').mockResolvedValue(baseline);
  mount(api); const button = await screen.findByRole('button', { name: 'Disconnect' }); expect(button).toBeDisabled();
  await userEvent.click(screen.getByRole('checkbox', { name: 'Stop this connection.' }));
  owner.mockResolvedValue({ ...baseline, version: 4, status: 'Disconnected; Microsoft revocation unconfirmed', imports: [receipt] });
  await userEvent.click(button);
  expect(await screen.findByText(/Revocation could not be confirmed/)).toBeVisible();
  expect(screen.getByRole('link', { name: 'Open document' })).toBeVisible();
  expect(owner).toHaveBeenCalledWith('disconnect', { version: 3 }, expect.any(AbortSignal));
});
it('clears prior customer results and rejects a stale response after a context change', async () => {
  const api = new AccessApi(session); let resolve: (value: unknown) => void = () => undefined;
  const pending = vi.spyOn(api, 'oneDrive').mockReturnValue(new Promise(done => { resolve = done; }));
  const view = mount(api); await waitFor(() => { expect(pending).toHaveBeenCalled(); });
  const next = new AccessApi(session); vi.spyOn(next, 'oneDrive').mockResolvedValue({ ...baseline, customerId: 'customer-002', status: 'Not configured', imports: [] });
  view.change(next, 'customer-002'); await screen.findByText('Not configured');
  await act(async () => { resolve({ ...baseline, imports: [receipt] }); await Promise.resolve(); });
  expect(screen.queryByRole('link', { name: 'Open document' })).not.toBeInTheDocument();
});
it('fails closed on mismatched owner context and conceals service details', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'oneDrive').mockResolvedValue({ ...baseline, tenantId: 'T002' });
  mount(api); expect(await screen.findByText(/Connection status is unavailable/)).toBeVisible();
  expect(screen.queryByText('Statements')).not.toBeInTheDocument();
});
it('browses a nested folder and requires separate folder permission before binding', async () => {
  const api = new AccessApi(session);
  const folder = { ...file, id: 'ABC!123', name: 'Statements folder', mimeType: 'application/vnd.monergy.onedrive-folder', size: 0, canDownload: false };
  const owner = vi.spyOn(api, 'oneDrive').mockImplementation((action, _body, _signal, page) => {
    if (action === 'folders') return Promise.resolve({ items: [{ ...folder, id: page ? 'child' : folder.id, name: page ? 'Monthly' : folder.name }], nextPageToken: null });
    if (action === 'folder') return Promise.resolve({ ...baseline, version: 4, folderName: 'Monthly' });
    return Promise.resolve({ ...baseline, status: 'Choose folder' });
  });
  mount(api); await screen.findByText('Choose folder', { selector: '.ant-tag' });
  await userEvent.click(screen.getByRole('button', { name: 'Choose folder' }));
  await userEvent.click(await screen.findByRole('combobox', { name: 'OneDrive folder' }));
  await userEvent.click(await screen.findByText('Statements folder'));
  expect(screen.getByRole('button', { name: 'Use this folder' })).toBeDisabled();
  await userEvent.click(screen.getByRole('button', { name: 'Browse selected folder' }));
  expect(owner).toHaveBeenCalledWith('folders', undefined, expect.any(AbortSignal), 'folder:ABC!123');
  await userEvent.click(screen.getByRole('combobox', { name: 'OneDrive folder' }));
  await userEvent.click(await screen.findByText('Monthly'));
  await userEvent.click(screen.getByRole('checkbox', { name: 'I allow imports from this folder.' }));
  await userEvent.click(screen.getByRole('button', { name: 'Use this folder' }));
  expect(owner).toHaveBeenCalledWith('folder', { version: 3, id: 'child', fileVersion: null, acceptFolderImport: true }, expect.any(AbortSignal));
  expect(await screen.findByText(/Folder connected/)).toBeVisible();
});
it('does not open a foreign authorization origin', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'oneDrive').mockImplementation(action => Promise.resolve(action === 'authorize' ?
    { authorizationUrl: 'https://login.microsoftonline.com.attacker.example/common/oauth2/v2.0/authorize' } :
    { ...baseline, status: 'Configured', connectionId: null }));
  mount(api); await screen.findByRole('button', { name: 'Prepare connection' });
  await userEvent.click(screen.getByRole('checkbox', { name: /I allow OneDrive access/ }));
  await userEvent.click(screen.getByRole('button', { name: 'Prepare connection' }));
  expect(await screen.findByText(/connection could not be updated/)).toBeVisible();
  expect(screen.queryByRole('link', { name: 'Continue to Microsoft' })).not.toBeInTheDocument();
});
