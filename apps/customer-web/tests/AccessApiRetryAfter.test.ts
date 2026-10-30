import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';

const session = { tenantId: 'T001', actorId: 'owner-001', authenticationContextId: 'session-1', subjectVersion: 1, expiresAt: '2099-01-01T00:00:00Z' };
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });
async function capture(request: Promise<unknown>): Promise<unknown> {
  try { await request; } catch (error: unknown) { return error; }
  throw new Error('Expected request failure.');
}

it('retains only a bounded numeric Retry-After delay from a governed provider failure', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ error: 'DRIVE_RATE_LIMITED' }), {
    status: 429, headers: { 'Content-Type': 'application/json', 'Retry-After': '60' },
  })));
  const error = await capture(new AccessApi(session).googleDrive('files'));
  expect(error).toBeInstanceOf(AccessApiError);
  expect(error).toMatchObject({ status: 429, code: 'DRIVE_RATE_LIMITED', retryAfterSeconds: 60 });
});

it('rejects unbounded or non-numeric provider retry timing', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ error: 'DRIVE_RATE_LIMITED' }), {
    status: 429, headers: { 'Content-Type': 'application/json', 'Retry-After': '3600' },
  })));
  const error = await capture(new AccessApi(session).oneDrive('files'));
  expect(error).toMatchObject({ status: 429, code: 'DRIVE_RATE_LIMITED', retryAfterSeconds: undefined });
});
