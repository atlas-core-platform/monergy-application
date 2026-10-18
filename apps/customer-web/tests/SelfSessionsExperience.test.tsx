import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { App } from '../src/App';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import SelfSessionsExperience from '../src/product/SelfSessionsExperience';

const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'current-session', subjectVersion: 3, expiresAt: '2027-01-01T00:00:00Z' };
const target = 'a'.repeat(64);
const entry = () => ({ actorId: 'A100', sessionReference: target, establishedAt: '2026-10-18T01:00:00Z', expiresAt: '2026-10-18T01:30:00Z', status: 'Active', isCurrent: false });
const page = () => ({ tenantId: 'T001', actorId: 'A100', items: [entry(), { ...entry(), sessionReference: 'b'.repeat(64), isCurrent: true }], nextCursor: null as string | null });
function state(api: AccessApi): WorkspaceSessionState {
  return { status: 'authorized', session, api, authorization: { area: 'product', context: { tenantId: 'T001', actorId: 'A100', customerId: 'C001', customerName: 'Customer', capabilities: [], environment: 'Test environment', dataDurability: 'Temporary data', productionAccepted: false } },
    message: '', recovery: null, connect: vi.fn().mockResolvedValue(undefined), retry: vi.fn().mockResolvedValue(undefined), signOut: vi.fn().mockResolvedValue(undefined), cancel: vi.fn() };
}
function mount(value: WorkspaceSessionState) {
  const wrap = (v: WorkspaceSessionState) => <FoundationProvider appearance="midnight"><Context value={v}><SelfSessionsExperience /></Context></FoundationProvider>;
  const view = render(wrap(value)); return { ...view, update: (v: WorkspaceSessionState) => { view.rerender(wrap(v)); } };
}
async function select() {
  fireEvent.click(await screen.findByRole('button', { name: 'End session' }));
  await waitFor(() => { expect(screen.getByRole('dialog', { name: 'End this session?' })).toBeVisible(); });
}
function confirm() { const button = screen.getByRole('button', { name: 'End selected session' }); fireEvent.click(button); return button; }
const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); window.history.replaceState({}, '', '/'); });

it('shows only session activity, marks the current session and never offers to end it as another session', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfSessions').mockResolvedValue(page()); mount(state(api));
  expect(await screen.findByText('Current session')).toBeVisible();
  expect(screen.getByText(/Device and location details are unavailable/)).toBeVisible();
  const current = screen.getByText('Current session').closest('li'); if (!current) throw new Error('Missing session row');
  expect(within(current).queryByRole('button', { name: 'End session' })).not.toBeInTheDocument();
  expect(screen.getAllByRole('button', { name: 'End session' })).toHaveLength(1);
  expect(screen.queryByText(session.authenticationContextId)).not.toBeInTheDocument();
});

it('requires confirmation and binds the exact selected session and operation before verifying the ended state', async () => {
  const api = new AccessApi(session); let ended = false;
  const read = vi.spyOn(api, 'selfSessions').mockImplementation(() => Promise.resolve({ ...page(), items: page().items.map(x => x.sessionReference === target && ended ? { ...x, status: 'Ended' } : x) }));
  const revoke = vi.spyOn(api, 'revokeSelfSession').mockImplementation((operationId, sessionReference) => { ended = true; return Promise.resolve({ operationId, sessionReference, revoked: true }); });
  const value = state(api); mount(value); await select();
  expect(revoke).not.toHaveBeenCalled(); expect(screen.getByRole('dialog', { name: 'End this session?' })).toBeVisible(); confirm();
  expect(await screen.findByText('The selected session has ended.')).toBeVisible();
  expect(revoke).toHaveBeenCalledWith(expect.stringMatching(/^[0-9a-f]{32}$/), target, expect.any(AbortSignal));
  expect(read).toHaveBeenCalledTimes(2); expect(screen.getByText('Ended')).toBeVisible(); expect(value.signOut).not.toHaveBeenCalled();
});

it('cancels the confirmation without making a mutation', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfSessions').mockResolvedValue(page()); const revoke = vi.spyOn(api, 'revokeSelfSession'); mount(state(api));
  await select(); fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  await waitFor(() => { expect(screen.queryByRole('dialog')).not.toBeInTheDocument(); }); expect(revoke).not.toHaveBeenCalled();
});

it('prevents a duplicate submission while the receipt is pending', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfSessions').mockResolvedValue(page());
  let resolve: (value: unknown) => void = () => undefined;
  const revoke = vi.spyOn(api, 'revokeSelfSession').mockImplementation(() => new Promise(r => { resolve = r; }));
  mount(state(api)); await select(); const button = confirm(); fireEvent.click(button); expect(revoke).toHaveBeenCalledOnce();
  expect(screen.getByRole('button', { name: 'Reload sessions' })).toBeDisabled();
  await act(async () => { resolve({ operationId: 'wrong', sessionReference: target, revoked: true }); await Promise.resolve(); });
  expect(await screen.findByText(/result is not confirmed/)).toBeVisible();
});

