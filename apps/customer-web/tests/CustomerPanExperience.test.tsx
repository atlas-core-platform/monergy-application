import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import CustomerPanExperience from '../src/product/CustomerPanExperience';
const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'current-session', subjectVersion: 3, expiresAt: '2027-01-01T00:00:00Z' };
const product = { tenantId: 'T001', actorId: 'A100', customerId: 'C001', customerName: 'Fixture', capabilities: [], environment: 'Test environment', dataDurability: 'Temporary data', productionAccepted: false };
const publication = 'a'.repeat(64); const ref = 'b'.repeat(64); const synthetic = 'ABCDE1234F';
const empty = () => ({ tenantId: 'T001', actorId: 'A100', customerId: 'C001', enabled: true, canSave: true, agreementRevision: publication as string | null,
  maskedPan: null as string | null, version: 0, verificationReference: null as string | null, updatedAt: null as string | null });
const stored = () => ({ ...empty(), maskedPan: '******234F', version: 1, verificationReference: ref, updatedAt: '2026-01-01T00:00:00Z' });
function state(api: AccessApi, customer = 'C001'): WorkspaceSessionState {
  return { status: 'authorized', session, api, authorization: { area: 'product', context: { ...product, customerId: customer } }, message: '', recovery: null,
    connect: vi.fn(), retry: vi.fn(), signOut: vi.fn(), cancel: vi.fn(), refreshContext: vi.fn() };
}
function mount(value: WorkspaceSessionState) {
  const wrap = (v: WorkspaceSessionState) => <FoundationProvider appearance="midnight"><Context value={v}><CustomerPanExperience /></Context></FoundationProvider>;
  const view = render(wrap(value)); return { ...view, update: (v: WorkspaceSessionState) => { view.rerender(wrap(v)); } };
}
async function open() { fireEvent.click(screen.getByRole('button', { name: 'Manage PAN' })); await screen.findByText(/Last verified PAN/); }
function enter() {
  fireEvent.change(screen.getByLabelText('PAN'), { target: { value: synthetic } });
  fireEvent.click(screen.getByRole('checkbox', { name: /my self-declared PAN/ }));
}
const submit = () => { fireEvent.click(screen.getByRole('button', { name: 'Save PAN' })); };
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
it('loads sensitive profile details only after explicit opening and begins with no identifier or confirmation', async () => {
  const api = new AccessApi(session); const call = vi.spyOn(api, 'selfCustomerPan').mockResolvedValue(empty()); mount(state(api));
  expect(call).not.toHaveBeenCalled(); await open();
  expect(screen.getByLabelText('PAN')).toHaveValue(''); expect(screen.getByRole('checkbox')).not.toBeChecked();
  expect(screen.getByRole('button', { name: 'Save PAN' })).toBeDisabled();
});
it('records an explicit PAN then verifies an opaque receipt and fresh mask without retaining the full input', async () => {
  const api = new AccessApi(session); let saved = false;
  const call = vi.spyOn(api, 'selfCustomerPan').mockImplementation((method, body) => {
    if (method === 'PUT') { saved = true; return Promise.resolve({ operationId: (body as { operationId: string }).operationId, version: 1, verificationReference: ref }); }
    return Promise.resolve(saved ? stored() : empty());
  });
  mount(state(api)); await open(); enter(); submit();
  expect(await screen.findByText('Your PAN has been saved.')).toBeVisible(); expect(screen.getByText('******234F')).toBeVisible();
  expect(screen.queryByDisplayValue(synthetic)).not.toBeInTheDocument(); expect(screen.queryByText(synthetic)).not.toBeInTheDocument();
  expect(call.mock.calls.map(([method]) => method)).toEqual(['GET', 'PUT', 'GET']);
  expect(call.mock.calls[1]?.[1]).toEqual({ operationId: expect.stringMatching(/^[0-9a-f]{32}$/), expectedVersion: 0, pan: synthetic, confirm: true, agreementRevision: publication });
});
it('clears the full draft immediately when a save begins even while its receipt is pending', async () => {
  const api = new AccessApi(session);
  const call = vi.spyOn(api, 'selfCustomerPan').mockImplementation(method => method === 'PUT' ? new Promise(() => undefined) : Promise.resolve(empty()));
  mount(state(api)); await open(); enter(); submit();
  await waitFor(() => { expect(call).toHaveBeenCalledTimes(2); }); expect(screen.getByLabelText('PAN')).toHaveValue('');
  expect(screen.getByRole('checkbox')).not.toBeChecked();
});
it('validates the entered format and resets confirmation whenever its value changes', async () => {
  const api = new AccessApi(session); const call = vi.spyOn(api, 'selfCustomerPan').mockResolvedValue(empty()); mount(state(api)); await open();
  fireEvent.change(screen.getByLabelText('PAN'), { target: { value: 'bad' } }); fireEvent.click(screen.getByRole('checkbox')); submit();
  expect(await screen.findByText(/Enter a PAN with five letters/)).toBeVisible(); expect(call).toHaveBeenCalledOnce();
  fireEvent.change(screen.getByLabelText('PAN'), { target: { value: synthetic } }); expect(screen.getByRole('checkbox')).not.toBeChecked();
});
it('allows explicit removal without current publication acceptance and confirms a null readback', async () => {
  const api = new AccessApi(session); let removed = false;
  const call = vi.spyOn(api, 'selfCustomerPan').mockImplementation((method, body) => {
    if (method === 'PUT') { removed = true; return Promise.resolve({ operationId: (body as { operationId: string }).operationId, version: 2, verificationReference: ref }); }
    return Promise.resolve({ ...stored(), canSave: false, agreementRevision: null, version: removed ? 2 : 1, maskedPan: removed ? null : '******234F' });
  });
  mount(state(api)); await open(); expect(screen.getByRole('button', { name: 'Replace PAN' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Remove PAN' })); expect(screen.getByRole('button', { name: 'Confirm PAN removal' })).toBeDisabled();
  fireEvent.click(screen.getByRole('checkbox', { name: /want to remove/ })); fireEvent.click(screen.getByRole('button', { name: 'Confirm PAN removal' }));
  expect(await screen.findByText('Your stored PAN has been removed.')).toBeVisible(); expect(screen.getByText('Not recorded')).toBeVisible();
  expect(call.mock.calls[1]?.[1]).toMatchObject({ pan: null, agreementRevision: null, confirm: true });
});
it('does not offer recording when keys or legal prerequisites are unavailable', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfCustomerPan').mockResolvedValue({ ...empty(), enabled: false, canSave: false, agreementRevision: null });
  mount(state(api)); fireEvent.click(screen.getByRole('button', { name: 'Manage PAN' }));
  expect(await screen.findByText('PAN is unavailable in this environment.')).toBeVisible(); expect(screen.queryByLabelText('PAN')).not.toBeInTheDocument();
});
it.each([409, 503])('blocks another mutation after an uncertain %s result until explicit reload', async code => {
  const api = new AccessApi(session); const call = vi.spyOn(api, 'selfCustomerPan').mockImplementation(method => method === 'PUT' ? Promise.reject(new AccessApiError(code, 'controlled')) : Promise.resolve(empty()));
  mount(state(api)); await open(); enter(); submit();
  expect(await screen.findByText(code === 409 ? /account agreements changed/ : /PAN change could not be confirmed/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Save PAN' })).toBeDisabled(); expect(screen.getByLabelText('PAN')).toHaveValue('');
  expect(call.mock.calls.filter(([method]) => method === 'PUT')).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Reload PAN' })); expect(await screen.findByLabelText('PAN')).toHaveValue('');
});
it.each(['scope', 'raw', 'revision'])('rejects an invalid %s owner response without displaying PAN details', async mutation => {
  const api = new AccessApi(session); let response = { ...stored() };
  if (mutation === 'scope') response = { ...response, customerId: 'C900' };
  if (mutation === 'raw') response = { ...response, maskedPan: synthetic };
  if (mutation === 'revision') response = { ...response, agreementRevision: null };
  vi.spyOn(api, 'selfCustomerPan').mockResolvedValue(response); mount(state(api)); fireEvent.click(screen.getByRole('button', { name: 'Manage PAN' }));
  expect(await screen.findByText(/PAN record could not be verified/)).toBeVisible(); expect(screen.queryByText(synthetic)).not.toBeInTheDocument(); expect(screen.queryByLabelText('PAN')).not.toBeInTheDocument();
});
it.each(['receipt', 'readback'])('refuses success when the %s verification reference is wrong', async mutation => {
  const api = new AccessApi(session); let saved = false;
  vi.spyOn(api, 'selfCustomerPan').mockImplementation((method, body) => {
    if (method === 'PUT') { saved = true; return Promise.resolve({ operationId: mutation === 'receipt' ? 'wrong' : (body as { operationId: string }).operationId, version: 1, verificationReference: ref }); }
    return Promise.resolve(saved ? { ...stored(), verificationReference: 'c'.repeat(64) } : empty());
  });
  mount(state(api)); await open(); enter(); submit(); expect(await screen.findByText(/PAN change could not be confirmed/)).toBeVisible();
  expect(screen.queryByText('Your PAN has been saved.')).not.toBeInTheDocument(); expect(screen.queryByDisplayValue(synthetic)).not.toBeInTheDocument();
});
it('clears local input on close and requires a fresh opening before further edits', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfCustomerPan').mockResolvedValue(empty()); mount(state(api)); await open(); enter();
  fireEvent.click(screen.getByRole('button', { name: 'Close PAN details' })); expect(screen.queryByDisplayValue(synthetic)).not.toBeInTheDocument();
  await open(); expect(screen.getByLabelText('PAN')).toHaveValue(''); expect(screen.getByRole('checkbox')).not.toBeChecked();
});
it('suppresses a late save after switching customer and API even when abort is ignored', async () => {
  const first = new AccessApi(session); let resolve: (v: unknown) => void = () => undefined;
  const call = vi.spyOn(first, 'selfCustomerPan').mockImplementation(method => method === 'PUT' ? new Promise(r => { resolve = r; }) : Promise.resolve(empty()));
  const view = mount(state(first)); await open(); enter(); submit(); await waitFor(() => { expect(call).toHaveBeenCalledTimes(2); });
  const second = new AccessApi(session); vi.spyOn(second, 'selfCustomerPan').mockResolvedValue({ ...stored(), customerId: 'C002', maskedPan: '******567G' });
  view.update(state(second, 'C002')); expect(await screen.findByText('******567G')).toBeVisible();
  await act(() => { resolve({ operationId: (call.mock.calls[1]?.[1] as { operationId: string }).operationId, version: 1, verificationReference: ref }); return Promise.resolve(); });
  expect(screen.queryByText('Your PAN has been saved.')).not.toBeInTheDocument(); expect(screen.queryByText('******234F')).not.toBeInTheDocument(); expect(call).toHaveBeenCalledTimes(2);
});
