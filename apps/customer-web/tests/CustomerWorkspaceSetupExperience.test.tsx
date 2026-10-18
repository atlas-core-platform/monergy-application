import { webcrypto, createHash } from 'node:crypto';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { App } from '../src/App';
import { AccessApi } from '../src/access/accessApi';
import { Context, useWorkspaceSession } from '../src/access/workspaceSession';
import type { CustomerWorkspaceSetup, WorkspaceSessionState } from '../src/access/workspaceSession';
import { WorkspaceSessionProvider } from '../src/access/WorkspaceSessionProvider';

const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'current-session', subjectVersion: 3, expiresAt: '2027-01-01T00:00:00Z' };
const pending: CustomerWorkspaceSetup = { tenantId: 'T001', actorId: 'A100', customerId: 'C001', required: true, emailVerified: true, profileReviewed: false, agreementsPublished: true, agreementsAccepted: false, ready: false, revision: 'a'.repeat(64) };
const product = { tenantId: 'T001', actorId: 'A100', customerId: 'C001', customerName: 'Customer', capabilities: [] as string[], environment: 'Test environment', dataDurability: 'Temporary data', productionAccepted: false, setup: pending };
const profile = () => ({ tenantId: 'T001', actorId: 'A100', customerId: 'C001', displayName: 'Customer', email: 'owner@example.test', mobile: null, version: 0, updatedAt: null as string | null, emailVerifiedAt: '2026-01-01T00:00:00Z', dateOfBirth: null as string | null });
function state(api: AccessApi, setup = pending): WorkspaceSessionState {
  return { status: 'authorized', session, api, authorization: { area: 'product', context: { ...product, setup } }, message: '', recovery: null,
    connect: vi.fn(), retry: vi.fn(), signOut: vi.fn(), cancel: vi.fn(), refreshContext: vi.fn() };
}
function mount(value: WorkspaceSessionState) {
  const wrap = (v: WorkspaceSessionState) => <Context value={v}><App /></Context>;
  const view = render(wrap(value)); return { ...view, update: (v: WorkspaceSessionState) => { view.rerender(wrap(v)); } };
}
beforeEach(() => { vi.stubGlobal('crypto', webcrypto); window.history.replaceState({}, '', '/finances'); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); window.history.replaceState({}, '', '/'); });

