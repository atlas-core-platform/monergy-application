import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { PersonaCredentialEntry } from '../src/access/PersonaCredentialEntry';

const password = 'a secure test password 2026';
function renderEntry(onSignIn = vi.fn(() => Promise.resolve())) {
  render(<FoundationProvider appearance="midnight"><PersonaCredentialEntry
    busy={false} onSignIn={onSignIn} onCancel={() => undefined} /></FoundationProvider>);
  return onSignIn;
}
async function emailAndTenant(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByRole('textbox', { name: 'Email address' }), 'a300@example.test');
}
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

it('submits email/password without asking for an actor ID or LOCAL access key', async () => {
  const login = renderEntry();
  const user = userEvent.setup();
  await emailAndTenant(user);
  await user.type(screen.getByLabelText('Password'), password);
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  await waitFor(() => {
    expect(login).toHaveBeenCalledWith('T001', 'a300@example.test', password);
  });
  expect(screen.queryByLabelText('Local access key')).not.toBeInTheDocument();
});

it('activates a pre-provisioned account through a purpose-bound email challenge without issuing a session', async () => {
  const signIn = renderEntry();
  const fetch = vi.fn((url: string) => Promise.resolve(new Response(JSON.stringify(
    url.endsWith('/activation/request')
      ? { challengeId: 'a'.repeat(64), expiresAt: '2099-01-01T00:00:00Z' }
      : { activated: true }),
  { status: 200, headers: { 'Content-Type': 'application/json' } })));
  vi.stubGlobal('fetch', fetch);
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Activate account' }));
  await emailAndTenant(user);
  await user.click(screen.getByRole('button', { name: 'Send verification code' }));
  await screen.findByLabelText('Six-digit verification code');
  await user.type(screen.getByRole('textbox', { name: 'Six-digit verification code' }), '123456');
  await user.type(screen.getByLabelText('New password (15–128 characters)'), password);
  await user.click(screen.getByRole('button', { name: 'Verify and activate' }));
  expect(await screen.findByText(/Your password is active/)).toBeVisible();
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(signIn).not.toHaveBeenCalled();
});
