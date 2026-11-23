import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import Drawer from '../src/assistant/AskMonergyDrawer';
import type { AssistantSession } from '../src/assistant/assistantApi';

const session: AssistantSession = { audience: 'product', credential: 'synthetic-session', expiresAt: '2099-01-01T00:00:00Z' };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const ready = () => json({ enabled: true, available: true, reviewAllowed: false });
const mount = (onSessionEnded = vi.fn(), onClose = vi.fn(), current = session) => ({
  ...render(<FoundationProvider appearance="midnight"><Drawer session={current} workspace="Customer" onSessionEnded={onSessionEnded} onClose={onClose} /></FoundationProvider>),
  onSessionEnded, onClose,
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });

async function prepareSuggestion() {
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Suggest a help topic' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'General help question' }), { target: { value: 'How do reviewed topics work?' } });
  await user.click(screen.getByRole('checkbox', { name: /I have reviewed this question/ }));
  return user;
}

it('recovers a lost save response with the same operation and frozen payload without claiming failure or logout', async () => {
  let attempts = 0;
  const operations: string[] = []; const bodies: unknown[] = [];
  const fetcher = vi.fn((url: string, options?: RequestInit) => {
    if (url.endsWith('/status')) return Promise.resolve(ready());
    operations.push(new Headers(options?.headers).get('Idempotency-Key') ?? ''); bodies.push(options?.body);
    if (++attempts === 1) return Promise.reject(new TypeError('lost reply after commit'));
    return Promise.resolve(json({ saved: true, gapId: 'a925d035-11fe-40e7-a833-d5fb42f9df5c' }));
  });
  vi.stubGlobal('fetch', fetcher);
  const mounted = mount(); const user = await prepareSuggestion();
  await user.click(screen.getByRole('button', { name: 'Submit suggestion' }));
  expect(await screen.findByText('We couldn’t confirm whether your suggestion was saved. Retry to check.')).toBeVisible();
  expect(screen.queryByText(/Your suggestion was not saved/)).not.toBeInTheDocument();
  expect(screen.getByRole('textbox', { name: 'General help question' })).toBeDisabled();
  await user.click(screen.getByRole('button', { name: 'Retry suggestion' }));
  expect(await screen.findByText('Your suggestion has been saved for review.')).toBeVisible();
  expect(operations).toHaveLength(2); expect(operations[0]).toBe(operations[1]); expect(bodies[0]).toBe(bodies[1]);
  expect(mounted.onSessionEnded).not.toHaveBeenCalled();
});

it('shows a definite rejection honestly and permits editing after rejection', async () => {
  vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(url.endsWith('/status') ? ready() :
    json({ outcome: 'REJECTED', saved: false }, 400))));
  mount(); const user = await prepareSuggestion();
  await user.click(screen.getByRole('button', { name: 'Submit suggestion' }));
  expect(await screen.findByText(/Your suggestion was not saved/)).toBeVisible();
  expect(screen.getByRole('textbox', { name: 'General help question' })).toBeEnabled();
});

it('requires deliberate separate confirmation and does not copy ordinary question text', async () => {
  const fetcher = vi.fn(() => Promise.resolve(ready())); vi.stubGlobal('fetch', fetcher);
  mount(); const user = userEvent.setup();
  fireEvent.change(await screen.findByRole('textbox', { name: 'Your question · Customer' }), { target: { value: 'ordinary-canary' } });
  await user.click(screen.getByRole('button', { name: 'Suggest a help topic' }));
  expect(screen.getByRole('textbox', { name: 'General help question' })).toHaveValue('');
  fireEvent.change(screen.getByRole('textbox', { name: 'General help question' }), { target: { value: 'A general topic?' } });
  expect(screen.getByRole('button', { name: 'Submit suggestion' })).toBeDisabled();
  expect(fetcher).toHaveBeenCalledTimes(1);
});

it('aborts and ignores late answers after the drawer is unmounted', async () => {
  let pendingResolve: (value: Response) => void = () => undefined;
  let pendingSignal: AbortSignal | undefined;
  vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
    if (url.endsWith('/status')) return Promise.resolve(ready());
    pendingSignal = options?.signal ?? undefined;
    return new Promise<Response>(resolve => { pendingResolve = resolve; });
  }));
  const mounted = mount(); const user = userEvent.setup();
  fireEvent.change(await screen.findByRole('textbox', { name: 'Your question · Customer' }), { target: { value: 'How?' } });
  await user.click(screen.getByRole('button', { name: 'Ask' }));
  mounted.unmount(); expect(pendingSignal?.aborted).toBe(true);
  await act(async () => { pendingResolve(json({ state: 'ANSWERED', answer: ['late-canary'], sources: ['Reviewed guidance'], canSuggest: false })); await Promise.resolve(); });
  expect(screen.queryByText('late-canary')).not.toBeInTheDocument();
});

