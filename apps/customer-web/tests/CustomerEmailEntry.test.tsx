import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { CustomerEmailEntry } from '../src/access/CustomerEmailEntry';
import { WorkspaceEntry } from '../src/access/WorkspaceEntry';
import { WorkspaceSessionProvider } from '../src/access/WorkspaceSessionProvider';
import { requestCustomerEmailCode } from '../src/access/customerEmailApi';
import { sessionMemory } from '../src/access/workspaceSession';

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const challenge = () => ({ challengeId: 'a'.repeat(64), expiresAt: new Date(Date.now() + 300_000).toISOString(), retryAfterSeconds: 60 });
const session = () => ({ tenantId: 'T001', actorId: 'A100', authenticationContextId: 'verified-session', subjectVersion: 2, expiresAt: new Date(Date.now() + 1_800_000).toISOString() });
const product = { tenantId: 'T001', actorId: 'A100', customerId: 'C001', customerName: 'Current Customer', capabilities: ['financial-profile.profile.read'], environment: 'Test environment', dataDurability: 'Temporary data', productionAccepted: false };
const mount = (onVerify = vi.fn().mockResolvedValue(undefined), onCancel = vi.fn()) => {
  const view = render(<FoundationProvider appearance="midnight"><CustomerEmailEntry busy={false} onVerify={onVerify} onCancel={onCancel} /></FoundationProvider>);
  return { ...view, onVerify, onCancel };
};
function mockOwner(confirmStatus = 200) {
  const fetch = vi.fn((url: string) => {
    if (url.endsWith('/customer-email-access')) return Promise.resolve(json({ enabled: true }));
    if (url.endsWith('/request')) return Promise.resolve(json(challenge()));
    if (url.endsWith('/confirm')) return Promise.resolve(json(confirmStatus === 200 ? session() : { error: 'EMAIL_CODE_INVALID' }, confirmStatus));
    if (url.endsWith('/v1/context')) return Promise.resolve(json(product));
    if (url.endsWith('/revoke')) return Promise.resolve(json(true));
    return Promise.resolve(json({}, 404));
  });
  vi.stubGlobal('fetch', fetch);
  return fetch;
}
function workspace(area: 'product' | 'administration' = 'product') {
  return render(<FoundationProvider appearance="midnight"><WorkspaceSessionProvider area={area}><WorkspaceEntry area={area}><h2>Private customer information</h2></WorkspaceEntry></WorkspaceSessionProvider></FoundationProvider>);
}
async function send() {
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'owner@example.test' } });
  await userEvent.click(screen.getByRole('button', { name: 'Send verification code' }));
  return screen.findByLabelText('Verification code');
}
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); vi.useRealTimers(); });

it('requests a code with generic recipient wording and clears the code before verification', async () => {
  const fetch = mockOwner();
  const view = mount();
  const code = await send();
  expect(screen.getByText(/If your email is registered for a customer workspace/)).toBeVisible();
  expect(screen.getByRole('button', { name: /Request another code in/ })).toBeDisabled();
  fireEvent.change(code, { target: { value: '012345' } });
  await userEvent.click(screen.getByRole('button', { name: 'Verify and open workspace' }));
  await waitFor(() => { expect(view.onVerify).toHaveBeenCalledWith('T001', 'a'.repeat(64), '012345'); });
  expect(code).toHaveValue('');
  const request = fetch.mock.calls.find(([url]) => url.endsWith('/request'));
  expect(request).toBeDefined();
});

it('does not submit a malformed code or expose internal error details', async () => {
  mockOwner(); const view = mount(); const code = await send();
  fireEvent.change(code, { target: { value: '12345' } });
  await userEvent.click(screen.getByRole('button', { name: 'Verify and open workspace' }));
  expect(await screen.findByText('Enter the six-digit code.')).toBeVisible();
  expect(view.onVerify).not.toHaveBeenCalled();
});

it('reports delivery setup unavailability without saying a code was sent', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ error: 'private SMTP credential detail' }, 503)));
  mount();
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'owner@example.test' } });
  await userEvent.click(screen.getByRole('button', { name: 'Send verification code' }));
  expect(await screen.findByText('Email sign-in is unavailable. Contact your organization or try again later.')).toBeVisible();
  expect(screen.queryByLabelText('Verification code')).not.toBeInTheDocument();
  expect(screen.queryByText(/private SMTP/)).not.toBeInTheDocument();
});

