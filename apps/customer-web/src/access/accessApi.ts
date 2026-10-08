export type AccessArea =
  'members' | 'roles' | 'permissions' | 'groups' | 'imports' | 'resource-grants';
export interface TenantSession {
  tenantId: string;
  actorId: string;
  authenticationContextId: string;
  subjectVersion: number;
  expiresAt: string;
}
export interface Capability {
  capabilityId: string;
  service: string;
  displayName: string;
  resourceType: string | null;
  lifecycle: string;
}
export interface Grant {
  capabilityId: string;
  scope: 'Tenant' | 'Resource';
}
export interface AccessRecord {
  id?: string;
  actorId?: string;
  normalizedEmail?: string;
  code?: string;
  label?: string;
  status?: string;
  businessRoleId?: string | null;
  tenantAdmin?: boolean;
  subjectVersion?: number;
  grants?: Grant[];
  permissionIds?: string[];
  memberCount?: number;
  rowCount?: number;
  attempts?: number;
  errorCode?: string | null;
  capabilityId?: string;
  resourceType?: string;
  resourceId?: string;
}
export interface AccessPage {
  policyVersion: number;
  items: AccessRecord[];
  nextCursor: string | null;
}
export interface ImportError {
  rowNumber: number;
  field: string;
  code: string;
}
export interface ImportPreview {
  valid: boolean;
  policyVersion: number;
  contentHash: string;
  rowCount: number;
  errors: ImportError[];
}
export interface ImportResult {
  id: string;
  status: string;
  contentHash: string;
  rowCount: number;
  policyVersion: number;
  errorCode: string | null;
  identities: { rowNumber: number; normalizedEmail: string; actorId: string | null }[];
}

export class AccessApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
  ) {
    super(
      status === 401
        ? 'Your session has ended. Connect again to continue.'
        : status === 403
          ? 'Tenant administrator access is required.'
          : status === 409
            ? 'The workspace changed or this operation conflicts with current access. Refresh and review before retrying.'
            : status === 503
              ? 'A required service is unavailable. Your request has not been confirmed. Refresh before retrying.'
              : 'The request could not be completed. Review the details and try again.',
    );
    this.name = 'AccessApiError';
  }
}

async function read<T>(url: string, options: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(url, {
      cache: 'no-store',
      redirect: 'error',
      ...options,
      signal: options.signal ?? AbortSignal.timeout(20_000),
    });
  } catch (error) {
    if (options.signal?.aborted) throw error;
    throw new AccessApiError(503, 'CONNECTION_UNAVAILABLE');
  }
  if (!response.ok) {
    const data: unknown = await response.json().catch(() => null);
    const code =
      data &&
      typeof data === 'object' &&
      'error' in data &&
      typeof data.error === 'string' &&
      /^[A-Z_]{1,100}$/.test(data.error)
        ? data.error
        : 'REQUEST_FAILED';
    throw new AccessApiError(response.status, code);
  }
  return (await response.json()) as T;
}

export async function establishSession(tenantId: string, key: string): Promise<TenantSession> {
  return read<TenantSession>('/identity-api/local/v1/tenant-sessions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Monergy-Reference-Authentication': key },
    body: JSON.stringify({ tenantId }),
  });
}

// Session data stays in component memory. No credential/session is stored in a
// URL, browser storage, log or persisted user preference.
export class AccessApi {
  constructor(private readonly session: TenantSession) {}
  request<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<T> {
    return read<T>('/access-api/v1/' + path, {
      method,
      headers: {
        'Content-Type': 'application/json',
        'X-Monergy-Tenant': this.session.tenantId,
        'X-Monergy-Session': this.session.authenticationContextId,
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
  }
  owner<T>(owner: 'identity' | 'audit' | 'uat', path: string, signal?: AbortSignal): Promise<T> {
    return read<T>('/' + owner + '-api/' + path, {
      method: 'GET',
      signal,
      headers: {
        'X-Monergy-Tenant': this.session.tenantId,
        'X-Monergy-Session': this.session.authenticationContextId,
      },
    });
  }
  list(area: AccessArea, after?: string, signal?: AbortSignal): Promise<AccessPage> {
    return this.request<AccessPage>(
      'administration/' +
        area +
        '?limit=100' +
        (after ? '&after=' + encodeURIComponent(after) : ''),
      'GET',
      undefined,
      signal,
    );
  }
  async all(area: AccessArea, signal?: AbortSignal): Promise<AccessRecord[]> {
    const records: AccessRecord[] = [];
    let cursor: string | undefined;
    let revision: number | undefined;
    do {
      const page = await this.list(area, cursor, signal);
      if (revision !== undefined && page.policyVersion !== revision)
        throw new AccessApiError(409, 'POLICY_VERSION_CONFLICT');
      revision = page.policyVersion;
      records.push(...page.items);
      cursor = page.nextCursor ?? undefined;
      if (records.length > 5000) throw new AccessApiError(400, 'SELECTION_TOO_LARGE');
    } while (cursor);
    return records;
  }
  async endSession(): Promise<void> {
    await read<boolean>('/identity-api/local/v1/tenant-sessions/revoke', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        tenantId: this.session.tenantId,
        authenticationContextId: this.session.authenticationContextId,
      }),
    });
  }
}

export const displayError = (error: unknown): string =>
  error instanceof AccessApiError
    ? error.message
    : 'The connection was interrupted. Refresh to check the current state before retrying.';
export const recordId = (record: AccessRecord): string => record.id ?? record.actorId ?? '';