it('intercepts a financial deep link without exposing its shell or requesting financial data', async () => {
  const api = new AccessApi(session); const calls = vi.spyOn(api, 'product'); vi.spyOn(api, 'selfCustomerProfile').mockResolvedValue(profile());
  mount(state(api)); expect(await screen.findByRole('heading', { name: 'Finish setting up your account' })).toBeVisible();
  expect(screen.queryByRole('heading', { name: 'Your trusted financial information.' })).not.toBeInTheDocument();
  expect(screen.queryByRole('navigation')).not.toBeInTheDocument(); expect(calls).not.toHaveBeenCalled();
});
it('keeps agreements outstanding after the profile is saved and owner context is refreshed', async () => {
  const api = new AccessApi(session); let saved = false;
  vi.spyOn(api, 'selfCustomerProfile').mockImplementation((method, body) => {
    if (method === 'PUT') { saved = true; return Promise.resolve({ operationId: (body as { operationId: string }).operationId, version: 1 }); }
    return Promise.resolve(saved ? { ...profile(), displayName: 'Reviewed Customer', version: 1, updatedAt: '2026-10-18T10:00:00Z' } : profile());
  });
  const financial = vi.spyOn(api, 'product'); const value = state(api); const view = mount(value);
  value.refreshContext = vi.fn().mockImplementation(() => { view.update({ ...value, authorization: { area: 'product', context: { ...product, setup: { ...pending, profileReviewed: true } } } }); return Promise.resolve(); });
  fireEvent.change(await screen.findByLabelText('Your name'), { target: { value: 'Reviewed Customer' } }); fireEvent.click(screen.getByRole('button', { name: 'Save profile' }));
  expect(await screen.findByText('Your profile has been saved.')).toBeVisible(); expect(value.refreshContext).toHaveBeenCalledOnce();
  expect(screen.getByRole('heading', { name: 'Finish setting up your account' })).toBeVisible(); expect(financial).not.toHaveBeenCalled();
});
it('opens the financial route only after explicit agreement receipt, readback and fresh readiness', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfCustomerProfile').mockResolvedValue({ ...profile(), version: 1 });
  let receipt: { operationId: string; revision: string; acceptedAt: string } | null = null;
  const documents = ['TERMS', 'PRIVACY', 'CONSENT'].map(kind => {
    const body = 'Synthetic setup fixture only. ' + kind;
    return { kind, title: 'Setup ' + kind, version: '1.0', body, sha256: createHash('sha256').update(body).digest('hex') };
  });
  vi.spyOn(api, 'selfAccountAgreements').mockImplementation((method, body) => {
    if (method === 'POST') { receipt = { operationId: (body as { operationId: string }).operationId, revision: pending.revision ?? '', acceptedAt: '2026-10-18T10:00:00Z' }; return Promise.resolve(receipt); }
    return Promise.resolve({ tenantId: 'T001', actorId: 'A100', customerId: 'C001', publication: { revision: pending.revision, publishedAt: '2026-01-01T00:00:00Z', documents }, acceptance: receipt });
  });
  const financial = vi.spyOn(api, 'product').mockResolvedValue({ financialProfileId: 'FP001', revision: 1, updatedAt: '2026-10-18T10:00:00Z', facts: [], dataDurability: 'Temporary data' });
  const value = state(api, { ...pending, profileReviewed: true }); const view = mount(value);
  value.refreshContext = vi.fn().mockImplementation(() => { view.update({ ...value, authorization: { area: 'product', context: { ...product, capabilities: ['financial-profile.profile.read'], setup: { ...pending, profileReviewed: true, agreementsAccepted: true, ready: true } } } }); return Promise.resolve(); });
  fireEvent.click(await screen.findByRole('button', { name: 'Review account agreements' }));
  await screen.findByText('Synthetic setup fixture only. TERMS');
  expect(financial).not.toHaveBeenCalled();
  for (const box of screen.getAllByRole('checkbox')) fireEvent.click(box);
  fireEvent.click(screen.getByRole('button', { name: 'Record agreement choices' }));
  expect(await screen.findByRole('heading', { name: 'Your trusted financial information.' })).toBeVisible();
  expect(value.refreshContext).toHaveBeenCalledOnce(); await waitFor(() => { expect(financial).toHaveBeenCalledWith('v1/financial-profile', 'GET', undefined, expect.anything()); });
});
it('explains missing email and unpublished agreements while retaining sign-out and control actions', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfCustomerProfile').mockResolvedValue(profile());
  const value = state(api, { ...pending, emailVerified: false, agreementsPublished: false, revision: null }); mount(value);
  expect(await screen.findByText('Email verification is still required.')).toBeVisible();
  expect(screen.getByText('Your organization has not published the required account agreements yet.')).toBeVisible();
  expect(screen.getByRole('button', { name: 'Manage sharing permissions' })).toBeEnabled(); expect(screen.getByRole('button', { name: 'Manage sessions' })).toBeEnabled();
  fireEvent.click(screen.getByRole('button', { name: 'Sign out' })); expect(value.signOut).toHaveBeenCalledOnce();
});
it('does not turn a failed readiness refresh into admission', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfCustomerProfile').mockResolvedValue(profile()); const financial = vi.spyOn(api, 'product');
  const value = state(api); value.refreshContext = vi.fn().mockRejectedValue(new Error('unavailable')); mount(value);
  fireEvent.click(await screen.findByRole('button', { name: 'Check setup status' }));
  expect(await screen.findByText(/Your setup status could not be verified/)).toBeVisible(); expect(financial).not.toHaveBeenCalled();
});
function Probe() { const s = useWorkspaceSession(); return <><button onClick={() => { void s.connect('T001', 'key'); }}>Connect test</button><p>{s.status}</p><p>{s.message}</p></>; }
const json = (v: unknown) => new Response(JSON.stringify(v), { status: 200, headers: { 'Content-Type': 'application/json' } });
it.each(['scope', 'ready', 'capabilities', 'revision'])('rejects an invalid %s setup context before authorizing the browser', async mutation => {
  let context = { ...product, setup: { ...pending } };
  if (mutation === 'scope') context.setup.customerId = 'C900';
  if (mutation === 'ready') context.setup.ready = true;
  if (mutation === 'capabilities') context = { ...context, capabilities: ['financial-profile.profile.read'] };
  if (mutation === 'revision') context.setup.revision = null;
  vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(json(url.endsWith('/v1/context') ? context : url.endsWith('/revoke') ? true : session))));
  render(<WorkspaceSessionProvider area="product"><Probe /></WorkspaceSessionProvider>);
  await act(() => { fireEvent.click(screen.getByRole('button', { name: 'Connect test' })); return Promise.resolve(); });
  expect(await screen.findByText(/Workspace verification could not be completed/)).toBeVisible(); expect(screen.getByText('public')).toBeVisible();
});
