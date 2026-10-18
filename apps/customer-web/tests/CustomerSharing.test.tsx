import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import ConsentExperience from '../src/product/ConsentExperience';

const session = { tenantId: 'T001', actorId: 'owner-001', authenticationContextId: 'customer-session', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
interface Change { requestId: string; expectedVersion: number; recipientEmail: string; capabilityIds: string[]; expiresAt: string }
const options = () => ({ tenantId: 'T001', actorId: 'owner-001', customerId: 'customer-001', version: 4, purpose: 'customer-advice', maximumDays: 90,
  scopes: [{ id: 'financial-profile.profile.read', label: 'Read financial information', description: 'Read recorded financial information.' }, { id: 'evidence.document.read', label: 'Read documents', description: 'Read documents and recorded versions.' }] });
function state(api: AccessApi, customerId = 'customer-001'): WorkspaceSessionState {
  return { status: 'authorized', session, api, authorization: { area: 'product', context: { tenantId: 'T001', actorId: 'owner-001', customerId, customerName: 'Customer', capabilities: [], environment: 'Test environment', dataDurability: 'Temporary data', productionAccepted: false } }, message: '', recovery: null,
    connect: vi.fn().mockResolvedValue(undefined), retry: vi.fn().mockResolvedValue(undefined), signOut: vi.fn().mockResolvedValue(undefined), cancel: vi.fn() };
}
function mount(api: AccessApi) {
  const wrap = (v: WorkspaceSessionState) => <FoundationProvider appearance="midnight"><Context value={v}><ConsentExperience /></Context></FoundationProvider>;
  const view = render(wrap(state(api))); return { ...view, update: (next: AccessApi, customer: string) => { view.rerender(wrap(state(next, customer))); } };
}
function mock(api: AccessApi) {
  const writes: Change[] = []; let revoked = false;
  const sharing = vi.spyOn(api, 'customerSharing').mockImplementation((_customer, body) => {
    if (body === undefined) return Promise.resolve(options());
    const change = body as Change; writes.push(change);
    return Promise.resolve({ requestId: change.requestId, recipientEmail: change.recipientEmail,
      receipt: { id: 'grant-new', customerId: 'customer-001', actorId: 'advisor-001', operation: 'Granted', version: 5, evidenceReference: 'consent-owner-evidence' } });
  });
  const read = vi.spyOn(api, 'customerConsent').mockImplementation(() => {
    const change = writes.at(-1);
    return Promise.resolve({ version: change ? revoked ? 6 : 5 : 4, items: change ? [{ id: 'grant-new', customerId: 'customer-001', actorId: 'advisor-001', recipientLabel: change.recipientEmail, purpose: 'customer-advice', capabilityIds: change.capabilityIds, expiresAt: change.expiresAt, revokedAt: revoked ? new Date().toISOString() : null }] : [] });
  });
  const revoke = vi.spyOn(api, 'revokeCustomerConsent').mockImplementation(() => { revoked = true; return Promise.resolve({ id: 'grant-new', customerId: 'customer-001', actorId: 'advisor-001', operation: 'Revoked', version: 6, evidenceReference: 'consent-revocation-evidence' }); });
  return { sharing, read, revoke, writes };
}
async function open() {
  const button = await screen.findByRole('button', { name: 'Share information' });
  await waitFor(() => { expect(button).toBeEnabled(); }); fireEvent.click(button);
  const dialog = await screen.findByRole('dialog', { name: 'Share information' });
  await waitFor(() => { expect(dialog).toBeVisible(); }); return dialog;
}
async function review(dialog: HTMLElement, email = 'ADVISOR@example.test') {
  fireEvent.change(await within(dialog).findByLabelText('Recipient email'), { target: { value: email } });
  fireEvent.click(within(dialog).getByRole('checkbox', { name: /^Read financial information/ }));
  fireEvent.click(within(dialog).getByRole('button', { name: 'Review sharing' }));
  return within(dialog).findByRole('checkbox', { name: /I recognize this recipient/ });
}
async function confirm(dialog: HTMLElement) {
  fireEvent.click(await within(dialog).findByRole('checkbox', { name: /I recognize this recipient/ }));
  const button = within(dialog).getByRole('button', { name: 'Confirm sharing' }); fireEvent.click(button); return button;
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('starts with no selected sharing scopes and does not grant before explicit recipient/scope/expiry confirmation', async () => {
  const api = new AccessApi(session); const f = mock(api); mount(api); const dialog = await open();
  expect(await within(dialog).findByRole('checkbox', { name: /^Read financial information/ })).not.toBeChecked();
  expect(within(dialog).getByRole('checkbox', { name: /^Read documents/ })).not.toBeChecked();
  await review(dialog); expect(within(dialog).getByText('advisor@example.test')).toBeVisible();
  expect(within(dialog).getByText(/Expires:/)).toBeVisible(); expect(within(dialog).getByRole('button', { name: 'Confirm sharing' })).toBeDisabled();
  expect(f.sharing.mock.calls.filter(([, body]) => body !== undefined)).toHaveLength(0);
});

it('creates a scoped permission, verifies owner readback and uses the new revision to stop it in the same customer journey', async () => {
  const api = new AccessApi(session); const f = mock(api); mount(api); const dialog = await open(); await review(dialog); await confirm(dialog);
  expect(await screen.findByText('Sharing permission recorded')).toBeVisible();
  const change = f.writes[0]; expect(change).toEqual({ requestId: expect.stringMatching(/^[0-9a-f]{32}$/), expectedVersion: 4, recipientEmail: 'advisor@example.test', capabilityIds: ['financial-profile.profile.read'], expiresAt: expect.any(String) });
  if (!change) throw new Error('Missing request'); expect(Date.parse(change.expiresAt) - Date.now()).toBeGreaterThan(6 * 86_400_000);
  expect(f.read).toHaveBeenCalledTimes(2);
  fireEvent.click(screen.getByRole('button', { name: 'Stop sharing' }));
  const revokeDialog = await screen.findByRole('dialog', { name: 'Confirm stopping a sharing permission' });
  await waitFor(() => { expect(revokeDialog).toBeVisible(); });
  fireEvent.click(within(revokeDialog).getByRole('checkbox', { name: /I recognize this recipient reference/ }));
  fireEvent.click(within(revokeDialog).getByRole('button', { name: 'Stop sharing' }));
  expect(await screen.findByText('Revoked')).toBeVisible();
  expect(f.revoke).toHaveBeenCalledWith('customer-001', 'grant-new', 5, expect.stringMatching(/^[0-9a-f]{32}$/), expect.any(AbortSignal));
});

it('validates recipient and scope and preserves the reviewed details when the user goes back', async () => {
  const api = new AccessApi(session); const f = mock(api); mount(api); const dialog = await open();
  fireEvent.click(await within(dialog).findByRole('button', { name: 'Review sharing' }));
  expect(await within(dialog).findByText('Enter a valid registered email.')).toBeVisible(); expect(await within(dialog).findByText('Choose at least one permission.')).toBeVisible();
  await review(dialog); fireEvent.click(within(dialog).getByRole('button', { name: 'Back to details' }));
  expect(await within(dialog).findByLabelText('Recipient email')).toHaveValue('advisor@example.test'); expect(within(dialog).getByRole('checkbox', { name: /^Read financial information/ })).toBeChecked();
  expect(f.writes).toHaveLength(0);
});

it('cancels without writing and keeps refresh/revocation mutually exclusive with sharing', async () => {
  const api = new AccessApi(session); const f = mock(api); mount(api); const dialog = await open();
  expect(screen.getByRole('button', { name: 'Refresh permissions' })).toBeDisabled();
  fireEvent.click(await within(dialog).findByRole('button', { name: 'Cancel' }));
  await waitFor(() => { expect(screen.queryByRole('dialog')).not.toBeInTheDocument(); }); expect(f.writes).toHaveLength(0); expect(screen.getByRole('button', { name: 'Refresh permissions' })).toBeEnabled();
});

it.each([409, 503])('requires owner refresh after an unconfirmed %s grant instead of submitting a new operation', async status => {
  const api = new AccessApi(session); const f = mock(api);
  f.sharing.mockImplementation((_customer, body) => body === undefined ? Promise.resolve(options()) : Promise.reject(new AccessApiError(status, 'controlled')));
  mount(api); const dialog = await open(); await review(dialog); await confirm(dialog);
  expect(await screen.findByText('Sharing needs verification')).toBeVisible(); expect(screen.getByRole('button', { name: 'Share information' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Refresh permissions' }));
  await waitFor(() => { expect(screen.getByRole('button', { name: 'Share information' })).toBeEnabled(); });
  expect(f.sharing.mock.calls.filter(([, body]) => body !== undefined)).toHaveLength(1);
});

it.each(['operation', 'email', 'recipient', 'scope', 'expiry', 'duplicate'])('rejects mismatched %s receipt/readback without claiming success', async kind => {
  const api = new AccessApi(session); const f = mock(api);
  if (['operation', 'email', 'recipient'].includes(kind)) f.sharing.mockImplementation((_customer, body) => {
    if (body === undefined) return Promise.resolve(options()); const c = body as Change;
    return Promise.resolve({ requestId: kind === 'operation' ? 'wrong' : c.requestId, recipientEmail: kind === 'email' ? 'foreign@example.test' : c.recipientEmail,
      receipt: { id: 'grant-new', customerId: 'customer-001', actorId: kind === 'recipient' ? 'owner-001' : 'advisor-001', operation: 'Granted', version: 5, evidenceReference: 'consent-evidence' } });
  });
  else f.read.mockImplementation(() => {
    const c = f.writes[0]; const item = { id: 'grant-new', customerId: 'customer-001', actorId: 'advisor-001', recipientLabel: c?.recipientEmail, purpose: 'customer-advice', capabilityIds: kind === 'scope' ? ['evidence.document.read'] : c?.capabilityIds ?? [], expiresAt: kind === 'expiry' ? '2027-01-01T00:00:00Z' : c?.expiresAt ?? '2027-01-01T00:00:00Z', revokedAt: null };
    return Promise.resolve(c ? { version: 5, items: kind === 'duplicate' ? [item, item] : [item] } : { version: 4, items: [] });
  });
  mount(api); const dialog = await open(); await review(dialog); await confirm(dialog);
  expect(await screen.findByText('Sharing needs verification')).toBeVisible(); expect(screen.queryByText('Sharing permission recorded')).not.toBeInTheDocument();
});

it('prevents duplicate confirmation while the first operation is pending', async () => {
  const api = new AccessApi(session); const f = mock(api); let resolve: (value: unknown) => void = () => undefined;
  f.sharing.mockImplementation((_customer, body) => body === undefined ? Promise.resolve(options()) : new Promise(r => { resolve = r; }));
  mount(api); const dialog = await open(); await review(dialog); const button = await confirm(dialog); fireEvent.click(button);
  expect(f.sharing.mock.calls.filter(([, body]) => body !== undefined)).toHaveLength(1);
  await act(async () => { resolve({}); await Promise.resolve(); }); expect(await screen.findByText('Sharing needs verification')).toBeVisible();
});

it('ignores a late grant response after a customer/API change even when transport ignores abort', async () => {
  const first = new AccessApi(session); const f = mock(first); let resolve: (value: unknown) => void = () => undefined;
  f.sharing.mockImplementation((_customer, body) => body === undefined ? Promise.resolve(options()) : new Promise(r => { resolve = r; }));
  const view = mount(first); const dialog = await open(); await review(dialog); await confirm(dialog);
  const second = new AccessApi(session); vi.spyOn(second, 'customerConsent').mockResolvedValue({ version: 8, items: [] }); view.update(second, 'customer-002');
  expect(await screen.findByText('No recorded delegated permissions')).toBeVisible();
  await act(async () => { resolve({}); await Promise.resolve(); });
  expect(screen.queryByText('Sharing needs verification')).not.toBeInTheDocument(); expect(screen.queryByText('Sharing permission recorded')).not.toBeInTheDocument(); expect(f.read).toHaveBeenCalledOnce();
});

it('ignores late options after cancellation and rejects a foreign owner options response', async () => {
  const api = new AccessApi(session); const f = mock(api); let resolve: (value: unknown) => void = () => undefined;
  f.sharing.mockImplementation(() => new Promise(r => { resolve = r; })); mount(api); const dialog = await open();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
  await act(async () => { resolve(options()); await Promise.resolve(); });
  await waitFor(() => { expect(screen.queryByRole('dialog')).not.toBeInTheDocument(); });
  f.sharing.mockResolvedValue({ ...options(), customerId: 'customer-002' }); const foreign = await open();
  expect(await within(foreign).findByText(/Sharing options could not be verified/)).toBeVisible(); expect(within(foreign).queryByLabelText('Recipient email')).not.toBeInTheDocument();
});

it('does not carry an old pending revocation into a different customer context', async () => {
  const first = new AccessApi(session); const f = mock(first);
  f.writes.push({ requestId: 'a'.repeat(32), expectedVersion: 4, recipientEmail: 'advisor@example.test', capabilityIds: ['financial-profile.profile.read'], expiresAt: '2027-01-01T00:00:00Z' });
  let resolve: (value: unknown) => void = () => undefined;
  f.revoke.mockImplementation(() => new Promise(r => { resolve = r; })); const view = mount(first);
  fireEvent.click(await screen.findByRole('button', { name: 'Stop sharing' }));
  const old = await screen.findByRole('dialog', { name: 'Confirm stopping a sharing permission' });
  await waitFor(() => { expect(old).toBeVisible(); });
  fireEvent.click(within(old).getByRole('checkbox', { name: /I recognize this recipient reference/ })); fireEvent.click(within(old).getByRole('button', { name: 'Stop sharing' }));
  const second = new AccessApi(session); vi.spyOn(second, 'customerConsent').mockResolvedValue({ version: 8, items: [] });
  vi.spyOn(second, 'customerSharing').mockResolvedValue({ ...options(), customerId: 'customer-002', version: 8 }); view.update(second, 'customer-002');
  const dialog = await open(); expect(await within(dialog).findByLabelText('Recipient email')).toBeVisible();
  await act(async () => { resolve({}); await Promise.resolve(); });
  expect(within(dialog).getByLabelText('Recipient email')).toBeVisible(); expect(screen.queryByText('Revocation needs verification')).not.toBeInTheDocument();
});

it('sends only intended grant fields with the current session headers', async () => {
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(new Response('{}', { headers: { 'Content-Type': 'application/json' } }))); vi.stubGlobal('fetch', fetch);
  const api = new AccessApi(session); const body = { requestId: 'a'.repeat(32), expectedVersion: 4, recipientEmail: 'advisor@example.test', capabilityIds: ['evidence.document.read'], expiresAt: '2027-01-01T00:00:00Z' };
  await api.customerSharing('customer-001'); await api.customerSharing('customer-001', body);
  expect(fetch).toHaveBeenNthCalledWith(1, '/consent-api/local/v1/consents/customers/customer-001/sharing-options', expect.objectContaining({ method: 'GET' }));
  expect(fetch).toHaveBeenNthCalledWith(2, '/consent-api/local/v1/consents/customers/customer-001/sharing', expect.objectContaining({ method: 'POST', body: JSON.stringify(body), headers: { 'Content-Type': 'application/json', 'X-Monergy-Tenant': 'T001', 'X-Monergy-Session': 'customer-session' } }));
});
