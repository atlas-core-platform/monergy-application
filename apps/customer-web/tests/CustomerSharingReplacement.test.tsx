import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import ConsentExperience from '../src/product/ConsentExperience';

const session = { tenantId: 'T001', actorId: 'owner-001', authenticationContextId: 'customer-session', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
interface Change { requestId: string; expectedVersion: number; capabilityIds: string[]; expiresAt: string }
const old = (expired = false) => ({ id: 'grant-old', customerId: 'customer-001', actorId: 'advisor-001', recipientLabel: 'advisor@example.test', purpose: 'customer-advice', capabilityIds: ['evidence.document.read'], expiresAt: expired ? '2026-01-01T00:00:00Z' : '2027-01-01T00:00:00Z', revokedAt: null as string | null });
function options(expired = false) { return { sharing: { tenantId: 'T001', actorId: 'owner-001', customerId: 'customer-001', version: 5, purpose: 'customer-advice', maximumDays: 90,
  scopes: [{ id: 'financial-profile.profile.read', label: 'Read financial information', description: 'Read recorded financial information.' }, { id: 'evidence.document.read', label: 'Read documents', description: 'Read recorded documents.' }] }, grant: old(expired) }; }
function receipt(c: Change) { return { requestId: c.requestId, revocation: { id: 'grant-old', customerId: 'customer-001', actorId: 'advisor-001', operation: 'Revoked', version: 6, evidenceReference: 'old-evidence' }, receipt: { id: 'grant-new', customerId: 'customer-001', actorId: 'advisor-001', operation: 'Granted', version: 7, evidenceReference: 'new-evidence' } }; }
function state(api: AccessApi, customerId = 'customer-001'): WorkspaceSessionState {
  return { status: 'authorized', session, api, authorization: { area: 'product', context: { tenantId: 'T001', actorId: 'owner-001', customerId, customerName: 'Customer', capabilities: [], environment: 'Test environment', dataDurability: 'Temporary data', productionAccepted: false } }, message: '', recovery: null,
    connect: vi.fn().mockResolvedValue(undefined), retry: vi.fn().mockResolvedValue(undefined), signOut: vi.fn().mockResolvedValue(undefined), cancel: vi.fn() };
}
function mount(api: AccessApi) {
  const wrap = (value: WorkspaceSessionState) => <FoundationProvider appearance="midnight"><Context value={value}><ConsentExperience /></Context></FoundationProvider>;
  const view = render(wrap(state(api))); return { ...view, update: (next: AccessApi) => { view.rerender(wrap(state(next, 'customer-002'))); } };
}
function mock(api: AccessApi, expired = false) {
  const writes: Change[] = []; let revoked = false;
  const replace = vi.spyOn(api, 'replaceCustomerSharing').mockImplementation((_customer, _grant, body) => {
    if (body === undefined) return Promise.resolve(options(expired)); const change = body as Change; writes.push(change); return Promise.resolve(receipt(change));
  });
  const read = vi.spyOn(api, 'customerConsent').mockImplementation(() => {
    const c = writes.at(-1); return Promise.resolve({ version: c ? revoked ? 8 : 7 : 4, items: c ? [
      { ...old(expired), revokedAt: new Date().toISOString() },
      { ...old(), id: 'grant-new', capabilityIds: c.capabilityIds, expiresAt: c.expiresAt, revokedAt: revoked ? new Date().toISOString() : null },
    ] : [old(expired)] });
  });
  const revoke = vi.spyOn(api, 'revokeCustomerConsent').mockImplementation(() => { revoked = true; return Promise.resolve({ id: 'grant-new', customerId: 'customer-001', actorId: 'advisor-001', operation: 'Revoked', version: 8, evidenceReference: 'stopped-evidence' }); });
  return { replace, read, revoke, writes };
}
async function open(expired = false) {
  fireEvent.click(await screen.findByRole('button', { name: expired ? 'Renew sharing' : 'Change sharing' }));
  const dialog = await screen.findByRole('dialog', { name: expired ? 'Renew sharing' : 'Change sharing' });
  await waitFor(() => { expect(dialog).toBeVisible(); });
  await within(dialog).findByRole('checkbox', { name: /^Read financial information/ }); return dialog;
}
async function review(dialog: HTMLElement) {
  fireEvent.click(within(dialog).getByRole('checkbox', { name: /^Read financial information/ })); fireEvent.click(within(dialog).getByRole('button', { name: 'Review sharing' }));
  await within(dialog).findByRole('checkbox', { name: /I recognize this recipient/ });
}
async function confirm(dialog: HTMLElement) {
  fireEvent.click(await within(dialog).findByRole('checkbox', { name: /I recognize this recipient/ })); const button = within(dialog).getByRole('button', { name: 'Confirm sharing' }); fireEvent.click(button); return button;
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it.each([false, true])('changes or renews the selected sharing record with a fixed recipient and fresh revision (expired=%s)', async expired => {
  const api = new AccessApi(session); const f = mock(api, expired); mount(api); const dialog = await open(expired);
  expect(within(dialog).queryByLabelText('Recipient email')).not.toBeInTheDocument(); expect(within(dialog).getByText(/Previous expiry:/)).toBeVisible();
  expect(within(dialog).getByRole('checkbox', { name: /^Read financial information/ })).not.toBeChecked();
  await review(dialog); expect(within(dialog).getByRole('button', { name: 'Confirm sharing' })).toBeDisabled(); expect(f.writes).toHaveLength(0);
  await confirm(dialog); expect(await screen.findByText('Sharing permission recorded')).toBeVisible();
  expect(f.writes[0]).toEqual({ requestId: expect.stringMatching(/^[a-f0-9]{32}$/), expectedVersion: 5, capabilityIds: ['financial-profile.profile.read'], expiresAt: expect.any(String) });
  expect(screen.getAllByText('Revoked')).toHaveLength(1); expect(screen.getByText('Unexpired record')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Stop sharing' }));
  const stop = await screen.findByRole('dialog', { name: 'Confirm stopping a sharing permission' }); await waitFor(() => { expect(stop).toBeVisible(); });
  fireEvent.click(within(stop).getByRole('checkbox', { name: /I recognize this recipient reference/ })); fireEvent.click(within(stop).getByRole('button', { name: 'Stop sharing' }));
  await waitFor(() => { expect(f.revoke).toHaveBeenCalledWith('customer-001', 'grant-new', 7, expect.any(String), expect.any(AbortSignal)); });
});

it('does not offer change or renewal on a revoked record', async () => {
  const api = new AccessApi(session); const f = mock(api); f.read.mockResolvedValue({ version: 4, items: [{ ...old(), revokedAt: new Date().toISOString() }] }); mount(api);
  expect(await screen.findByText('Revoked')).toBeVisible(); expect(screen.queryByRole('button', { name: /^(Change|Renew) sharing$/ })).not.toBeInTheDocument(); expect(f.replace).not.toHaveBeenCalled();
});

it.each(['old-id', 'actor', 'version', 'same-id', 'evidence', 'old-operation', 'evidence-type', 'old-active', 'scope'])('rejects mismatched replacement %s evidence and requires refresh', async kind => {
  const api = new AccessApi(session); const f = mock(api);
  if (['old-active', 'scope'].includes(kind)) f.read.mockImplementation(() => {
    const c = f.writes[0]; return Promise.resolve(c ? { version: 7, items: [{ ...old(), revokedAt: kind === 'old-active' ? null : new Date().toISOString() }, { ...old(), id: 'grant-new', expiresAt: c.expiresAt, capabilityIds: kind === 'scope' ? ['evidence.document.read'] : c.capabilityIds }] } : { version: 4, items: [old()] });
  });
  else f.replace.mockImplementation((_customer, _grant, body) => {
    if (body === undefined) return Promise.resolve(options()); const result = receipt(body as Change);
    if (kind === 'old-id') result.revocation.id = 'wrong'; if (kind === 'actor') result.receipt.actorId = 'other';
    if (kind === 'version') result.revocation.version = 5; if (kind === 'same-id') result.receipt.id = 'grant-old'; if (kind === 'evidence') result.receipt.evidenceReference = result.revocation.evidenceReference;
    if (kind === 'old-operation') result.revocation.operation = 'Granted'; if (kind === 'evidence-type') Object.assign(result.receipt, { evidenceReference: 3 });
    return Promise.resolve(result);
  });
  mount(api); const dialog = await open(); await review(dialog); await confirm(dialog);
  expect(await screen.findByText('Sharing needs verification')).toBeVisible(); expect(screen.queryByText('Sharing permission recorded')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Share information' })).toBeDisabled();
});

it('prevents a second pending change and ignores a late response after customer/API change', async () => {
  const api = new AccessApi(session); const f = mock(api); let resolve: (v: unknown) => void = () => undefined;
  f.replace.mockImplementation((_customer, _grant, body) => body === undefined ? Promise.resolve(options()) : new Promise(r => { resolve = r; }));
  const view = mount(api); const dialog = await open(); await review(dialog); const button = await confirm(dialog); fireEvent.click(button);
  expect(f.replace.mock.calls.filter(([, , body]) => body !== undefined)).toHaveLength(1);
  const next = new AccessApi(session); vi.spyOn(next, 'customerConsent').mockResolvedValue({ version: 9, items: [] }); view.update(next);
  expect(await screen.findByText('No recorded delegated permissions')).toBeVisible();
  await act(async () => { resolve({}); await Promise.resolve(); }); expect(screen.queryByText('Sharing needs verification')).not.toBeInTheDocument(); expect(f.read).toHaveBeenCalledOnce();
});

it('rejects a foreign target before rendering choices and cancels without mutation', async () => {
  const api = new AccessApi(session); const f = mock(api); f.replace.mockResolvedValue({ ...options(), grant: { ...old(), actorId: 'other' } }); mount(api);
  fireEvent.click(await screen.findByRole('button', { name: 'Change sharing' }));
  const dialog = await screen.findByRole('dialog', { name: 'Change sharing' }); await waitFor(() => { expect(dialog).toBeVisible(); }); expect(await within(dialog).findByText(/Sharing options could not be verified/)).toBeVisible();
  expect(within(dialog).queryByRole('button', { name: 'Review sharing' })).not.toBeInTheDocument(); fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
  expect(f.replace.mock.calls.filter(([, , body]) => body !== undefined)).toHaveLength(0);
});

it('requires explicit refresh after conflict and sends no automatic second mutation', async () => {
  const api = new AccessApi(session); const f = mock(api); f.replace.mockImplementation((_customer, _grant, body) => body === undefined ? Promise.resolve(options()) : Promise.reject(new AccessApiError(409, 'CONSENT_VERSION_CONFLICT')));
  mount(api); const dialog = await open(); await review(dialog); await confirm(dialog);
  expect(await screen.findByText('Sharing needs verification')).toBeVisible(); fireEvent.click(screen.getByRole('button', { name: 'Refresh permissions' }));
  expect(await screen.findByRole('button', { name: 'Change sharing' })).toBeEnabled(); expect(f.replace.mock.calls.filter(([, , body]) => body !== undefined)).toHaveLength(1);
});

it('sends exact replacement fields and current session headers without recipient authority overrides', async () => {
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(new Response('{}', { headers: { 'Content-Type': 'application/json' } }))); vi.stubGlobal('fetch', fetch);
  const api = new AccessApi(session); const body = { requestId: 'a'.repeat(32), expectedVersion: 5, capabilityIds: ['evidence.document.read'], expiresAt: '2027-01-01T00:00:00Z' };
  await api.replaceCustomerSharing('customer-001', 'grant-old'); await api.replaceCustomerSharing('customer-001', 'grant-old', body);
  expect(fetch).toHaveBeenNthCalledWith(1, '/consent-api/local/v1/consents/customers/customer-001/grants/grant-old/sharing-options', expect.objectContaining({ method: 'GET' }));
  expect(fetch).toHaveBeenNthCalledWith(2, '/consent-api/local/v1/consents/customers/customer-001/grants/grant-old/replacement', expect.objectContaining({ method: 'POST', body: JSON.stringify(body), headers: { 'Content-Type': 'application/json', 'X-Monergy-Tenant': 'T001', 'X-Monergy-Session': 'customer-session' } }));
});
