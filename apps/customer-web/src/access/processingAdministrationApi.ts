import { AccessApiError } from './accessApi';
export const processingStates = ['QUEUED', 'PROCESSING', 'COMPLETED', 'FAILED', 'RETRYING', 'CANCELLED'] as const;
export interface AdminJob { tenantId: string; jobId: string; customerId: string; task: string; state: string; cancellationPending: boolean; attempt: number; retryCount: number; replayCount: number; requestedAt: string; updatedAt: string; nextAttemptAt: string | null; terminalAt: string | null; failureCode: string | null; needsOwnerReview: boolean; canRetry: boolean; canCancel: boolean; revision: string; documentVersionId?: string | null }
export interface QueuePage { tenantId: string; items: AdminJob[]; nextCursor: string | null; matching: number; counts: { queued: number; processing: number; completed: number; failed: number; retrying: number; cancelled: number } }
export interface JobAttempt { attempt: number; startedAt: string; endedAt: string | null; outcome: string | null; failureCode: string | null; retryable: boolean }
export interface AttemptPage { tenantId: string; jobId: string; items: JobAttempt[]; nextBeforeAttempt: number | null }
export interface JobChange { operationId: string; expectedRevision: string; action: 'retry' | 'cancel'; reason: string }
export interface JobReceipt { tenantId: string; jobId: string; operationId: string; action: 'retry' | 'cancel'; reason: string; recordedBy: string; recordedAt: string; before: AdminJob; after: AdminJob }
export interface JobHistory { tenantId: string; jobId: string; items: JobReceipt[]; nextBeforeId: string | null }
export interface CustomerLabel { customerId: string; displayName: string; secondaryLabel: string | null }
export interface CustomerLabels { tenantId: string; items: CustomerLabel[]; unavailable: string[] }
export const processingPending = new Map<string, { job: AdminJob; customerName: string; change: JobChange }>();
export const identifier = (value: unknown): value is string => typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(value);
const code = (value: unknown): value is string => typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._:-]{0,159}$/.test(value);
const time = (value: unknown): value is string => typeof value === 'string' && Number.isFinite(Date.parse(value)) && Date.parse(value) >= 0;
const count = (value: number) => Number.isSafeInteger(value) && value >= 0;
const hash = (value: string) => /^[0-9a-f]{64}$/.test(value);
export const stateLabel = (state: string) => ({ QUEUED: 'Queued', PROCESSING: 'Processing', COMPLETED: 'Completed', FAILED: 'Failed', RETRYING: 'Retrying', CANCELLED: 'Cancelled' })[state] ?? 'Unavailable';
export function validJob(row: AdminJob, tenant: string, jobId?: string) {
 return (row.documentVersionId === undefined || row.documentVersionId === null || /^[A-Za-z0-9][A-Za-z0-9._:-]{0,159}$/.test(row.documentVersionId)) && row.tenantId === tenant && identifier(row.jobId) && (!jobId || row.jobId === jobId) && identifier(row.customerId) && ['Document processing', 'Background task'].includes(row.task) && processingStates.some(s => s === row.state) && typeof row.cancellationPending === 'boolean' && typeof row.needsOwnerReview === 'boolean' && typeof row.canRetry === 'boolean' && typeof row.canCancel === 'boolean' && [row.attempt, row.retryCount, row.replayCount].every(count) && time(row.requestedAt) && time(row.updatedAt) && (row.nextAttemptAt === null || time(row.nextAttemptAt)) && (row.terminalAt === null || time(row.terminalAt)) && (row.failureCode === null || code(row.failureCode)) && hash(row.revision) && (!row.cancellationPending || row.state === 'PROCESSING' && !row.canRetry && !row.canCancel) && (!row.needsOwnerReview || !row.canRetry && !row.canCancel) && (!row.canRetry || row.task === 'Document processing' && ['FAILED', 'RETRYING'].includes(row.state)) && (!row.canCancel || !['COMPLETED', 'CANCELLED'].includes(row.state));
}
export function validReceipt(row: JobReceipt, tenant: string, jobId: string) {
 return row.tenantId === tenant && row.jobId === jobId && /^[0-9a-f]{32}$/.test(row.operationId) && ['retry', 'cancel'].includes(row.action) && row.reason.trim() === row.reason && row.reason.length > 0 && row.reason.length <= 500 && !/[\p{Cc}]/u.test(row.reason) && identifier(row.recordedBy) && time(row.recordedAt) && Date.parse(row.recordedAt) <= Date.now() + 5000 && validJob(row.before, tenant, jobId) && validJob(row.after, tenant, jobId) && row.before.customerId === row.after.customerId && row.before.documentVersionId === row.after.documentVersionId && row.before.task === row.after.task && row.before.requestedAt === row.after.requestedAt && row.before.attempt === row.after.attempt && row.before.retryCount === row.after.retryCount && row.before.revision !== row.after.revision && !row.after.needsOwnerReview && !row.after.canRetry && (row.action === 'retry' ? row.before.canRetry && row.after.state === 'RETRYING' && row.after.replayCount === row.before.replayCount + 1 && !row.after.cancellationPending && row.after.terminalAt === null : row.before.canCancel && !row.after.canCancel && row.after.replayCount === row.before.replayCount && row.after.state === (row.before.state === 'PROCESSING' ? 'PROCESSING' : 'CANCELLED') && row.after.cancellationPending === (row.after.state === 'PROCESSING') && row.after.nextAttemptAt === null);
}
export function validQueue(page: QueuePage, tenant: string) {
 const counts = [page.counts.queued, page.counts.processing, page.counts.completed, page.counts.failed, page.counts.retrying, page.counts.cancelled];
 return page.tenantId === tenant && Array.isArray(page.items) && page.items.length <= 50 && page.items.every(row => validJob(row, tenant)) && new Set(page.items.map(row => row.jobId)).size === page.items.length && (page.nextCursor === null || page.items.length === 50 && page.nextCursor === page.items.at(-1)?.jobId) && count(page.matching) && counts.length === 6 && counts.every(count) && page.matching <= counts.reduce((sum, n) => sum + n, 0) && page.matching >= page.items.length;
}
export function validAttempts(page: AttemptPage, tenant: string, jobId: string, before: number | null) {
 if (page.tenantId !== tenant || page.jobId !== jobId || !Array.isArray(page.items) || page.items.length > 50 || page.nextBeforeAttempt !== null && (page.items.length !== 50 || page.nextBeforeAttempt !== page.items.at(-1)?.attempt)) return false;
 let last = before ?? Number.MAX_SAFE_INTEGER;
 return page.items.every(row => { if (!count(row.attempt) || row.attempt < 1 || row.attempt >= last || !time(row.startedAt) || row.endedAt !== null && (!time(row.endedAt) || Date.parse(row.endedAt) < Date.parse(row.startedAt)) || row.outcome !== null && !code(row.outcome) || row.failureCode !== null && !code(row.failureCode) || typeof row.retryable !== 'boolean') return false; last = row.attempt; return true; });
}
export function validHistory(page: JobHistory, tenant: string, jobId: string) {
 if (page.tenantId !== tenant || page.jobId !== jobId || !Array.isArray(page.items) || page.items.length > 50 || new Set(page.items.map(row => row.operationId)).size !== page.items.length || page.nextBeforeId !== null && (page.items.length !== 50 || page.nextBeforeId !== page.items.at(-1)?.operationId)) return false;
 let last: JobReceipt | null = null;
 return page.items.every(row => { if (!validReceipt(row, tenant, jobId) || last !== null && (Date.parse(row.recordedAt) > Date.parse(last.recordedAt) || row.recordedAt === last.recordedAt && row.operationId >= last.operationId)) return false; last = row; return true; });
}
export function validLabels(value: CustomerLabels, tenant: string, requested: string[]) {
 return value.tenantId === tenant && Array.isArray(value.items) && Array.isArray(value.unavailable) && value.items.every(row => requested.includes(row.customerId) && identifier(row.customerId) && row.displayName.trim().length > 0 && row.displayName.length <= 120 && !/[\p{Cc}]/u.test(row.displayName) && (row.secondaryLabel === null || row.secondaryLabel.length <= 120 && !/[\p{Cc}]/u.test(row.secondaryLabel))) && value.unavailable.every(id => requested.includes(id)) && new Set([...value.items.map(row => row.customerId), ...value.unavailable]).size === requested.length && value.items.length + value.unavailable.length === requested.length;
}
export function invalidProcessingResponse(): never { throw new AccessApiError(503, 'PROCESSING_RESULT_INVALID'); }
