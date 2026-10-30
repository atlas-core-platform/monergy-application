import { AccessApiError, sessionClientHeaders } from './accessApi';
export async function registrationRequest<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
 const response = await fetch('/identity-api/local/v1/customer-registration' + path, { method: body === undefined ? 'GET' : 'POST', headers: body === undefined ? undefined : { 'Content-Type': 'application/json', ...(path === '/sign-in' ? sessionClientHeaders() : {}) }, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store', redirect: 'error', signal });
 if (!response.ok) throw new AccessApiError(response.status, 'REGISTRATION_REQUEST_FAILED');
 return await response.json() as T;
}