it('keeps questions and answers out of browser storage and uses understandable source labels', async () => {
  const storage = vi.spyOn(Storage.prototype, 'setItem');
  vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(url.endsWith('/status') ? ready() : json({
    state: 'ANSWERED', answer: ['Reviewed product guidance.'], sources: ['Using Monergy'], canSuggest: false,
  }))));
  mount(); const user = userEvent.setup();
  fireEvent.change(await screen.findByRole('textbox', { name: 'Your question · Customer' }), { target: { value: 'How?' } });
  await user.click(screen.getByRole('button', { name: 'Ask' }));
  expect(await screen.findByText('Reviewed product guidance.')).toBeVisible();
  expect(screen.getByText('About this answer')).toBeInTheDocument();
  expect(screen.getByText('Using Monergy')).toBeInTheDocument();
  expect(storage).not.toHaveBeenCalled();
});

it('does not log out a valid session on a feature denial', async () => {
  vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(url.endsWith('/status') ? ready() : json({ outcome: 'REJECTED', saved: false }, 403))));
  const mounted = mount(); const user = await prepareSuggestion();
  await user.click(screen.getByRole('button', { name: 'Submit suggestion' }));
  await waitFor(() => { expect(mounted.onClose).toHaveBeenCalled(); });
  expect(mounted.onSessionEnded).not.toHaveBeenCalled();
});

const topic = { gapId: 'a925d035-11fe-40e7-a833-d5fb42f9df5c', generalQuestion: 'Restricted general question',
  sourceAudience: 'product', classification: 'Knowledge gap', status: 'New', revision: 1,
  createdAt: '2026-11-19T00:00:00Z', expiresAt: '2026-12-19T00:00:00Z', approvedGeneralTopic: false };

it('freezes an uncertain review and retries the identical operation instead of claiming rollback', async () => {
  const requests: RequestInit[] = []; let saved = false;
  vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
    if (url.endsWith('/status')) return Promise.resolve(json({ enabled: true, available: true, reviewAllowed: true }));
    if (options?.method === 'PATCH') {
      requests.push(options);
      if (requests.length === 1) return Promise.reject(new TypeError('lost review reply'));
      saved = true; return Promise.resolve(json({ saved: true }));
    }
    return Promise.resolve(json({ topics: [{ ...topic, revision: saved ? 2 : 1 }], nextCursor: null }));
  }));
  const mounted = mount(vi.fn(), vi.fn(), { ...session, audience: 'platform' }); const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Questions to improve' }));
  await user.click(await screen.findByRole('button', { name: 'Review question' }));
  await user.click(screen.getByRole('button', { name: 'Save review' }));
  expect(await screen.findByText('We couldn’t confirm whether this change was saved. Retry to check.')).toBeVisible();
  expect(screen.getByRole('textbox', { name: 'Responsible owner reference' })).toBeDisabled();
  await user.click(screen.getByRole('button', { name: 'Retry review' }));
  await waitFor(() => { expect(requests).toHaveLength(2); expect(screen.queryByRole('button', { name: 'Retry review' })).not.toBeInTheDocument(); });
  expect(requests[0]?.body).toBe(requests[1]?.body);
  expect(new Headers(requests[0]?.headers).get('Idempotency-Key')).toBe(new Headers(requests[1]?.headers).get('Idempotency-Key'));
  expect(mounted.onSessionEnded).not.toHaveBeenCalled();
});

it('removes restricted content after a current authority denial without disconnecting platform help', async () => {
  let denied = false;
  vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(url.endsWith('/status')
    ? json({ enabled: true, available: true, reviewAllowed: true })
    : denied ? json({ error: 'FORBIDDEN' }, 403) : json({ topics: [topic], nextCursor: null }))));
  const mounted = mount(vi.fn(), vi.fn(), { ...session, audience: 'platform' }); const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Questions to improve' }));
  expect(await screen.findByText(topic.generalQuestion)).toBeVisible();
  fireEvent.change(screen.getByRole('textbox', { name: 'Your question · Customer' }), { target: { value: 'clear-on-authority-change' } });
  denied = true; await user.click(screen.getByRole('button', { name: 'Refresh questions' }));
  await waitFor(() => { expect(screen.queryByText(topic.generalQuestion)).not.toBeInTheDocument(); });
  expect(screen.queryByRole('button', { name: 'Questions to improve' })).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Ask' })).toBeInTheDocument();
  expect(screen.getByRole('textbox', { name: 'Your question · Customer' })).toHaveValue('');
  expect(mounted.onSessionEnded).not.toHaveBeenCalled();
});