it('rejects malformed or expired owner challenge responses', async () => {
  for (const result of [{ ...challenge(), challengeId: 'short' }, { ...challenge(), expiresAt: '2020-01-01T00:00:00Z' }]) {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(result)));
    await expect(requestCustomerEmailCode('T001', 'owner@example.test', new AbortController().signal)).rejects.toMatchObject({ code: 'EMAIL_RESPONSE_INVALID' });
  }
});

it('ignores a late request after the user cancels, even when fetch ignores abort', async () => {
  let resolve: (value: Response) => void = () => undefined;
  vi.stubGlobal('fetch', vi.fn().mockImplementation(() => new Promise<Response>(r => { resolve = r; })));
  const view = mount();
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'owner@example.test' } });
  await userEvent.click(screen.getByRole('button', { name: 'Send verification code' }));
  await userEvent.click(screen.getByRole('button', { name: 'Use another sign-in method' }));
  await act(async () => { resolve(json(challenge())); await Promise.resolve(); });
  expect(view.onCancel).toHaveBeenCalledOnce();
  expect(screen.queryByLabelText('Verification code')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Email')).toBeVisible();
});

it('opens private UI only after email confirmation and owner context both succeed, with no browser persistence', async () => {
  mockOwner();
  const storage = vi.spyOn(Storage.prototype, 'setItem');
  workspace();
  await userEvent.click(await screen.findByRole('button', { name: 'Use an email code' }));
  const code = await send();
  expect(screen.queryByText('Private customer information')).not.toBeInTheDocument();
  fireEvent.change(code, { target: { value: '123456' } });
  await userEvent.click(screen.getByRole('button', { name: 'Verify and open workspace' }));
  expect(await screen.findByText('Private customer information')).toBeVisible();
  expect(sessionMemory.current?.actorId).toBe('A100');
  expect(storage).not.toHaveBeenCalled();
});

it('keeps private UI closed after code rejection and allows another code attempt', async () => {
  mockOwner(401); workspace();
  await userEvent.click(await screen.findByRole('button', { name: 'Use an email code' }));
  const code = await send(); fireEvent.change(code, { target: { value: '123456' } });
  await userEvent.click(screen.getByRole('button', { name: 'Verify and open workspace' }));
  expect(await screen.findByText(/The verification code is invalid, expired or already used/)).toBeVisible();
  expect(screen.queryByText('Private customer information')).not.toBeInTheDocument();
  expect(code).toHaveValue('');
  expect(screen.getByRole('button', { name: 'Verify and open workspace' })).toBeEnabled();
});

it('revokes a session returned after cancellation and never reveals the private workspace', async () => {
  const fetch = mockOwner();
  let resolve: (value: Response) => void = () => undefined;
  fetch.mockImplementation((url: string) => {
    if (url.endsWith('/confirm')) return new Promise<Response>(r => { resolve = r; });
    if (url.endsWith('/customer-email-access')) return Promise.resolve(json({ enabled: true }));
    if (url.endsWith('/request')) return Promise.resolve(json(challenge()));
    return Promise.resolve(json(true));
  });
  workspace();
  await userEvent.click(await screen.findByRole('button', { name: 'Use an email code' }));
  const code = await send(); fireEvent.change(code, { target: { value: '123456' } });
  await userEvent.click(screen.getByRole('button', { name: 'Verify and open workspace' }));
  await userEvent.click(screen.getByRole('button', { name: 'Use another sign-in method' }));
  await act(async () => { resolve(json(session())); await Promise.resolve(); });
  await waitFor(() => { expect(fetch.mock.calls.some(([url]) => url.endsWith('/revoke'))).toBe(true); });
  expect(screen.queryByText('Private customer information')).not.toBeInTheDocument();
  expect(sessionMemory.current).toBeNull();
});

it('never offers customer email access as an administrator authentication method', () => {
  const fetch = mockOwner(); workspace('administration');
  expect(screen.getByLabelText('Local access key')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Use an email code' })).not.toBeInTheDocument();
  expect(fetch).not.toHaveBeenCalled();
});

it('expires a waiting code and enables resend after the owner cooldown', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  mockOwner(); mount();
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'owner@example.test' } });
  const form = screen.getByRole('button', { name: 'Send verification code' }).closest('form');
  if (!form) throw new Error('Email entry form is missing.');
  fireEvent.submit(form);
  await act(async () => { await vi.advanceTimersByTimeAsync(50); });
  expect(screen.getByLabelText('Verification code')).toBeEnabled();
  await act(async () => { await vi.advanceTimersByTimeAsync(301_000); });
  expect(screen.getByLabelText('Verification code')).toBeDisabled();
  expect(screen.getByText('This code has expired. Request a new code to continue.')).toBeVisible();
  expect(screen.getByRole('button', { name: 'Request another code' })).toBeEnabled();
});