it.each([409, 503])('blocks further changes after an unconfirmed %s result until explicit reload succeeds', async status => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfSessions').mockResolvedValue(page());
  const revoke = vi.spyOn(api, 'revokeSelfSession').mockRejectedValue(new AccessApiError(status, 'controlled'));
  mount(state(api)); await select(); confirm();
  expect(await screen.findByText(/result is not confirmed/)).toBeVisible(); expect(screen.getByRole('button', { name: 'End session' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Reload sessions' }));
  expect(await screen.findByRole('button', { name: 'End session' })).toBeEnabled(); expect(revoke).toHaveBeenCalledOnce();
});

it.each(['receipt', 'readback'])('does not claim success from an invalid %s', async kind => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfSessions').mockResolvedValue(page());
  const read = vi.spyOn(api, 'revokeSelfSession').mockImplementation((operationId, sessionReference) => Promise.resolve({ operationId: kind === 'receipt' ? 'wrong' : operationId, sessionReference, revoked: true }));
  mount(state(api)); await select(); confirm(); expect(await screen.findByText(/result is not confirmed/)).toBeVisible();
  expect(screen.queryByText('The selected session has ended.')).not.toBeInTheDocument(); expect(read).toHaveBeenCalledOnce();
});

it.each(['foreign', 'duplicate', 'cursor'])('rejects a malformed %s owner page before displaying session data', async kind => {
  const api = new AccessApi(session); const value = page();
  if (kind === 'foreign') value.items[0] = { ...entry(), actorId: 'A900' };
  if (kind === 'duplicate') value.items = [entry(), entry()];
  if (kind === 'cursor') value.nextCursor = target;
  vi.spyOn(api, 'selfSessions').mockResolvedValue(value); mount(state(api));
  expect(await screen.findByText(/Your sessions could not be verified/)).toBeVisible(); expect(screen.queryByRole('list', { name: 'Your sessions' })).not.toBeInTheDocument();
});

it('uses the exact bounded cursor for older activity and reloads latest activity explicitly', async () => {
  const api = new AccessApi(session);
  const items = Array.from({ length: 100 }, (_, n) => ({ ...entry(), sessionReference: n.toString(16).padStart(64, '0'), status: 'Ended' }));
  const cursor = items[99]?.sessionReference; if (!cursor) throw new Error('Missing test cursor');
  const read = vi.spyOn(api, 'selfSessions').mockImplementation(after => Promise.resolve(after ? { ...page(), items: [entry()] } : { ...page(), items, nextCursor: cursor }));
  mount(state(api)); fireEvent.click(await screen.findByRole('button', { name: 'Older sessions' }));
  expect(await screen.findByRole('button', { name: 'Latest sessions' })).toBeVisible(); expect(read).toHaveBeenLastCalledWith(cursor, expect.any(AbortSignal));
  fireEvent.click(screen.getByRole('button', { name: 'Latest sessions' })); expect(await screen.findByRole('button', { name: 'Older sessions' })).toBeVisible(); expect(read).toHaveBeenLastCalledWith(undefined, expect.any(AbortSignal));
});

it('ignores a late list response after API/session changes even when transport ignores abort', async () => {
  const first = new AccessApi(session); let resolve: (value: unknown) => void = () => undefined;
  const read = vi.spyOn(first, 'selfSessions').mockImplementation(() => new Promise(r => { resolve = r; }));
  const view = mount(state(first)); await waitFor(() => { expect(read).toHaveBeenCalledOnce(); });
  const second = new AccessApi({ ...session, authenticationContextId: 'new-session' }); vi.spyOn(second, 'selfSessions').mockResolvedValue({ ...page(), items: [] });
  view.update({ ...state(second), session: { ...session, authenticationContextId: 'new-session' } });
  expect(await screen.findByText('No sessions on this page.')).toBeVisible();
  await act(async () => { resolve(page()); await Promise.resolve(); }); expect(screen.queryByText('Current session')).not.toBeInTheDocument();
});

it('ignores a late revocation receipt after the current session/API changes', async () => {
  const first = new AccessApi(session); const read = vi.spyOn(first, 'selfSessions').mockResolvedValue(page());
  let resolve: (value: unknown) => void = () => undefined;
  const revoke = vi.spyOn(first, 'revokeSelfSession').mockImplementation(() => new Promise(r => { resolve = r; }));
  const view = mount(state(first)); await select(); confirm();
  const second = new AccessApi({ ...session, authenticationContextId: 'new-session' }); vi.spyOn(second, 'selfSessions').mockResolvedValue({ ...page(), items: [] });
  view.update({ ...state(second), session: { ...session, authenticationContextId: 'new-session' } });
  expect(await screen.findByText('No sessions on this page.')).toBeVisible();
  const call = revoke.mock.calls[0]; if (!call) throw new Error('Missing pending operation');
  await act(async () => { resolve({ operationId: call[0], sessionReference: target, revoked: true }); await Promise.resolve(); });
  expect(read).toHaveBeenCalledOnce(); expect(screen.queryByText('The selected session has ended.')).not.toBeInTheDocument();
});

it('sends only operation and selected reference, using the current session headers', async () => {
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(json({}))); vi.stubGlobal('fetch', fetch); const api = new AccessApi(session);
  await api.selfSessions(target); await api.revokeSelfSession('c'.repeat(32), target);
  expect(fetch).toHaveBeenNthCalledWith(1, '/identity-api/local/v1/me/sessions?after=' + target, expect.objectContaining({ headers: { 'X-Monergy-Tenant': 'T001', 'X-Monergy-Session': 'current-session' } }));
  const options = fetch.mock.calls[1]?.[1] as RequestInit;
  expect(JSON.parse(options.body as string)).toEqual({ operationId: 'c'.repeat(32), sessionReference: target });
});

it('gates the actual anonymous Settings route before private session content loads', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ enabled: false }))); window.history.replaceState({}, '', '/settings/sessions'); render(<App />);
  expect(await screen.findByLabelText('Local access key')).toBeVisible(); expect(screen.queryByRole('list', { name: 'Your sessions' })).not.toBeInTheDocument();
  expect(screen.queryByText('Current session')).not.toBeInTheDocument();
});
