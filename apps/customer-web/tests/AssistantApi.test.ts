import { afterEach, expect, it, vi } from 'vitest';
import { AssistantApi, AssistantError, type AssistantSession } from '../src/assistant/assistantApi';

const session: AssistantSession = { audience: 'product', credential: 'synthetic-session', expiresAt: '2099-01-01T00:00:00Z' };
const signal = () => new AbortController().signal;
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('treats a lost submission acknowledgement as unknown, then retries the same operation', async () => {
  const fetcher = vi.fn().mockRejectedValueOnce(new TypeError('lost acknowledgement'))
    .mockResolvedValueOnce(json({ saved: true, gapId: '3f5e73b5-6208-439c-8e89-8e8723561d48' }));
  vi.stubGlobal('fetch', fetcher);
  const api = new AssistantApi(session);
  await expect(api.suggest('How do reviewed topics work?', 'operation-one', signal())).rejects.toMatchObject({ outcome: 'UNKNOWN' });
  await expect(api.suggest('How do reviewed topics work?', 'operation-one', signal())).resolves.toBeUndefined();
  const options = fetcher.mock.calls.map(call => call[1] as RequestInit);
  expect(options.map(option => new Headers(option.headers).get('Idempotency-Key'))).toEqual(['operation-one', 'operation-one']);
  expect(options[0]?.body).toBe(options[1]?.body);
});

it.each([
  json({ outcome: 'UNKNOWN', error: 'ASSISTANT_OUTCOME_UNKNOWN' }, 503),
  json({ saved: false }, 503),
  json({ saved: true }),
  new Response('{', { status: 200 }),
])('does not report definite failure for uncertain or malformed acknowledgement %#', async response => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response));
  await expect(new AssistantApi(session).suggest('How do reviewed topics work?', 'operation-one', signal()))
    .rejects.toMatchObject({ outcome: 'UNKNOWN' });
});

it('distinguishes an explicit pre-persistence rejection', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ outcome: 'REJECTED', saved: false }, 400)));
  await expect(new AssistantApi(session).suggest('Invalid input', 'operation-one', signal()))
    .rejects.toMatchObject({ status: 400, outcome: 'REJECTED' });
});

it('rejects an expired session before dispatch', async () => {
  const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher);
  await expect(new AssistantApi({ ...session, expiresAt: '2000-01-01T00:00:00Z' }).suggest('How?', 'operation-one', signal()))
    .rejects.toMatchObject({ status: 401, outcome: 'REJECTED' });
  expect(fetcher).not.toHaveBeenCalled();
});

it.each(['product', 'platform'] as const)('sends only current %s session authority and minimal question body', async audience => {
  const fetcher = vi.fn().mockResolvedValue(json({ state: 'NOT_DOCUMENTED', answer: [], sources: [], canSuggest: true }));
  vi.stubGlobal('fetch', fetcher);
  await new AssistantApi({ ...session, audience }).ask('How?', signal());
  const [url, options] = fetcher.mock.calls[0] as [string, RequestInit];
  expect(url).toBe(audience === 'product' ? '/assistant-api/local/v1/answers' : '/platform-api/local/v1/assistant/answers');
  expect(JSON.parse(options.body as string)).toEqual({ contractVersion: '1.0.0', question: 'How?' });
  expect(Array.from(new Headers(options.headers).keys()).sort()).toEqual(['content-type', audience === 'product' ? 'x-monergy-session' : 'x-monergy-platform-session']);
  expect(options).toMatchObject({ redirect: 'error', cache: 'no-store' });
});

it('bounds response bytes and rejects invalid UTF-8 without displaying it', async () => {
  const fetcher = vi.fn().mockResolvedValueOnce(new Response('x'.repeat(96 * 1024 + 1)))
    .mockResolvedValueOnce(new Response(new Uint8Array([0xc3, 0x28])));
  vi.stubGlobal('fetch', fetcher);
  await expect(new AssistantApi(session).status(signal())).rejects.toBeInstanceOf(AssistantError);
  await expect(new AssistantApi(session).status(signal())).rejects.toBeInstanceOf(AssistantError);
});

it('recovers resource-less list evidence through the event route, without inventing a gap', async () => {
  const event = { eventId: 'opaque-event', operationId: 'opaque-operation', resourceId: null, action: 'FeedbackRead', state: 'Paused', attempts: 16, recoveries: 0, createdAt: '2026-11-19T00:00:00Z' };
  const fetcher = vi.fn().mockResolvedValueOnce(json({ events: [event], nextCursor: null }))
    .mockResolvedValueOnce(json({ event: { ...event, state: 'Pending', attempts: 0, recoveries: 1 } }));
  vi.stubGlobal('fetch', fetcher);
  const api = new AssistantApi({ ...session, audience: 'platform' });
  expect(await api.auditDeliveries(null, signal())).toEqual({ events: [event], nextCursor: null });
  expect(await api.retryAuditDelivery(event.eventId, 'retry-operation', signal())).toMatchObject({ resourceId: null, state: 'Pending' });
  expect(fetcher.mock.calls[1]?.[0]).toBe('/platform-api/local/v1/assistant/review/audit-deliveries/opaque-event/retry');
});

it.each([
  { events: Array(21).fill({}), nextCursor: null },
  { events: [{ eventId: 'invalid' }], nextCursor: null },
  { events: [], nextCursor: 'x'.repeat(257) },
])('rejects malformed or excessive audit discovery %#', async response => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(response)));
  await expect(new AssistantApi({ ...session, audience: 'platform' }).auditDeliveries(null, signal())).rejects.toBeInstanceOf(AssistantError);
});

it('treats a lost review mutation acknowledgement as unknown', async () => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('lost reply')));
  await expect(new AssistantApi({ ...session, audience: 'platform' }).update('gap', {
    expectedRevision: 1, status: 'Under review', classification: 'Knowledge gap', approvedGeneralTopic: false,
    topicId: null, ownerReference: null, duplicateOf: null, privacyDelete: false, resolution: null,
  }, 'operation', signal())).rejects.toMatchObject({ outcome: 'UNKNOWN' });
});
