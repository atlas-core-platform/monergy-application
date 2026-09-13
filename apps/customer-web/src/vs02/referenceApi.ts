import type { ReferenceRunFailure, ReferenceRunRequest, ReferenceRunResult } from './types';

export class ReferenceRunError extends Error {
  constructor(
    message: string,
    readonly failure: ReferenceRunFailure,
  ) {
    super(message);
    this.name = 'ReferenceRunError';
  }
}

export async function runReferenceFlow(
  request: ReferenceRunRequest,
  signal?: AbortSignal,
): Promise<ReferenceRunResult> {
  const response = await fetch('/local-ci/vs02/reference-runs', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  });

  if (!response.ok) {
    const failure = (await response.json().catch(() => ({
      code: 'reference.unavailable',
      message: 'The reference flow is unavailable.',
      retryable: response.status >= 500,
    }))) as ReferenceRunFailure;
    throw new ReferenceRunError(failure.message, failure);
  }

  return (await response.json()) as ReferenceRunResult;
}