it.each(['question', 'suggestion'] as const)('keeps a new %s locked when an aborted pre-denial request settles late', async kind => {
  let denied = false;
  const intervals = vi.spyOn(window, 'setInterval');
  const pending: { resolve: (response: Response) => void; signal?: AbortSignal; body?: BodyInit | null; operation: string | null }[] = [];
  vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
    if (url.endsWith('/status')) return Promise.resolve(json({ enabled: true, available: true, reviewAllowed: true }));
    if (url.includes('/review/topics')) return Promise.resolve(denied
      ? json({ error: 'FORBIDDEN' }, 403) : json({ topics: [topic], nextCursor: null }));
    return new Promise<Response>(resolve => {
      pending.push({ resolve, signal: options?.signal ?? undefined, body: options?.body,
        operation: new Headers(options?.headers).get('Idempotency-Key') });
    });
  }));
  const mounted = mount(vi.fn(), vi.fn(), { ...session, audience: 'platform' });
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Questions to improve' }));
  expect(await screen.findByText(topic.generalQuestion)).toBeVisible();
  const startRequest = async (value: string) => {
    if (kind === 'suggestion') {
      await user.click(screen.getByRole('button', { name: 'Suggest a help topic' }));
      fireEvent.change(screen.getByRole('textbox', { name: 'General help question' }), { target: { value } });
      await user.click(screen.getByRole('checkbox', { name: /I have reviewed this question/ }));
      await user.click(screen.getByRole('button', { name: 'Submit suggestion' }));
    } else {
      fireEvent.change(screen.getByRole('textbox', { name: 'Your question · Customer' }), { target: { value } });
      await user.click(screen.getByRole('button', { name: 'Ask' }));
    }
  };
  const response = (answer: string) => kind === 'suggestion'
    ? json({ saved: true, gapId: topic.gapId })
    : json({ state: 'ANSWERED', answer: [answer], sources: ['Reviewed guidance'], canSuggest: false });
  await startRequest('Old conversation question');
  expect(pending).toHaveLength(1);
  denied = true;
  await user.click(screen.getByRole('button', { name: 'Refresh questions' }));
  await waitFor(() => { expect(screen.queryByText(topic.generalQuestion)).not.toBeInTheDocument(); });
  expect(pending[0]?.signal?.aborted).toBe(true);
  expect(screen.queryByRole('button', { name: 'Questions to improve' })).not.toBeInTheDocument();
  await startRequest('New conversation question');
  expect(pending).toHaveLength(2);
  const input = screen.getByRole('textbox', { name: kind === 'suggestion' ? 'General help question' : 'Your question · Customer' });
  expect(input).toBeDisabled();
  const newBody = pending[1]?.body;
  const newOperation = pending[1]?.operation;
  await act(async () => { pending[0]?.resolve(response('Stale answer')); await Promise.resolve(); });
  expect(input).toBeDisabled();
  expect(input).toHaveValue('New conversation question');
  if (kind === 'suggestion') expect(screen.getByRole('checkbox', { name: /I have reviewed this question/ })).toBeDisabled();
  expect(screen.queryByText('Stale answer')).not.toBeInTheDocument();
  expect(screen.queryByText('Your suggestion has been saved for review.')).not.toBeInTheDocument();
  expect(pending).toHaveLength(2);
  expect(pending[1]?.body).toBe(newBody);
  expect(pending[1]?.operation).toBe(newOperation);
  await act(async () => { pending[1]?.resolve(response('Current answer')); await Promise.resolve(); });
  expect(await screen.findByText(kind === 'suggestion' ? 'Your suggestion has been saved for review.' : 'Current answer')).toBeVisible();
  const pollingCallback = intervals.mock.calls.find(([, delay]) => delay === 30000)?.[0];
  if (typeof pollingCallback !== 'function') throw new Error('Status polling was not registered.');
  await act(async () => { pollingCallback(); await Promise.resolve(); });
  expect(await screen.findByRole('button', { name: 'Questions to improve' })).toBeVisible();
  expect(screen.getByRole('textbox', { name: 'Your question · Customer' })).toHaveValue('');
  expect(mounted.onSessionEnded).not.toHaveBeenCalled();
  expect(mounted.onClose).not.toHaveBeenCalled();
});

it('discovers and retries history with no gap resource through the protected reviewer view', async () => {
  const event = { eventId: 'c925d035-11fe-40e7-a833-d5fb42f9df5c', operationId: 'a925d035-11fe-40e7-a833-d5fb42f9df5c', resourceId: null, action: 'FeedbackRead', state: 'Paused', attempts: 16, recoveries: 0, createdAt: '2026-11-19T00:00:00Z' };
  const fetcher = vi.fn((url: string, options?: RequestInit) => {
    if (url.endsWith('/status')) return Promise.resolve(json({ enabled: true, available: true, reviewAllowed: true }));
    if (url.includes('audit-deliveries')) return Promise.resolve(json(options?.method === 'POST'
      ? { event: { ...event, state: 'Pending', attempts: 0, recoveries: 1 } } : { events: [event], nextCursor: null }));
    return Promise.resolve(json({ topics: [], nextCursor: null }));
  });
  vi.stubGlobal('fetch', fetcher); mount(vi.fn(), vi.fn(), { ...session, audience: 'platform' }); const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Questions to improve' }));
  await user.click(await screen.findByRole('button', { name: 'Check all review history' }));
  expect(await screen.findByText(/Review list access · Paused/)).toBeVisible();
  await user.click(screen.getByRole('button', { name: 'Retry history delivery' }));
  expect(await screen.findByText(/Review list access · Pending/)).toBeVisible();
  expect(fetcher.mock.calls.some(([url]) => url.includes(event.eventId + '/retry'))).toBe(true);
});
