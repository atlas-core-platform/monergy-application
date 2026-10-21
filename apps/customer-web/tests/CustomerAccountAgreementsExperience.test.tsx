import { createHash, webcrypto } from 'node:crypto';
import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import { WorkspaceSessionProvider } from '../src/access/WorkspaceSessionProvider';
import { WorkspaceEntry } from '../src/access/WorkspaceEntry';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import CustomerAccountAgreementsExperience from '../src/product/CustomerAccountAgreementsExperience';

const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'current-session', subjectVersion: 3, expiresAt: '2027-01-01T00:00:00Z' };
const context = { tenantId: 'T001', actorId: 'A100', customerId: 'C001', customerName: 'Customer', capabilities: [], environment: 'Test environment', dataDurability: 'Temporary data', productionAccepted: false };
const revision = 'a'.repeat(64);
const data = () => ({ tenantId: 'T001', actorId: 'A100', customerId: 'C001', publication: {
  revision, publishedAt: '2026-01-01T00:00:00Z', documents: ['TERMS', 'PRIVACY', 'CONSENT'].map(kind => {
    const body = 'Synthetic test fixture only. ' + kind;
    return { kind, title: 'Fixture ' + kind, version: '1.0', body, sha256: createHash('sha256').update(body).digest('hex') };
  }),
}, acceptance: null as { operationId: string; revision: string; acceptedAt: string } | null });
function state(api: AccessApi): WorkspaceSessionState {
  return { status: 'authorized', session, api, authorization: { area: 'product', context }, message: '', recovery: null,
    connect: vi.fn(), retry: vi.fn(), signOut: vi.fn(), cancel: vi.fn(), refreshContext: vi.fn() };
}
function mount(value: WorkspaceSessionState) {
  const wrap = (v: WorkspaceSessionState) => <FoundationProvider appearance="midnight"><Context value={v}><CustomerAccountAgreementsExperience /></Context></FoundationProvider>;
  const view = render(wrap(value)); return { ...view, update: (v: WorkspaceSessionState) => { view.rerender(wrap(v)); } };
}
async function chooseAll() {
  await screen.findByText('Synthetic test fixture only. TERMS');
  for (const box of screen.getAllByRole('checkbox')) fireEvent.click(box);
}
beforeEach(() => { vi.stubGlobal('crypto', webcrypto); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('displays exact plain-text documents and leaves every choice unchecked', async () => {
  const api = new AccessApi(session); const call = vi.spyOn(api, 'selfAccountAgreements').mockResolvedValue(data()); mount(state(api));
  expect(await screen.findByText('Synthetic test fixture only. TERMS')).toBeVisible();
  for (const box of screen.getAllByRole('checkbox')) expect(box).not.toBeChecked();
  expect(screen.getByRole('button', { name: 'Record agreement choices' })).toBeDisabled(); expect(call).toHaveBeenCalledOnce();
  fireEvent.click(screen.getByRole('checkbox', { name: 'I accept the displayed terms.' })); expect(screen.getByRole('button', { name: 'Record agreement choices' })).toBeDisabled();
});
it('records only explicit choices and exact revision, then verifies a fresh owner readback', async () => {
  const api = new AccessApi(session); let receipt: { operationId: string; revision: string; acceptedAt: string } | null = null;
  const call = vi.spyOn(api, 'selfAccountAgreements').mockImplementation((method, body) => {
    if (method === 'POST') { receipt = { operationId: (body as { operationId: string }).operationId, revision, acceptedAt: '2026-10-18T10:00:00Z' }; return Promise.resolve(receipt); }
    return Promise.resolve({ ...data(), acceptance: receipt });
  });
  mount(state(api)); await chooseAll(); fireEvent.click(screen.getByRole('button', { name: 'Record agreement choices' }));
  expect(await screen.findByText('Your agreement choices have been recorded.')).toBeVisible();
  expect(call.mock.calls.map(([m]) => m)).toEqual(['GET', 'POST', 'GET']);
  expect(call.mock.calls.find(([method]) => method === 'POST')?.[1]).toEqual({ operationId: expect.stringMatching(/^[0-9a-f]{32}$/), revision, acceptTerms: true, acknowledgePrivacy: true, consentToProcessing: true });
  expect(screen.queryByRole('checkbox')).not.toBeInTheDocument(); expect(screen.getByText('Current agreements recorded')).toBeVisible();
});
it('does not invent policies or enable acceptance when nothing has been published', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfAccountAgreements').mockResolvedValue({ ...data(), publication: null }); mount(state(api));
  expect(await screen.findByText('Account agreements have not been published for your organization.')).toBeVisible();
  expect(screen.queryByRole('checkbox')).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Record agreement choices' })).not.toBeInTheDocument();
});
it.each(['foreign', 'hash', 'duplicate', 'receipt'])('refuses a %s response before displaying agreement content', async kind => {
  const result = data();
  const first = result.publication.documents.at(0); if (!first) throw new Error('Missing fixture document');
  if (kind === 'foreign') result.customerId = 'C900';
  if (kind === 'hash') first.body = 'unverified replacement';
  if (kind === 'duplicate') first.kind = 'CONSENT';
  if (kind === 'receipt') result.acceptance = { operationId: 'b'.repeat(32), revision: 'b'.repeat(64), acceptedAt: '2026-10-18T10:00:00Z' };
  const api = new AccessApi(session); vi.spyOn(api, 'selfAccountAgreements').mockResolvedValue(result); mount(state(api));
  expect(await screen.findByText(/Your account agreements could not be verified/)).toBeVisible(); expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
});
it.each([409, 503])('blocks another save after an uncertain %s response until explicit reload', async code => {
  const api = new AccessApi(session); const call = vi.spyOn(api, 'selfAccountAgreements').mockImplementation(method => method === 'POST' ? Promise.reject(new AccessApiError(code, 'controlled')) : Promise.resolve(data()));
  mount(state(api)); await chooseAll(); fireEvent.click(screen.getByRole('button', { name: 'Record agreement choices' }));
  expect(await screen.findByText(code === 409 ? /agreement information changed/ : /result is not confirmed/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Record agreement choices' })).toBeDisabled(); expect(call.mock.calls.filter(([m]) => m === 'POST')).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Reload agreements' })); await chooseAll(); expect(screen.getByRole('button', { name: 'Record agreement choices' })).toBeEnabled();
});
it('rejects a mismatched operation receipt without claiming success', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfAccountAgreements').mockImplementation(method => Promise.resolve(method === 'POST' ? { operationId: 'b'.repeat(32), revision, acceptedAt: '2026-10-18T10:00:00Z' } : data()));
  mount(state(api)); await chooseAll(); fireEvent.click(screen.getByRole('button', { name: 'Record agreement choices' }));
  expect(await screen.findByText(/result is not confirmed/)).toBeVisible(); expect(screen.queryByText('Your agreement choices have been recorded.')).not.toBeInTheDocument();
});
it('does not claim success when the fresh owner readback does not contain the saved acceptance', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfAccountAgreements').mockImplementation((method, body) => Promise.resolve(method === 'POST' ? { operationId: (body as { operationId: string }).operationId, revision, acceptedAt: '2026-10-18T10:00:00Z' } : data()));
  mount(state(api)); await chooseAll(); fireEvent.click(screen.getByRole('button', { name: 'Record agreement choices' }));
  expect(await screen.findByText(/result is not confirmed/)).toBeVisible(); expect(screen.queryByText('Your agreement choices have been recorded.')).not.toBeInTheDocument();
});
it('ignores late agreement content after customer/API change even when abort is ignored', async () => {
  const api = new AccessApi(session); let resolve: (v: unknown) => void = () => undefined;
  const first = vi.spyOn(api, 'selfAccountAgreements').mockImplementation(() => new Promise(r => { resolve = r; }));
  const view = mount(state(api)); await waitFor(() => { expect(first).toHaveBeenCalledOnce(); });
  const next = new AccessApi(session); vi.spyOn(next, 'selfAccountAgreements').mockResolvedValue({ ...data(), customerId: 'C002', publication: null });
  view.update({ ...state(next), authorization: { area: 'product', context: { ...context, customerId: 'C002' } } });
  expect(await screen.findByText(/have not been published/)).toBeVisible();
  await act(async () => { resolve(data()); await Promise.resolve(); });
  expect(screen.queryByText('Synthetic test fixture only. TERMS')).not.toBeInTheDocument();
});
it('shows no agreements or acceptance action outside an owned customer context', () => {
  const api = new AccessApi(session); const call = vi.spyOn(api, 'selfAccountAgreements'); mount({ ...state(api), authorization: null });
  expect(screen.getByText(/Connect to your own customer workspace/)).toBeVisible(); expect(call).not.toHaveBeenCalled();
});
it('keeps anonymous direct agreement entry behind the workspace gate', async () => {
  render(<FoundationProvider appearance="midnight"><WorkspaceSessionProvider area="product"><WorkspaceEntry area="product"><CustomerAccountAgreementsExperience /></WorkspaceEntry></WorkspaceSessionProvider></FoundationProvider>);
  fireEvent.click(await screen.findByRole('button', { name: 'Use a local test key' }));
  expect(await screen.findByLabelText('Local access key')).toBeVisible(); expect(screen.queryByRole('heading', { name: 'Account agreements' })).not.toBeInTheDocument();
});
it('does not follow a late save response after the customer context changes', async () => {
  const api = new AccessApi(session); let resolve: (v: unknown) => void = () => undefined; let operationId = '';
  const call = vi.spyOn(api, 'selfAccountAgreements').mockImplementation((method, body) => {
    if (method === 'POST') { operationId = (body as { operationId: string }).operationId; return new Promise(r => { resolve = r; }); }
    return Promise.resolve(data());
  });
  const view = mount(state(api)); await chooseAll(); fireEvent.click(screen.getByRole('button', { name: 'Record agreement choices' }));
  await waitFor(() => { expect(call.mock.calls.some(([method]) => method === 'POST')).toBe(true); });
  const next = new AccessApi(session); vi.spyOn(next, 'selfAccountAgreements').mockResolvedValue({ ...data(), customerId: 'C002', publication: null });
  view.update({ ...state(next), authorization: { area: 'product', context: { ...context, customerId: 'C002' } } });
  expect(await screen.findByText(/have not been published/)).toBeVisible();
  await act(async () => { resolve({ operationId, revision, acceptedAt: '2026-10-18T10:00:00Z' }); await Promise.resolve(); });
  expect(call.mock.calls.map(([method]) => method)).toEqual(['GET', 'POST']); expect(screen.queryByText('Your agreement choices have been recorded.')).not.toBeInTheDocument();
});
