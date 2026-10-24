import { FoundationProvider } from '@monergy/ui-foundation';
import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { WorkspaceEntry } from '../src/access/WorkspaceEntry';
import { WorkspaceSessionProvider } from '../src/access/WorkspaceSessionProvider';
import { sessionMemory, useWorkspaceSession } from '../src/access/workspaceSession';

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const session = () => ({ tenantId: 'T001', actorId: 'A100', authenticationContextId: 'password-session', subjectVersion: 2, expiresAt: new Date(Date.now() + 1800000).toISOString() });
const context = { tenantId: 'T001', actorId: 'A100', customerId: 'C001', customerName: 'Current Customer', capabilities: ['financial-profile.profile.read'], environment: 'Test environment', dataDurability: 'Temporary data', productionAccepted: false };
function Private() {
  const { signOut } = useWorkspaceSession();
  return <><h2>Private customer information</h2><button onClick={() => { void signOut(); }}>Sign out</button></>;
}
function RestrictedFeature() {
  const { api } = useWorkspaceSession();
  const [message, setMessage] = useState('');
  return <><h2>Private customer information</h2>
    <button onClick={() => {
      if (!api) return;
      void api.product('v1/financial-profile').catch((failure: unknown) => {
        setMessage(failure instanceof Error ? failure.message : 'The request was denied.');
      });
    }}>Try restricted feature</button>
    {message && <p role="alert">{message}</p>}
  </>;
}
function mountRestricted() {
  return render(<FoundationProvider appearance="midnight"><WorkspaceSessionProvider area="product">
    <WorkspaceEntry area="product"><RestrictedFeature /></WorkspaceEntry>
  </WorkspaceSessionProvider></FoundationProvider>);
}
function mount() {
  return render(<FoundationProvider appearance="midnight"><WorkspaceSessionProvider area="product"><WorkspaceEntry area="product"><Private /></WorkspaceEntry></WorkspaceSessionProvider></FoundationProvider>);
}
function owner(signInStatus = 200, contextStatus = 200) {
  const fetch = vi.fn((url: string, options?: RequestInit) => {
    if (options?.signal?.aborted) return Promise.reject(new Error('Aborted'));
    if (url.endsWith('/customer-registration') || url.endsWith('/customer-email-access')) return Promise.resolve(json({ enabled: true }));
    if (url.endsWith('/sign-in')) return Promise.resolve(json(signInStatus === 200 ? session() : { error: 'private owner detail' }, signInStatus));
    if (url.endsWith('/v1/context')) return Promise.resolve(json(contextStatus === 200 ? context : { error: 'private owner detail' }, contextStatus));
    if (url.endsWith('/revoke')) return Promise.resolve(json(true));
    return Promise.resolve(json({}, 404));
  });
  vi.stubGlobal('fetch', fetch); return fetch;
}
async function signIn() {
  fireEvent.change(await screen.findByLabelText('Email'), { target: { value: 'person@example.test' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'a long password value' } });
  await userEvent.click(screen.getByRole('button', { name: /^Sign in$/ }));
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });
it('defaults to configured password sign-in and verifies the owner before opening private UI', async () => {
  const request = owner(); const storage = vi.spyOn(Storage.prototype, 'setItem'); mount();
  expect(screen.queryByLabelText('Local access key')).not.toBeInTheDocument();
  await signIn(); expect(await screen.findByText('Private customer information')).toBeVisible();
  expect(sessionMemory.current?.actorId).toBe('A100'); expect(storage).not.toHaveBeenCalled();
  const call = request.mock.calls.find(([url]) => url.endsWith('/sign-in'));
  expect(JSON.parse(typeof call?.[1]?.body === 'string' ? call[1].body : '{}')).toEqual({ tenantId: 'T001', email: 'person@example.test', password: 'a long password value' });
  await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
  expect(await screen.findByLabelText('Password')).toHaveValue(''); expect(sessionMemory.current).toBeNull();
  expect(request.mock.calls.some(([url]) => url.endsWith('/revoke'))).toBe(true);
});
it.each([401, 429, 503])('keeps a rejected %s password private and displays the correct recovery guidance', async status => {
  owner(status); mount(); await signIn();
  expect(await screen.findByText(status === 401 ? /Check your organization, email and password/ : status === 429 ? /Too many attempts/ : /Workspace verification could not be completed/)).toBeVisible();
  expect(screen.getByLabelText('Password')).toHaveValue(''); expect(screen.queryByText('Private customer information')).not.toBeInTheDocument();
  expect(screen.queryByText(/verification code is invalid/)).not.toBeInTheDocument(); expect(screen.queryByText(/private owner detail/)).not.toBeInTheDocument();
});
it.each([403, 503])('revokes the issued session when workspace authorization fails with %s', async status => {
  const request = owner(200, status); mount(); await signIn();
  await waitFor(() => { expect(request.mock.calls.some(([url]) => url.endsWith('/revoke'))).toBe(true); });
  expect(screen.queryByText('Private customer information')).not.toBeInTheDocument(); expect(sessionMemory.current).toBeNull();
});
it('keeps an authenticated customer workspace open after a feature-level 403 denial', async () => {
  const request = owner();
  const original = request.getMockImplementation();
  if (!original) throw Error('Missing request handler');
  request.mockImplementation((url, options) =>
    url.endsWith('/v1/financial-profile')
      ? Promise.resolve(json({ error: 'ACCESS_DENIED' }, 403))
      : original(url, options));
  mountRestricted();
  await signIn();
  expect(await screen.findByText('Private customer information')).toBeVisible();
  await userEvent.click(screen.getByRole('button', { name: 'Try restricted feature' }));
  expect(await screen.findByRole('alert')).toHaveTextContent(/not included in your current access/i);
  expect(screen.getByText('Private customer information')).toBeVisible();
  expect(sessionMemory.current?.actorId).toBe('A100');
  expect(request.mock.calls.some(([url]) => url.endsWith('/revoke'))).toBe(false);
});
it('allows cancellation during password issuance and revokes a late session', async () => {
  const request = owner(); const fallback = request.getMockImplementation(); if (!fallback) throw new Error('Missing mock'); let finish: (value: Response) => void = () => undefined;
  request.mockImplementation((url, options) => url.endsWith('/sign-in') ? new Promise<Response>(resolve => { finish = resolve; }) : fallback(url, options));
  mount(); await signIn(); await userEvent.click(screen.getByRole('button', { name: 'Use another sign-in method' }));
  await act(async () => { finish(json(session())); await Promise.resolve(); });
  await waitFor(() => { expect(request.mock.calls.some(([url]) => url.endsWith('/revoke'))).toBe(true); });
  expect(screen.getByLabelText('Local access key')).toBeVisible(); expect(screen.queryByText('Private customer information')).not.toBeInTheDocument();
});
it('does not replace an explicit local choice when method discovery finishes late', async () => {
  const request = owner(); const fallback = request.getMockImplementation(); if (!fallback) throw new Error('Missing mock'); let finish: (value: Response) => void = () => undefined;
  request.mockImplementation((url, options) => url.endsWith('/customer-registration') ? new Promise<Response>(resolve => { finish = resolve; }) : fallback(url, options));
  mount(); await userEvent.click(screen.getByRole('button', { name: 'Use a local test key' }));
  await act(async () => { finish(json({ enabled: true })); await Promise.resolve(); });
  expect(screen.getByLabelText('Local access key')).toBeVisible(); expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: 'Sign in or create an account' })); expect(screen.getByLabelText('Password')).toBeVisible();
});
it('rejects a foreign-tenant session before requesting the customer workspace', async () => {
  const request = owner(); const fallback = request.getMockImplementation(); if (!fallback) throw new Error('Missing mock');
  request.mockImplementation((url, options) => url.endsWith('/sign-in') ? Promise.resolve(json({ ...session(), tenantId: 'T002' })) : fallback(url, options));
  mount(); await signIn(); await screen.findByText(/Workspace verification could not be completed/);
  expect(request.mock.calls.some(([url]) => url.endsWith('/v1/context'))).toBe(false); expect(sessionMemory.current).toBeNull();
});
it('closes an expired password session before any further private interaction', async () => {
  const request = owner(); const fallback = request.getMockImplementation(); if (!fallback) throw new Error('Missing mock'); const expires = Date.now() + 60000;
  request.mockImplementation((url, options) => url.endsWith('/sign-in') ? Promise.resolve(json({ ...session(), expiresAt: new Date(expires).toISOString() })) : fallback(url, options));
  mount(); await signIn(); await screen.findByText('Private customer information');
  vi.spyOn(Date, 'now').mockReturnValue(expires + 1); fireEvent(window, new PopStateEvent('popstate'));
  expect(await screen.findByText(/Your session has ended/)).toBeVisible(); expect(screen.queryByText('Private customer information')).not.toBeInTheDocument(); expect(sessionMemory.current).toBeNull();
});
it('offers no password form when it is not configured', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ enabled: false }))); mount();
  expect(await screen.findByLabelText('Local access key')).toBeVisible(); expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
});
