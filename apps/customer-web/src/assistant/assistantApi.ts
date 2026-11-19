export type AssistantAudience = 'product' | 'platform';
export interface AssistantSession {
  audience: AssistantAudience;
  credential: string;
  expiresAt: string;
}
export interface AssistantStatus { enabled: boolean; available: boolean; reviewAllowed: boolean }
export interface AssistantAnswer { state: string; answer: string[]; sources: string[]; canSuggest: boolean }
export interface HelpTopic {
  gapId: string; generalQuestion: string; sourceAudience: AssistantAudience;
  classification: string; status: string; revision: number; createdAt: string; expiresAt: string;
  topicId?: string | null; ownerReference?: string | null; duplicateOf?: string | null;
  approvedGeneralTopic: boolean;
  topicSubmissionCount?: number | null; matchedOutcomeCount?: number | null;
  resolution?: { approvedChangeReference: string; publishedVersion: string; verificationReference: string } | null;
}
export interface TopicPage { topics: HelpTopic[]; nextCursor: string | null }
export interface DeliveryStatus {
  gapId: string; deliveries: { eventId: string; attempts: number; recoveries: number; state: 'Accepted' | 'Pending' | 'Paused' }[];
  retryLimit: number; recoveryLimit: number;
}
export interface AuditDeliveryEvent {
  eventId: string; operationId: string; resourceId: string | null; action: string;
  state: 'Accepted' | 'Pending' | 'Paused'; attempts: number; recoveries: number; createdAt: string;
}
export interface AuditDeliveryPage { events: AuditDeliveryEvent[]; nextCursor: string | null }
export interface TopicChange {
  expectedRevision: number; status: string; classification: string;
  approvedGeneralTopic: boolean; topicId: string | null; ownerReference: string | null;
  duplicateOf: string | null; privacyDelete: boolean;
  resolution: HelpTopic['resolution'];
}

export class AssistantError extends Error {
  constructor(public readonly status: number, public readonly retryAfter = 0,
    public readonly outcome: 'NONE' | 'REJECTED' | 'UNKNOWN' = 'NONE') {
    super(outcome === 'UNKNOWN' ? 'We couldn’t confirm whether this change was saved. Retry to check.' :
      status === 401 ? 'Your session has ended. Sign in again to continue.' :
      status === 403 ? 'You do not have access to this action.' :
      status === 429 ? 'Please wait before trying again.' :
      status === 409 ? 'This question has changed. Refresh it before saving.' :
      status === 400 ? 'Please enter a general help question without personal or account details.' :
      'Help is temporarily unavailable. Please try again.');
  }
}
const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);
const strings = (value: unknown, count: number, length: number): value is string[] =>
  Array.isArray(value) && value.length <= count && value.every(item => typeof item === 'string' && item.length <= length);
const text = (value: unknown, maximum: number): value is string => typeof value === 'string' && value.length <= maximum;
const optionalText = (value: unknown, maximum: number) => value == null || text(value, maximum);
const optionalCount = (value: unknown) => value == null || (typeof value === 'number' && Number.isSafeInteger(value) && value >= 0);
const auditEvent = (value: unknown): value is AuditDeliveryEvent => object(value) &&
  text(value.eventId, 100) && text(value.operationId, 100) && (value.resourceId === null || text(value.resourceId, 100)) &&
  text(value.action, 64) && text(value.createdAt, 64) &&
  (value.state === 'Accepted' || value.state === 'Pending' || value.state === 'Paused') &&
  typeof value.attempts === 'number' && Number.isSafeInteger(value.attempts) && value.attempts >= 0 && value.attempts <= 16 &&
  typeof value.recoveries === 'number' && Number.isSafeInteger(value.recoveries) && value.recoveries >= 0 && value.recoveries <= 3;
