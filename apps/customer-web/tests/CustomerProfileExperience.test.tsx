import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context, useWorkspaceSession } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import { WorkspaceSessionProvider } from '../src/access/WorkspaceSessionProvider';
import { WorkspaceEntry } from '../src/access/WorkspaceEntry';
import CustomerProfileExperience from '../src/product/CustomerProfileExperience';

const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'current-session', subjectVersion: 3, expiresAt: '2027-01-01T00:00:00Z' };
const context = { tenantId: 'T001', actorId: 'A100', customerId: 'C001', customerName: 'Customer', capabilities: [], environment: 'Test environment', dataDurability: 'Temporary data', productionAccepted: false };
const profile = () => ({ tenantId: 'T001', actorId: 'A100', customerId: 'C001', displayName: 'Customer', email: 'owner@example.test', mobile: null as string | null, version: 2, updatedAt: null, emailVerifiedAt: null, dateOfBirth: null as string | null });
function state(api: AccessApi): WorkspaceSessionState {
  return { status: 'authorized', session, api, authorization: { area: 'product', context }, message: '', recovery: null,
    connect: vi.fn().mockResolvedValue(undefined), retry: vi.fn().mockResolvedValue(undefined), signOut: vi.fn().mockResolvedValue(undefined), cancel: vi.fn(), refreshContext: vi.fn().mockResolvedValue(undefined) };
}
function mount(value: WorkspaceSessionState) {
  const wrap = (v: WorkspaceSessionState) => <FoundationProvider appearance="midnight"><Context value={v}><CustomerProfileExperience /></Context></FoundationProvider>;
  const view = render(wrap(value));
  return { ...view, update: (v: WorkspaceSessionState) => { view.rerender(wrap(v)); } };
}
async function edit(name = 'Updated Customer') {
  fireEvent.change(await screen.findByLabelText('Your name'), { target: { value: name } });
}
function save() { fireEvent.click(screen.getByRole('button', { name: 'Save profile' })); }
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); window.history.replaceState({}, '', '/'); });

