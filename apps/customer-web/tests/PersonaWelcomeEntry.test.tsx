import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { PersonaWelcomeEntry } from '../src/access/PersonaWelcomeEntry';

const password = 'a secure test password 2026';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  window.history.replaceState(null, '', '/');
});

function mount(status = 200) {
  const request = vi.fn<(url: string, options?: RequestInit) => Promise<Response>>((url) => Promise.resolve(new Response(
    JSON.stringify(url.endsWith('/entry-api/local/v1/context')
      ? { kind: 'tenant', tenantId: 'T001', displayName: 'Reference organization' }
      : status === 200 ? { activated: true } : { error: 'WELCOME_LINK_INVALID' }),
    { status: url.endsWith('/entry-api/local/v1/context') ? 200 : status, headers: { 'Content-Type': 'application/json' } },
  )));
  vi.stubGlobal('fetch', request);
  window.history.replaceState(null, '',
    '/welcome#tenant=T001&ticket=' + 'a'.repeat(64) + '&return=%2Fadvisor');
  render(<FoundationProvider appearance="midnight"><PersonaWelcomeEntry /></FoundationProvider>);
  return request;
}

it('clears the ticket from the URL, sets a password, and requires a separate normal sign-in', async () => {
  const request = mount();
  expect(window.location.pathname).toBe('/welcome');
  expect(window.location.hash).toBe('');
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText('Password (15–128 characters)'), password);
  await user.type(screen.getByLabelText('Confirm password'), password);
  await user.click(screen.getByRole('button', { name: 'Set password' }));
  expect(await screen.findByText('Your account is ready. Continue to sign in.')).toBeVisible();
  expect(screen.getByRole('link', { name: 'Continue to sign in' })).toHaveAttribute(
    'href',
    '/advisor',
  );
  expect(request).toHaveBeenCalledTimes(2);
  const options = request.mock.calls[1]?.[1];
  expect(JSON.parse(typeof options?.body === 'string' ? options.body : '')).toEqual({
    tenantId: 'T001', ticket: 'a'.repeat(64), password,
  });
});

it('shows a safe message when a ticket is expired or replayed', async () => {
  mount(401);
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText('Password (15–128 characters)'), password);
  await user.type(screen.getByLabelText('Confirm password'), password);
  await user.click(screen.getByRole('button', { name: 'Set password' }));
  await waitFor(() => {
    expect(screen.getByText(/invalid, expired, or has already been used/)).toBeVisible();
  });
  expect(screen.queryByRole('link', { name: 'Continue to sign in' })).not.toBeInTheDocument();
});

it('uses the protected recovery owner without exposing the replacement password', async () => {
  const request = mount();
  window.location.hash = 'tenant=T001&ticket=' + 'b'.repeat(64) + '&return=%2Fadvisor&mode=recovery';
  await waitFor(() => expect(screen.getByText(/Choose a new password/)).toBeVisible());
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Password (15–128 characters)'), password);
  await user.type(screen.getByLabelText('Confirm password'), password);
  await user.click(screen.getByRole('button', { name: 'Set password' }));
  await waitFor(() => { expect(request).toHaveBeenCalled(); });
  const call = request.mock.calls.at(-1);
  expect(call?.[0]).toBe('/identity-api/local/v1/persona-credentials/tester/recovery/confirm');
  expect(JSON.parse(typeof call?.[1]?.body === 'string' ? call[1].body : '')).toEqual({
    tenantId: 'T001', ticket: 'b'.repeat(64), newPassword: password,
    operationId: expect.stringMatching(/^[0-9a-f]{32}$/),
  });
  expect(window.location.hash).toBe('');
});

it('reconciles a committed recovery after the first acknowledgement is lost', async () => {
  let confirmations = 0;
  const request = vi.fn<(url: string, options?: RequestInit) => Promise<Response>>((url) => {
    if (url.endsWith('/entry-api/local/v1/context')) return Promise.resolve(new Response(JSON.stringify(
      { kind: 'tenant', tenantId: 'T001', displayName: 'Reference organization' }),
    { status: 200, headers: { 'Content-Type': 'application/json' } }));
    confirmations++;
    if (confirmations === 1) return Promise.reject(new TypeError('lost acknowledgement'));
    return Promise.resolve(new Response(JSON.stringify({ recovered: true }),
      { status: 200, headers: { 'Content-Type': 'application/json' } }));
  });
  vi.stubGlobal('fetch', request);
  window.history.replaceState(null, '', '/welcome#tenant=T001&ticket=' + 'c'.repeat(64) +
    '&return=%2Fadvisor&mode=recovery');
  render(<FoundationProvider appearance="midnight"><PersonaWelcomeEntry /></FoundationProvider>);
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText('Password (15–128 characters)'), password);
  await user.type(screen.getByLabelText('Confirm password'), password);
  await user.click(screen.getByRole('button', { name: 'Set password' }));
  expect(await screen.findByText('Your account is ready. Continue to sign in.')).toBeVisible();
  const confirmationsSent = request.mock.calls.filter(([url]) => url.endsWith('/tester/recovery/confirm'));
  expect(confirmationsSent).toHaveLength(2);
  expect(confirmationsSent[0]?.[1]?.body).toBe(confirmationsSent[1]?.[1]?.body);
});

it('rejects a later welcome link for a different hostname tenant in the same tab', async () => {
  mount();
  window.location.hash = 'tenant=T002&ticket=' + 'b'.repeat(64) + '&return=%2Faccess';
  await waitFor(() => {
    expect(window.location.hash).toBe('');
    expect(screen.getByText(/different organization address/)).toBeVisible();
  });
});
