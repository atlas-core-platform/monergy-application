import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { PersonaCredentialEntry } from '../src/access/PersonaCredentialEntry';

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const mount = () => render(<FoundationProvider appearance="midnight">
  <PersonaCredentialEntry activationMode="tester-link" returnPath="/advisor"
    busy={false} onSignIn={() => Promise.resolve()} onCancel={() => undefined} />
</FoundationProvider>);

it('never requests an invitation or reveals a ticket without protected tester authorization', async () => {
  const fetcher = vi.fn(() => Promise.resolve(json({ error: 'FORBIDDEN' }, 403)));
  vi.stubGlobal('fetch', fetcher);
  mount();
  expect(fetcher).not.toHaveBeenCalled();
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Activate your invited account (LOCAL/UAT)' }));
  expect(screen.getByText('Development tester access only')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Open activation page' })).not.toBeInTheDocument();
  fireEvent.change(screen.getByRole('textbox', { name: 'Your invited email address' }),
    { target: { value: 'a300@example.test' } });
  fireEvent.change(screen.getByLabelText('Protected LOCAL tester authorization'),
    { target: { value: 'wrong-secret' } });
  await user.click(screen.getByRole('button', { name: 'Request protected welcome link' }));
  expect(await screen.findByText(/protected tester welcome link is unavailable/)).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Open activation page' })).not.toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledOnce();
});

it('posts the tester credential in a header and binds the welcome destination to the Advisor route', async () => {
  const fetcher = vi.fn((url: string, options?: RequestInit) => {
    expect(url).toBe('/identity-api/local/v1/persona-credentials/tester/welcome-link');
    expect(options?.method).toBe('POST');
    return Promise.resolve(json({
      welcomePath: '/welcome#tenant=T001&ticket=' + 'a'.repeat(64) + '&return=%2Fadvisor',
      expiresAt: '2099-01-01T00:00:00Z',
    }));
  });
  vi.stubGlobal('fetch', fetcher);
  mount();
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Activate your invited account (LOCAL/UAT)' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Your invited email address' }),
    { target: { value: 'a300@example.test' } });
  fireEvent.change(screen.getByLabelText('Protected LOCAL tester authorization'),
    { target: { value: 'test-authorized-value' } });
  await user.click(screen.getByRole('button', { name: 'Request protected welcome link' }));
  expect(await screen.findByRole('button', { name: 'Open activation page' })).toBeVisible();
  // Successful issuance replaces the form; no credential input remains mounted.
  expect(screen.queryByLabelText('Protected LOCAL tester authorization')).not.toBeInTheDocument();
  const call = fetcher.mock.calls[0];
  expect(call?.[0]).toBe('/identity-api/local/v1/persona-credentials/tester/welcome-link');
  const requestOptions = call?.[1];
  expect(requestOptions?.headers).toMatchObject({ 'X-Monergy-Tester-Activation': 'test-authorized-value' });
  expect(typeof requestOptions?.body).toBe('string');
  if (typeof requestOptions?.body !== 'string') throw new Error('Expected JSON request body');
  const body = JSON.parse(requestOptions.body) as {
    tenantId: string; email: string; returnPath: string; requestId: string;
  };
  expect(body).toMatchObject({
    tenantId: 'T001', email: 'a300@example.test', returnPath: '/advisor',
  });
  expect(body.requestId).toMatch(/^[a-f0-9]{32}$/);
  expect(screen.queryByText('test-authorized-value')).not.toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledOnce();
});

it('reuses the same request id after an uncertain transport failure', async () => {
  const bodies: { requestId: string }[] = [];
  let attempt = 0;
  const fetcher = vi.fn((_url: string, options?: RequestInit) => {
    if (typeof options?.body === 'string')
      bodies.push(JSON.parse(options.body) as { requestId: string });
    attempt += 1;
    if (attempt === 1) return Promise.reject(new TypeError('connection interrupted'));
    return Promise.resolve(json({
      welcomePath: '/welcome#tenant=T001&ticket=' + 'b'.repeat(64) + '&return=%2Fadvisor',
      expiresAt: '2099-01-01T00:00:00Z',
    }));
  });
  vi.stubGlobal('fetch', fetcher);
  mount();
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Activate your invited account (LOCAL/UAT)' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Your invited email address' }),
    { target: { value: 'a300@example.test' } });
  fireEvent.change(screen.getByLabelText('Protected LOCAL tester authorization'),
    { target: { value: 'test-authorized-value' } });
  await user.click(screen.getByRole('button', { name: 'Request protected welcome link' }));
  expect(await screen.findByText(/protected tester welcome link is unavailable/)).toBeVisible();

  fireEvent.change(screen.getByLabelText('Protected LOCAL tester authorization'),
    { target: { value: 'test-authorized-value' } });
  await user.click(screen.getByRole('button', { name: 'Request protected welcome link' }));
  expect(await screen.findByRole('button', { name: 'Open activation page' })).toBeVisible();

  expect(fetcher).toHaveBeenCalledTimes(2);
  expect(bodies).toHaveLength(2);
  expect(bodies[0]?.requestId).toMatch(/^[a-f0-9]{32}$/);
  expect(bodies[1]?.requestId).toBe(bodies[0]?.requestId);
});

it('starts a fresh request id only for an intentional new welcome link', async () => {
  const requestIds: string[] = [];
  const fetcher = vi.fn((_url: string, options?: RequestInit) => {
    if (typeof options?.body === 'string') {
      const body = JSON.parse(options.body) as { requestId: string };
      requestIds.push(body.requestId);
    }
    const ticket = requestIds.length === 1 ? 'c'.repeat(64) : 'd'.repeat(64);
    return Promise.resolve(json({
      welcomePath: '/welcome#tenant=T001&ticket=' + ticket + '&return=%2Fadvisor',
      expiresAt: '2099-01-01T00:00:00Z',
    }));
  });
  vi.stubGlobal('fetch', fetcher);
  mount();
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Activate your invited account (LOCAL/UAT)' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Your invited email address' }),
    { target: { value: 'a300@example.test' } });
  fireEvent.change(screen.getByLabelText('Protected LOCAL tester authorization'),
    { target: { value: 'test-authorized-value' } });
  await user.click(screen.getByRole('button', { name: 'Request protected welcome link' }));
  expect(await screen.findByRole('button', { name: 'Open activation page' })).toBeVisible();

  await user.click(screen.getByRole('button', { name: 'Request another link' }));
  fireEvent.change(screen.getByLabelText('Protected LOCAL tester authorization'),
    { target: { value: 'test-authorized-value' } });
  await user.click(screen.getByRole('button', { name: 'Request protected welcome link' }));
  expect(await screen.findByRole('button', { name: 'Open activation page' })).toBeVisible();

  expect(requestIds).toHaveLength(2);
  expect(requestIds[0]).toMatch(/^[a-f0-9]{32}$/);
  expect(requestIds[1]).toMatch(/^[a-f0-9]{32}$/);
  expect(requestIds[1]).not.toBe(requestIds[0]);
});

