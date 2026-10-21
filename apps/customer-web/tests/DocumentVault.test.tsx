import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import DocumentVault from '../src/product/DocumentVault';
import { listVault, replaceVault } from '../src/product/documentVaultApi';
import type { VaultItem } from '../src/product/documentVaultApi';

const row: VaultItem = { documentId: 'document-one', documentVersionId: 'version-one', evidenceId: 'evidence-one', version: 1,
  originalFileName: 'statement.txt', contentType: 'text/plain', contentLength: 10, contentSha256: 'a'.repeat(64), sourceName: 'Customer upload', receivedAt: '2026-10-21T00:00:00Z', createdAt: '2026-10-21T00:00:00Z',
  metadata: { category: 'Other', documentType: null, documentDate: null, financialYear: null, revision: 0, updatedAt: null, updatedBy: null },
  sourceLocation: null, scanStatus: 'UNRECORDED', verificationStatus: 'NOT_VERIFIED', storageMode: 'TEMPORARY_REFERENCE', storageLocation: null, storageVersionId: null };
function view(api: AccessApi, onOpen = vi.fn(), customer = 'customer-one', write = true) {
  return <FoundationProvider appearance="midnight"><Context value={{ status: 'authorized', api,
    session: { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'session-one', subjectVersion: 1, expiresAt: '2027-01-01T00:00:00Z' },
    authorization: { area: 'product', context: { tenantId: 'T001', actorId: 'A100', customerId: customer, customerName: 'Customer', capabilities: ['evidence.document.read', ...(write ? ['evidence.document.upload'] : [])], environment: 'Test', dataDurability: 'Temporary', productionAccepted: false } },
    message: '', recovery: null, connect: () => Promise.resolve(), retry: () => Promise.resolve(), signOut: () => Promise.resolve(), cancel: () => undefined,
  }}><DocumentVault onOpen={onOpen} refreshKey="" /></Context></FoundationProvider>;
}
function setup(write = true) {
  const api = new AccessApi({ tenantId: 'T001', actorId: 'A100', authenticationContextId: 'session-one', subjectVersion: 1, expiresAt: '2027-01-01T00:00:00Z' }); const open = vi.fn();
  const transport = vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.includes('?') ? { items: [row], nextCursor: null } : row));
  const mounted = render(view(api, open, 'customer-one', write)); return { api, open, transport, mounted, user: userEvent.setup() };
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('shows all required categories and separates customer metadata, scan and authenticity', async () => {
  const { user } = setup(); await user.click(await screen.findByRole('button', { name: /statement.txt/ }));
  expect(await screen.findByText('No recorded scan evidence')).toBeVisible();
  expect(screen.getByText(/Not verified. Processing/)).toBeVisible();
  expect(screen.getByText(/Temporary test environment storage/)).toBeVisible();
  await user.click(screen.getByRole('combobox', { name: 'Document category' }));
  for (const label of ['Identity', 'Income Tax', 'Banking', 'Credit', 'Loans', 'Insurance', 'Investments', 'Property', 'Vehicles']) expect(screen.getAllByText(label).length).toBeGreaterThan(0);
});
it('preserves exact version identity when opening history, processing and financial links', async () => {
  const { user, open } = setup(); await user.click(await screen.findByRole('button', { name: /statement.txt/ }));
  await user.click(await screen.findByRole('button', { name: 'Version history, processing and related financial records' }));
  expect(open).toHaveBeenCalledWith(row);
});
it('does not permit writes without upload capability', async () => {
  const { user } = setup(false); await user.click(await screen.findByRole('button', { name: /statement.txt/ }));
  expect(await screen.findByRole('button', { name: 'Save metadata' })).toBeDisabled();
  expect(screen.getByLabelText('Replacement file')).toBeDisabled();
});
it('retains the same metadata operation and frozen fields after an uncertain response', async () => {
  const { user, transport } = setup(); let writes = 0;
  transport.mockImplementation((path, method, body) => {
    if (method === 'PUT') { writes++; if (writes === 1) return Promise.reject(new AccessApiError(503, 'OWNER_UNAVAILABLE'));
      return Promise.resolve({ ...row.metadata, ...(body as object), revision: 1, updatedBy: 'A100', updatedAt: '2026-10-21T00:00:00Z' }); }
    return Promise.resolve(path.includes('?') ? { items: [row], nextCursor: null } : row);
  });
  await user.click(await screen.findByRole('button', { name: /statement.txt/ }));
  await user.type(await screen.findByLabelText('Document type'), 'Bank statement');
  await user.click(screen.getByRole('button', { name: 'Save metadata' }));
  expect(await screen.findByRole('button', { name: 'Retry same operation' })).toBeEnabled(); expect(screen.getByLabelText('Document type')).toBeDisabled();
  await user.click(screen.getByRole('button', { name: 'Retry same operation' }));
  expect(await screen.findByText('Document metadata saved.')).toBeVisible();
  const requests = transport.mock.calls.filter(call => call[1] === 'PUT'); expect(requests).toHaveLength(2); expect(requests[0]?.[2]).toEqual(requests[1]?.[2]);
});
it('reviews replacement, retries the identical payload and opens only the new source version', async () => {
  const { user, transport, open } = setup(); let writes = 0; let version = '';
  transport.mockImplementation((path, method, body) => {
    if (method === 'POST') { writes++; version = 'document-version-' + (body as { operationId: string }).operationId;
      if (writes === 1) return Promise.reject(new AccessApiError(503, 'OWNER_UNAVAILABLE'));
      return Promise.resolve({ documentId: row.documentId, documentVersionId: version, version: 2 }); }
    if (path.endsWith(version) && version) return Promise.resolve({ ...row, documentVersionId: version, version: 2 });
    return Promise.resolve(path.includes('?') ? { items: [row], nextCursor: null } : row);
  });
  await user.click(await screen.findByRole('button', { name: /statement.txt/ }));
  await user.upload(await screen.findByLabelText('Replacement file'), new File(['changed'], 'replacement.txt', { type: 'text/plain' }));
  await user.click(screen.getByRole('button', { name: 'Review replacement' }));
  expect(transport.mock.calls.filter(call => call[1] === 'POST')).toHaveLength(0);
  await user.click(await screen.findByRole('button', { name: 'Confirm replacement' }));
  await user.click(await screen.findByRole('button', { name: 'Retry same operation' }));
  await user.click(await screen.findByRole('button', { name: 'Open replacement and process' }));
  await waitFor(() => { expect(open).toHaveBeenCalledWith(expect.objectContaining({ documentVersionId: version, version: 2 })); });
  const requests = transport.mock.calls.filter(call => call[1] === 'POST'); expect(requests[0]?.[2]).toEqual(requests[1]?.[2]);
});
it('clears old customer data when scope changes and ignores a late list response', async () => {
  const { api, transport, mounted } = setup();
  await screen.findByRole('button', { name: /statement.txt/ });
  transport.mockImplementation(() => Promise.resolve({ items: [], nextCursor: null }));
  mounted.rerender(view(api, vi.fn(), 'customer-two'));
  expect(screen.queryByText('statement.txt')).not.toBeInTheDocument();
  expect(await screen.findByText('No document versions match this category and search.')).toBeVisible();
});
it('fails closed on malformed pagination and replacement receipts', async () => {
  const api = new AccessApi({ tenantId: 'T001', actorId: 'A100', authenticationContextId: 'session-one', subjectVersion: 1, expiresAt: '2027-01-01T00:00:00Z' }); vi.spyOn(api, 'product').mockResolvedValue({ items: [row], nextCursor: 'foreign-version' });
  await expect(listVault(api, 'all', '', null, new AbortController().signal)).rejects.toThrow();
  await expect(replaceVault(api, 'document-one', { operationId: 'op', expectedVersionId: 'version-one', originalFileName: 'new.txt', contentType: 'text/plain', contentBase64: 'YQ==' }, new AbortController().signal)).rejects.toThrow();
});
