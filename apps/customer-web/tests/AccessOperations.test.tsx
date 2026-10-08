import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AccessOperations } from '../src/access/AccessOperations';
import { AccessApi } from '../src/access/accessApi';
const session = {
  tenantId: 'T001',
  actorId: 'A900',
  authenticationContextId: 'test-session',
  subjectVersion: 1,
  expiresAt: '2027-01-01T00:00:00Z',
};
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
describe('connected session administration', () => {
  it('requires review and submits the current policy revision to revoke all sessions', async () => {
    const calls: { url: string; options?: RequestInit }[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string, options?: RequestInit) => {
        calls.push({ url, options });
        if (url.includes('/context')) return Promise.resolve(json({ policyVersion: 12 }));
        if (url.includes('/revoke-sessions')) return Promise.resolve(json({ policyVersion: 13 }));
        return Promise.resolve(
          json({
            items: [
              {
                sessionReference: 'a'.repeat(64),
                actorId: 'A100',
                subjectVersion: 2,
                establishedAt: '2026-10-08T00:00:00Z',
                expiresAt: '2027-01-01T00:00:00Z',
                revoked: false,
              },
            ],
            nextCursor: null,
          }),
        );
      }),
    );
    render(
      <FoundationProvider>
        <AccessOperations api={new AccessApi(session)} onExpired={vi.fn()} />
      </FoundationProvider>,
    );
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /Sessions & security/ }));
    await user.click(await screen.findByRole('button', { name: 'Revoke all for A100' }));
    expect(calls.some((call) => call.url.includes('/revoke-sessions'))).toBe(false);
    await user.click(await screen.findByRole('button', { name: 'Revoke sessions', exact: true }));
    await waitFor(() => {
      expect(calls.some((call) => call.url.includes('/revoke-sessions'))).toBe(true);
    });
    const mutation = calls.find((call) => call.url.includes('/revoke-sessions'));
    expect(mutation?.options?.body).toBe(JSON.stringify({ expectedPolicyVersion: 12 }));
    expect(mutation?.options?.headers).toMatchObject({
      'X-Monergy-Tenant': 'T001',
      'X-Monergy-Session': 'test-session',
    });
    expect(await screen.findByText(/Session revocation committed/)).toBeVisible();
  }, 30000);
  it('reports current authority loss without showing another tenant or stale session data', async () => {
    const expired = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(json({ error: 'TENANT_SESSION_INVALID' }, 401)),
    );
    render(
      <FoundationProvider>
        <AccessOperations api={new AccessApi(session)} onExpired={expired} />
      </FoundationProvider>,
    );
    await userEvent.setup().click(screen.getByRole('button', { name: /Sessions & security/ }));
    await waitFor(() => {
      expect(expired).toHaveBeenCalledOnce();
    });
    expect(screen.queryByRole('button', { name: /Revoke all for/ })).not.toBeInTheDocument();
    expect(await screen.findByText(/Your session has ended/)).toBeVisible();
  });
});
