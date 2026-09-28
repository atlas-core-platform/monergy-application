import type { ReportingFailure, TrustedFinancialReport } from './types';

interface ContractResponse<T> {
  outcome: 'Success' | 'Rejected' | 'Failed';
  data: T | null;
  error: ReportingFailure | null;
}

export class ReferenceReportingError extends Error {
  constructor(
    message: string,
    readonly failure: ReportingFailure,
  ) {
    super(message);
    this.name = 'ReferenceReportingError';
  }
}

const security = {
  actor: {
    actorId: 'reference-actor',
    actorType: 'HUMAN',
    authenticatedAt: '1970-01-01T00:00:00Z',
    authenticationContextId: 'reference-authentication',
  },
  workload: { workloadId: 'reporting', workloadIdentityId: 'reference-reporting-workload' },
  access: {
    purpose: 'REFERENCE_REPORTING',
    consentReferenceId: null,
    authorizationContextId: 'reference-reporting-authorization',
    customerId: 'reference-customer',
  },
};

async function invoke<T>(contractId: string, contractName: string, payload: object): Promise<T> {
  const requestId = `report-request-${globalThis.crypto.randomUUID()}`;
  const response = await fetch(`/contracts/${contractId}/v1`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      contractName,
      contractVersion: '1.0.0',
      requestId,
      correlationId: requestId,
      causationId: null,
      security,
      idempotencyKey: contractId === 'cid-051' ? 'reference-basic-report' : null,
      payload,
    }),
  });
  const body = (await response.json().catch(() => null)) as ContractResponse<T> | null;
  if (!response.ok || body?.outcome !== 'Success' || body.data === null) {
    const failure = body?.error ?? {
      code: 'report.reference.unavailable',
      message: 'The trusted reference report is unavailable.',
      retryable: response.status >= 500,
    };
    throw new ReferenceReportingError(failure.message, failure);
  }
  return body.data;
}

export async function generateReferenceReport(): Promise<TrustedFinancialReport> {
  const generated = await invoke<TrustedFinancialReport>('cid-051', 'GenerateReport', {
    customerId: 'reference-customer',
  });
  return invoke<TrustedFinancialReport>('cid-052', 'GetReport', {
    customerId: 'reference-customer',
    reportId: generated.reportId,
  });
}

export function downloadReferenceExport(report: TrustedFinancialReport): void {
  const blob = new Blob([report.export.content], { type: report.export.mediaType });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = report.export.fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}