async function readJson(response: Response): Promise<unknown> {
  const reader = response.body?.getReader();
  if (!reader) throw new AssistantError(503);
  const chunks: Uint8Array[] = []; let size = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > 96 * 1024) { await reader.cancel(); throw new AssistantError(503); }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) as unknown; }
  catch { throw new AssistantError(503); }
}
async function mutation<T>(action: () => Promise<T>): Promise<T> {
  try { return await action(); }
  catch (value) {
    if (value instanceof AssistantError && value.outcome !== 'NONE') throw value;
    throw new AssistantError(value instanceof AssistantError ? value.status : 503,
      value instanceof AssistantError ? value.retryAfter : 0, 'UNKNOWN');
  }
}
const topic = (value: unknown): value is HelpTopic => object(value) && text(value.gapId, 100) &&
  text(value.generalQuestion, 500) && (value.sourceAudience === 'product' || value.sourceAudience === 'platform') &&
  text(value.classification, 64) && text(value.status, 64) && Number.isSafeInteger(value.revision) &&
  Number(value.revision) > 0 && text(value.createdAt, 64) && text(value.expiresAt, 64) &&
  typeof value.approvedGeneralTopic === 'boolean' && optionalText(value.topicId, 128) &&
  optionalText(value.ownerReference, 128) && optionalText(value.duplicateOf, 100) &&
  optionalCount(value.topicSubmissionCount) && optionalCount(value.matchedOutcomeCount) &&
  (value.resolution == null || (object(value.resolution) &&
    text(value.resolution.approvedChangeReference, 256) && text(value.resolution.publishedVersion, 256) &&
    text(value.resolution.verificationReference, 256)));