it('shows read-only registered email and accurately labels the unverified optional phone', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfCustomerProfile').mockResolvedValue(profile());
  mount(state(api));
  expect(await screen.findByText('owner@example.test')).toBeVisible();
  expect(screen.queryByRole('textbox', { name: /email/i })).not.toBeInTheDocument();
  expect(screen.getByText('Verification not recorded')).toBeVisible();
  expect(screen.getByText(/This number has not been verified for sign-in/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Save profile' })).toBeDisabled();
});

it('saves only editable fields with an operation and version, reloads owner state and refreshes the workspace label', async () => {
  const api = new AccessApi(session); let changed = false;
  const call = vi.spyOn(api, 'selfCustomerProfile').mockImplementation((method, body) => {
    if (method === 'PUT') { changed = true; const change = body as { operationId: string }; return Promise.resolve({ operationId: change.operationId, version: 3 }); }
    return Promise.resolve(changed ? { ...profile(), displayName: 'Updated Customer', mobile: '+919876543210', version: 3 } : profile());
  });
  const value = state(api); mount(value); await edit();
  fireEvent.change(screen.getByLabelText('Mobile number'), { target: { value: '+919876543210' } }); save();
  expect(await screen.findByText('Your profile has been saved.')).toBeVisible();
  const put = call.mock.calls.find(([method]) => method === 'PUT');
  expect(put?.[1]).toEqual({ operationId: expect.stringMatching(/^[0-9a-f]{32}$/), expectedVersion: 2, displayName: 'Updated Customer', mobile: '+919876543210', dateOfBirth: null });
  expect(call.mock.calls.map(([method]) => method)).toEqual(['GET', 'PUT', 'GET']);
  expect(value.refreshContext).toHaveBeenCalledOnce(); expect(screen.getByLabelText('Your name')).toHaveValue('Updated Customer');
});

it.each([409, 503])('blocks a repeat mutation after an unconfirmed %s save until explicit reload', async status => {
  const api = new AccessApi(session);
  const call = vi.spyOn(api, 'selfCustomerProfile').mockImplementation(method => method === 'PUT' ? Promise.reject(new AccessApiError(status, 'controlled')) : Promise.resolve(profile()));
  mount(state(api)); await edit(); save();
  expect(await screen.findByText(status === 409 ? /changed since it was loaded/ : /save result is not confirmed/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Save profile' })).toBeDisabled();
  expect(call.mock.calls.filter(([method]) => method === 'PUT')).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Discard changes and reload' }));
  expect(await screen.findByLabelText('Your name')).toHaveValue('Customer');
  await edit('Another Customer'); expect(screen.getByRole('button', { name: 'Save profile' })).toBeEnabled();
});

it('rejects a mismatched operation receipt without reloading or claiming success', async () => {
  const api = new AccessApi(session);
  const call = vi.spyOn(api, 'selfCustomerProfile').mockImplementation(method => Promise.resolve(method === 'PUT' ? { operationId: 'wrong', version: 3 } : profile()));
  const value = state(api); mount(value); await edit(); save();
  expect(await screen.findByText(/save result is not confirmed/)).toBeVisible();
  expect(call).toHaveBeenCalledTimes(2); expect(value.refreshContext).not.toHaveBeenCalled();
  expect(screen.queryByText('Your profile has been saved.')).not.toBeInTheDocument();
});

it('never displays a foreign customer profile returned by the service', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfCustomerProfile').mockResolvedValue({ ...profile(), customerId: 'C900', email: 'foreign@example.test' });
  mount(state(api)); expect(await screen.findByText(/Your profile could not be verified/)).toBeVisible();
  expect(screen.queryByText('foreign@example.test')).not.toBeInTheDocument(); expect(screen.queryByLabelText('Your name')).not.toBeInTheDocument();
});

it('ignores a late response after a customer and API change even when the dependency ignores abort', async () => {
  const first = new AccessApi(session); let resolve: (value: unknown) => void = () => undefined;
  const call = vi.spyOn(first, 'selfCustomerProfile').mockImplementation(() => new Promise(r => { resolve = r; }));
  const view = mount(state(first)); await waitFor(() => { expect(call).toHaveBeenCalledOnce(); });
  const second = new AccessApi(session); vi.spyOn(second, 'selfCustomerProfile').mockResolvedValue({ ...profile(), customerId: 'C002', email: 'current@example.test' });
  view.update({ ...state(second), authorization: { area: 'product', context: { ...context, customerId: 'C002' } } });
  expect(await screen.findByText('current@example.test')).toBeVisible();
  await act(async () => { resolve(profile()); await Promise.resolve(); });
  expect(screen.queryByText('owner@example.test')).not.toBeInTheDocument(); expect(screen.getByText('current@example.test')).toBeVisible();
});

it('validates an optional international phone before submitting and discards local edits explicitly', async () => {
  const api = new AccessApi(session); const call = vi.spyOn(api, 'selfCustomerProfile').mockResolvedValue(profile());
  mount(state(api)); await edit(); fireEvent.change(screen.getByLabelText('Mobile number'), { target: { value: 'invalid' } }); save();
  expect(await screen.findByText(/Include the country code/)).toBeVisible(); expect(call).toHaveBeenCalledOnce();
  fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
  expect(screen.getByLabelText('Your name')).toHaveValue('Customer'); expect(screen.getByLabelText('Mobile number')).toHaveValue('');
});

function LabelProbe() {
  const value = useWorkspaceSession();
  return <><button onClick={() => { void value.connect('T001', 'test-key'); }}>Connect test</button><span>{value.authorization?.area === 'product' ? value.authorization.context.customerName : 'Public'}</span><WorkspaceEntry area="product"><CustomerProfileExperience /></WorkspaceEntry></>;
}
it('refreshes the real session owner context after save while keeping the profile page mounted', async () => {
  let saved = false;
  vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
    let value: unknown = {};
    if (url.endsWith('/tenant-sessions')) value = session;
    else if (url.endsWith('/v1/context')) value = { ...context, customerName: saved ? 'Updated Customer' : 'Customer' };
    else if (url.endsWith('/customer-profile')) {
      if (options?.method === 'PUT') { saved = true; const change = JSON.parse(options.body as string) as { operationId: string }; value = { operationId: change.operationId, version: 3 }; }
      else value = saved ? { ...profile(), displayName: 'Updated Customer', version: 3 } : profile();
    }
    return Promise.resolve(new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } }));
  }));
  render(<FoundationProvider appearance="midnight"><WorkspaceSessionProvider area="product"><LabelProbe /></WorkspaceSessionProvider></FoundationProvider>);
  fireEvent.click(screen.getByRole('button', { name: 'Connect test' })); await edit();
  const heading = screen.getByRole('heading', { name: 'My Profile' }); save();
  expect(await screen.findByText('Your profile has been saved.')).toBeVisible();
  expect(screen.getByText('Updated Customer')).toBeVisible(); expect(screen.getByRole('heading', { name: 'My Profile' })).toBe(heading);
});

