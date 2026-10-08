import { tenantContract } from '../access/workspaceSession';
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

const reportingEnvironment = import.meta.env as unknown as {
  readonly VITE_MONERGY_D11_CUSTOMER_ID?: string;
  readonly VITE_MONERGY_REPORTING_MODE?: string;
};

export const reportingExperienceMode =
  reportingEnvironment.VITE_MONERGY_REPORTING_MODE === 'PERSISTED_REPORTING'
    ? 'PERSISTED_REPORTING'
    : 'REFERENCE';

const customerId =
  reportingExperienceMode === 'PERSISTED_REPORTING'
    ? (reportingEnvironment.VITE_MONERGY_D11_CUSTOMER_ID ?? 'd11-customer-a')
    : 'reference-customer';

const security = {
  actor: {
    actorId:
      reportingExperienceMode === 'PERSISTED_REPORTING'
        ? `d11-synthetic-actor-${customerId}`
        : 'reference-actor',
    actorType: reportingExperienceMode === 'PERSISTED_REPORTING' ? 'SYNTHETIC_HUMAN' : 'HUMAN',
    authenticatedAt: '1970-01-01T00:00:00Z',
    authenticationContextId:
      reportingExperienceMode === 'PERSISTED_REPORTING'
        ? `d11-authentication-${customerId}`
        : 'reference-authentication',
  },
  workload: {
    workloadId: reportingExperienceMode === 'PERSISTED_REPORTING' ? 'customer-web' : 'reporting',
    workloadIdentityId:
      reportingExperienceMode === 'PERSISTED_REPORTING'
        ? 'd11-customer-web'
        : 'reference-reporting-workload',
  },
  access: {
    purpose:
      reportingExperienceMode === 'PERSISTED_REPORTING'
        ? 'D11_PERSISTED_REPORTING'
        : 'REFERENCE_REPORTING',
    consentReferenceId:
      reportingExperienceMode === 'PERSISTED_REPORTING' ? `d11-consent-${customerId}` : null,
    authorizationContextId:
      reportingExperienceMode === 'PERSISTED_REPORTING'
        ? `d11-customer-web-${customerId}-authorization`
        : 'reference-reporting-authorization',
    customerId,
  },
};

async function invoke<T>(
  contractId: string,
  contractName: string,
  payload: object,
  idempotencyKey: string | null = null,
): Promise<T> {
  const requestId = `report-request-${globalThis.crypto.randomUUID()}`;
  const connected = tenantContract({
    contractName,
    contractVersion: '1.0.0',
    requestId,
    correlationId: requestId,
    causationId: null,
    security,
    idempotencyKey,
    payload,
  });
  const response = await fetch(`/contracts/${contractId}/v1`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', ...connected.headers },
    body: JSON.stringify(connected.body),
  });
  const body = (await response.json().catch(() => null)) as ContractResponse<T> | null;
  if (!response.ok || body?.outcome !== 'Success' || body.data === null) {
    const failure =
      body?.error && typeof body.error === 'object'
        ? body.error
        : {
            code: 'report.reference.unavailable',
            message:
              response.status === 401
                ? 'Your session has ended. Connect again in People & access.'
                : response.status === 403
                  ? 'Your current role does not allow this report.'
                  : 'The trusted reference report is unavailable.',
            retryable: response.status >= 500,
          };
    throw new ReferenceReportingError(failure.message, failure);
  }
  return body.data;
}

export async function generateReferenceReport(): Promise<TrustedFinancialReport> {
  const generationIdempotencyKey = `report-generation-${globalThis.crypto.randomUUID()}`;
  const generated = await invoke<TrustedFinancialReport>(
    'cid-051',
    'GenerateReport',
    {
      customerId,
    },
    generationIdempotencyKey,
  );
  return getReferenceReport(generated.reportId);
}

export function getReferenceReport(reportId: string): Promise<TrustedFinancialReport> {
  return invoke<TrustedFinancialReport>('cid-052', 'GetReport', {
    customerId,
    reportId,
  });
}

export interface ReportingDispatchStatus {
  state: 'DELIVERED' | 'PENDING' | 'DEGRADED' | 'UNKNOWN';
  pendingEvents: number;
  lastFailureCode: string | null;
}

function isReportedDispatchState(value: unknown): value is 'DELIVERED' | 'PENDING' | 'DEGRADED' {
  return value === 'DELIVERED' || value === 'PENDING' || value === 'DEGRADED';
}

export function parseReportingDispatchStatus(value: unknown): ReportingDispatchStatus {
  const candidate = value as Partial<ReportingDispatchStatus> | null;
  if (
    candidate === null ||
    !isReportedDispatchState(candidate.state) ||
    typeof candidate.pendingEvents !== 'number'
  ) {
    throw new Error('Malformed Audit dispatch status.');
  }
  return {
    state: candidate.state,
    pendingEvents: candidate.pendingEvents,
    lastFailureCode: candidate.lastFailureCode ?? null,
  };
}

export async function getReportingDispatchStatus(
  cancellationSignal?: AbortSignal,
): Promise<ReportingDispatchStatus | null> {
  if (reportingExperienceMode !== 'PERSISTED_REPORTING') return null;
  const controller = new AbortController();
  const timeout = globalThis.setTimeout(() => {
    controller.abort('audit-status-timeout');
  }, 3_000);
  const cancel = () => {
    controller.abort(cancellationSignal?.reason);
  };
  cancellationSignal?.addEventListener('abort', cancel, { once: true });
  try {
    const response = await fetch('/operations/local/reporting/status', {
      signal: controller.signal,
    });
    if (!response.ok) {
      return {
        state: 'DEGRADED',
        pendingEvents: -1,
        lastFailureCode: 'audit.status.unavailable',
      };
    }
    return parseReportingDispatchStatus(await response.json());
  } catch (error) {
    if (cancellationSignal?.aborted) throw error;
    return {
      state: 'UNKNOWN',
      pendingEvents: -1,
      lastFailureCode: 'audit.status.unknown',
    };
  } finally {
    globalThis.clearTimeout(timeout);
    cancellationSignal?.removeEventListener('abort', cancel);
  }
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