export class AssistantApi {
  private readonly base: string;
  constructor(private readonly session: AssistantSession) {
    this.base = session.audience === 'product' ? '/assistant-api/local/v1' : '/platform-api/local/v1/assistant';
  }
  private async request(path: string, signal: AbortSignal, method = 'GET', body?: unknown, operationId?: string): Promise<unknown> {
    if (!Number.isFinite(Date.parse(this.session.expiresAt)) || Date.parse(this.session.expiresAt) <= Date.now())
      throw new AssistantError(401, 0, operationId ? 'REJECTED' : 'NONE');
    const headers = new Headers({ 'Content-Type': 'application/json' });
    headers.set(this.session.audience === 'product' ? 'X-Monergy-Session' : 'X-Monergy-Platform-Session', this.session.credential);
    if (operationId) headers.set('Idempotency-Key', operationId);
    const response = await fetch(this.base + path, {
      method, headers, signal, credentials: 'same-origin', redirect: 'error', cache: 'no-store',
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
      let rejected = false;
      if (operationId) {
        try {
          const result = await readJson(response);
          rejected = object(result) && result.outcome === 'REJECTED' && result.saved === false;
        } catch { /* A missing or malformed acknowledgement cannot establish rollback. */ }
      } else await response.body?.cancel();
      const retry = Number(response.headers.get('Retry-After'));
      throw new AssistantError(response.status, Number.isFinite(retry) ? Math.max(0, Math.min(3600, retry)) : 0,
        operationId ? (rejected ? 'REJECTED' : 'UNKNOWN') : 'NONE');
    }
    return readJson(response);
  }
  async status(signal: AbortSignal): Promise<AssistantStatus> {
    const result = await this.request('/status', signal);
    if (!object(result) || typeof result.enabled !== 'boolean' || typeof result.available !== 'boolean' ||
        typeof result.reviewAllowed !== 'boolean') throw new AssistantError(503);
    return { enabled: result.enabled, available: result.available, reviewAllowed: result.reviewAllowed };
  }
  async ask(question: string, signal: AbortSignal): Promise<AssistantAnswer> {
    const result = await this.request('/answers', signal, 'POST', { contractVersion: '1.0.0', question });
    if (!object(result) || !text(result.state, 64) || !strings(result.answer, 20, 12000) ||
        !strings(result.sources, 20, 120) || typeof result.canSuggest !== 'boolean') throw new AssistantError(503);
    return { state: result.state, answer: result.answer, sources: result.sources, canSuggest: result.canSuggest };
  }
  async suggest(generalQuestion: string, operationId: string, signal: AbortSignal): Promise<void> {
    return mutation(async () => {
      const result = await this.request('/help-topics', signal, 'POST', { contractVersion: '1.0.0', generalQuestion }, operationId);
      if (!object(result) || result.saved !== true || !text(result.gapId, 100)) throw new AssistantError(503);
    });
  }
  async topics(cursor: string | null, signal: AbortSignal): Promise<TopicPage> {
    const result = await this.request('/review/topics?limit=20' + (cursor ? '&cursor=' + encodeURIComponent(cursor) : ''), signal);
    if (!object(result) || !Array.isArray(result.topics) || result.topics.length > 20 ||
        !result.topics.every(topic) || !optionalText(result.nextCursor, 256)) throw new AssistantError(503);
    return { topics: result.topics, nextCursor: typeof result.nextCursor === 'string' ? result.nextCursor : null };
  }
  async update(gapId: string, change: TopicChange, operationId: string, signal: AbortSignal): Promise<void> {
    return mutation(async () => {
      const result = await this.request('/review/topics/' + encodeURIComponent(gapId), signal, 'PATCH',
        { contractVersion: '1.0.0', ...change }, operationId);
      if (!object(result) || result.saved !== true) throw new AssistantError(503);
    });
  }
  async delivery(gapId: string, signal: AbortSignal, operationId?: string): Promise<DeliveryStatus> {
    const result = await this.request('/review/topics/' + encodeURIComponent(gapId) + '/delivery' + (operationId ? '/retry' : ''),
      signal, operationId ? 'POST' : 'GET', operationId ? { contractVersion: '1.0.0' } : undefined, operationId);
    if (!object(result) || result.gapId !== gapId || result.retryLimit !== 16 || result.recoveryLimit !== 3 ||
        !Array.isArray(result.deliveries) || result.deliveries.length > 50) throw new AssistantError(503);
    const deliveries: DeliveryStatus['deliveries'] = [];
    for (const item of result.deliveries) {
      if (!object(item) || !text(item.eventId, 100) || typeof item.attempts !== 'number' || !Number.isSafeInteger(item.attempts) || item.attempts < 0 ||
          typeof item.recoveries !== 'number' || !Number.isSafeInteger(item.recoveries) || item.recoveries < 0 ||
          (item.state !== 'Accepted' && item.state !== 'Pending' && item.state !== 'Paused')) throw new AssistantError(503);
      deliveries.push({ eventId: item.eventId, attempts: item.attempts, recoveries: item.recoveries, state: item.state });
    }
    return { gapId, deliveries, retryLimit: 16, recoveryLimit: 3 };
  }
  async auditDeliveries(cursor: string | null, signal: AbortSignal): Promise<AuditDeliveryPage> {
    const result = await this.request('/review/audit-deliveries?limit=20' + (cursor ? '&cursor=' + encodeURIComponent(cursor) : ''), signal);
    if (!object(result) || !Array.isArray(result.events) || result.events.length > 20 ||
        !result.events.every(auditEvent) || !optionalText(result.nextCursor, 256)) throw new AssistantError(503);
    return { events: result.events, nextCursor: typeof result.nextCursor === 'string' ? result.nextCursor : null };
  }
  async retryAuditDelivery(eventId: string, operationId: string, signal: AbortSignal): Promise<AuditDeliveryEvent> {
    return mutation(async () => {
      const result = await this.request('/review/audit-deliveries/' + encodeURIComponent(eventId) + '/retry', signal,
        'POST', { contractVersion: '1.0.0' }, operationId);
      if (!object(result) || !auditEvent(result.event) || result.event.eventId !== eventId) throw new AssistantError(503);
      return result.event;
    });
  }
}
