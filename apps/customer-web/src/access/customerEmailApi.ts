import { AccessApiError, sessionClientHeaders } from './accessApi';
import type { TenantSession } from './accessApi';

export interface CustomerEmailChallenge {
  challengeId: string;
  expiresAt: string;
  retryAfterSeconds: number;
}

async function request<T>(path: string, body: unknown, signal: AbortSignal): Promise<T> {
  let response: Response;
  try {
    response = await fetch('/identity-api/local/v1/customer-email-access/' + path, {
      method: 'POST',
      cache: 'no-store',
      redirect: 'error',
      headers: { 'Content-Type': 'application/json', ...(path === 'confirm' ? sessionClientHeaders() : {}) },
      body: JSON.stringify(body),
      signal: AbortSignal.any([signal, AbortSignal.timeout(20_000)]),
    });
    if (path !== 'confirm') signal.throwIfAborted();
  } catch (failure) {
    if (signal.aborted) throw failure;
    throw new AccessApiError(503, 'EMAIL_ACCESS_UNAVAILABLE');
  }
  if (!response.ok)
    throw new AccessApiError(response.status, response.status === 401 ? 'EMAIL_CODE_INVALID' : 'EMAIL_ACCESS_UNAVAILABLE');
  const result: unknown = await response.json().catch(() => null);
  // Return an issued session even when a test/transport ignores cancellation:
  // WorkspaceSessionProvider must revoke a superseded, unreturned credential.
  if (path !== 'confirm') signal.throwIfAborted();
  if (!result || typeof result !== 'object') throw new AccessApiError(503, 'EMAIL_RESPONSE_INVALID');
  return result as T;
}

export async function requestCustomerEmailCode(tenantId: string, email: string, signal: AbortSignal): Promise<CustomerEmailChallenge> {
  const result = await request<Partial<CustomerEmailChallenge>>('request', { tenantId, email }, signal);
  if (typeof result.challengeId !== 'string' || !/^[a-f0-9]{64}$/.test(result.challengeId) ||
      typeof result.expiresAt !== 'string' || !Number.isFinite(Date.parse(result.expiresAt)) ||
      Date.parse(result.expiresAt) <= Date.now() || Date.parse(result.expiresAt) > Date.now() + 6 * 60_000 ||
      result.retryAfterSeconds !== 60)
    throw new AccessApiError(503, 'EMAIL_RESPONSE_INVALID');
  return result as CustomerEmailChallenge;
}

export function confirmCustomerEmailCode(tenantId: string, challengeId: string, code: string, signal: AbortSignal): Promise<TenantSession> {
  return request<TenantSession>('confirm', { tenantId, challengeId, code }, signal);
}