it('keeps anonymous direct profile entry behind the workspace gate', () => {
  window.history.replaceState({}, '', '/profile');
  render(<FoundationProvider appearance="midnight"><WorkspaceSessionProvider area="product"><WorkspaceEntry area="product"><CustomerProfileExperience /></WorkspaceEntry></WorkspaceSessionProvider></FoundationProvider>);
  expect(screen.getByLabelText('Local access key')).toBeVisible(); expect(screen.queryByRole('heading', { name: 'My Profile' })).not.toBeInTheDocument();
});

it('saves a date-only birth date and confirms the exact date through fresh owner readback', async () => {
  const api = new AccessApi(session); let saved = false;
  const call = vi.spyOn(api, 'selfCustomerProfile').mockImplementation((method, body) => {
    if (method === 'PUT') { saved = true; return Promise.resolve({ operationId: (body as { operationId: string }).operationId, version: 3 }); }
    return Promise.resolve(saved ? { ...profile(), dateOfBirth: '2000-02-29', version: 3 } : profile());
  });
  mount(state(api)); fireEvent.change(await screen.findByLabelText('Date of birth'), { target: { value: '2000-02-29' } }); save();
  expect(await screen.findByText('Your profile has been saved.')).toBeVisible();
  expect(call.mock.calls.find(([m]) => m === 'PUT')?.[1]).toMatchObject({ dateOfBirth: '2000-02-29', expectedVersion: 2 });
  expect(screen.getByLabelText('Date of birth')).toHaveValue('2000-02-29');
  expect(screen.getByText(/self-declared profile detail/)).toBeVisible();
});
it('explicitly clears an existing birth date and confirms null rather than inventing a default', async () => {
  const api = new AccessApi(session); let saved = false;
  const call = vi.spyOn(api, 'selfCustomerProfile').mockImplementation((method, body) => {
    if (method === 'PUT') { saved = true; return Promise.resolve({ operationId: (body as { operationId: string }).operationId, version: 3 }); }
    return Promise.resolve({ ...profile(), dateOfBirth: saved ? null : '1992-04-09', version: saved ? 3 : 2 });
  });
  mount(state(api)); expect(await screen.findByLabelText('Date of birth')).toHaveValue('1992-04-09');
  fireEvent.change(screen.getByLabelText('Date of birth'), { target: { value: '' } }); save();
  expect(await screen.findByText('Your profile has been saved.')).toBeVisible();
  expect(call.mock.calls.find(([m]) => m === 'PUT')?.[1]).toMatchObject({ dateOfBirth: null });
  expect(screen.getByLabelText('Date of birth')).toHaveValue('');
});
it('rejects a future birth date before requesting a save and allows explicit discard', async () => {
  const api = new AccessApi(session); const call = vi.spyOn(api, 'selfCustomerProfile').mockResolvedValue(profile());
  mount(state(api)); fireEvent.change(await screen.findByLabelText('Date of birth'), { target: { value: '2099-01-01' } }); save();
  expect(await screen.findByText(/Enter a valid date of birth/)).toBeVisible(); expect(call).toHaveBeenCalledOnce();
  fireEvent.click(screen.getByRole('button', { name: 'Discard changes' })); expect(screen.getByLabelText('Date of birth')).toHaveValue('');
  expect(screen.getByRole('button', { name: 'Save profile' })).toBeDisabled();
});
it.each(['2001-02-29', '2099-01-01', '1992-04-09T00:00:00Z', undefined])('rejects malformed or missing date-of-birth readback %s', async birthDate => {
  const api = new AccessApi(session); vi.spyOn(api, 'selfCustomerProfile').mockResolvedValue({ ...profile(), dateOfBirth: birthDate });
  mount(state(api)); expect(await screen.findByText(/Your profile could not be verified/)).toBeVisible();
  expect(screen.queryByLabelText('Date of birth')).not.toBeInTheDocument();
});
it('does not claim a saved birth date when the receipt version reloads a different date', async () => {
  const api = new AccessApi(session); const value = state(api);
  let saved = false;
  const call = vi.spyOn(api, 'selfCustomerProfile').mockImplementation((method, body) => {
    if (method === 'PUT') { saved = true; return Promise.resolve({ operationId: (body as { operationId: string }).operationId, version: 3 }); }
    return Promise.resolve({ ...profile(), version: saved ? 3 : 2 });
  });
  mount(value); fireEvent.change(await screen.findByLabelText('Date of birth'), { target: { value: '2000-02-29' } }); save();
  expect(await screen.findByText(/save result is not confirmed/)).toBeVisible(); expect(value.refreshContext).not.toHaveBeenCalled();
  expect(call.mock.calls.map(([method]) => method)).toEqual(['GET', 'PUT', 'GET']);
  expect(screen.queryByText('Your profile has been saved.')).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Save profile' })).toBeDisabled();
});
