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
  const request = vi.fn<(url: string, options?: RequestInit) => Promise<Response>>(() => Promise.resolve(new Response(
    JSON.stringify(status === 200 ? { activated: true } : { error: 'WELCOME_LINK_INVALID' }),
    { status, headers: { 'Content-Type': 'application/json' } },
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
  await user.type(screen.getByLabelText('Password (15–128 characters)'), password);
  await user.type(screen.getByLabelText('Confirm password'), password);
  await user.click(screen.getByRole('button', { name: 'Set password' }));
  expect(await screen.findByText('Your account is ready. Continue to sign in.')).toBeVisible();
  expect(screen.getByRole('link', { name: 'Continue to sign in' })).toHaveAttribute(
    'href',
    '/advisor?organization=T001',
  );
  expect(request).toHaveBeenCalledTimes(1);
  const options = request.mock.calls[0]?.[1];
  expect(JSON.parse(typeof options?.body === 'string' ? options.body : '')).toEqual({
    tenantId: 'T001', ticket: 'a'.repeat(64), password,
  });
});

it('shows a safe message when a ticket is expired or replayed', async () => {
  mount(401);
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Password (15–128 characters)'), password);
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
  });
  expect(window.location.hash).toBe('');
});

it('consumes and clears a later welcome link opened in the same tab', async () => {
  mount();
  window.location.hash = 'tenant=T002&ticket=' + 'b'.repeat(64) + '&return=%2Faccess';
  await waitFor(() => {
    expect(window.location.hash).toBe('');
    expect(screen.getByText(/Your organization is/)).toHaveTextContent('T002');
  });
});
