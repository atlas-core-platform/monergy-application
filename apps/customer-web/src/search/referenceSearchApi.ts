import { tenantContract } from '../access/workspaceSession';
import type { SearchFailure, SearchKind, SearchMatchMode, SearchResults } from './types';
interface ContractResponse<T> {
  outcome: 'Success' | 'Rejected' | 'Failed';
  data: T | null;
  error: SearchFailure | null;
}
export class ReferenceSearchError extends Error {
  constructor(
    message: string,
    readonly failure: SearchFailure,
  ) {
    super(message);
    this.name = 'ReferenceSearchError';
  }
}
export async function searchReference(
  kind: SearchKind,
  query: string,
  matchMode: SearchMatchMode,
  signal?: AbortSignal,
): Promise<SearchResults> {
  const contractName = kind === 'documents' ? 'SearchDocuments' : 'SearchFinancialData';
  const contractId = kind === 'documents' ? 'cid-042' : 'cid-043';
  const requestId = `search-request-${globalThis.crypto.randomUUID()}`;
  const connected = tenantContract({
    contractName,
    contractVersion: '1.0.0',
    requestId,
    correlationId: requestId,
    causationId: null,
    security: {
      actor: {
        actorId: 'reference-actor',
        actorType: 'HUMAN',
        authenticatedAt: '1970-01-01T00:00:00Z',
        authenticationContextId: 'reference-authentication',
      },
      workload: { workloadId: 'search-retrieval', workloadIdentityId: 'reference-workload' },
      access: {
        purpose: 'REFERENCE_SEARCH',
        consentReferenceId: null,
        authorizationContextId: 'reference-authorization',
        customerId: 'reference-customer',
      },
    },
    idempotencyKey: null,
    payload: { customerId: 'reference-customer', query, matchMode, limit: 20 },
  });
  const response = await fetch(`/contracts/${contractId}/v1`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', ...connected.headers },
    signal,
    body: JSON.stringify(connected.body),
  });
  const body = (await response.json().catch(() => null)) as ContractResponse<SearchResults> | null;
  if (!response.ok || body?.outcome !== 'Success' || body.data === null) {
    const failure =
      body?.error && typeof body.error === 'object'
        ? body.error
        : {
            code: 'search.reference.unavailable',
            message:
              response.status === 401
                ? 'Your session has ended. Connect again in People & access.'
                : response.status === 403
                  ? 'Your current role does not allow this search.'
                  : 'The authorized reference search is unavailable.',
            retryable: response.status >= 500,
          };
    throw new ReferenceSearchError(failure.message, failure);
  }
  return body.data;
}
