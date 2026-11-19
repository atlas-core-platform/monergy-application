import { Alert, Button, Checkbox, Drawer, Input, Select, Space, Spin, Typography } from 'antd';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AssistantApi, AssistantError } from './assistantApi';
import type { AssistantAnswer, AssistantSession, AssistantStatus, DeliveryStatus, HelpTopic, TopicChange, TopicPage } from './assistantApi';

const explanations: Record<string, string> = {
  UNKNOWN: 'I don’t have information about that yet.',
  INSUFFICIENT_GROUNDING: 'I don’t have enough information to answer that.',
  UNRESOLVED: 'This hasn’t been decided yet.',
  UNSUPPORTED: 'I can’t help with that question here.',
  NOT_IMPLEMENTED: 'This feature isn’t available yet.',
  CONFLICT: 'The available guidance doesn’t agree, so I can’t give a reliable answer.',
  DEPENDENCY_UNAVAILABLE: 'Help is temporarily unavailable. Please try again.',
};
const platformExamples = [
  'What is the difference between Platform Administrator and Organization Administrator?',
  'How does tenant onboarding work?', 'What does ReadyForAdmin mean?',
  'Why does Monergy use tenant-specific URLs?', 'How does workspace selection work?',
];
const tenantExamples = ['What does a DSA / Case Manager do?', 'What can an Organization Administrator do?',
  'How does Consent work?', 'Is Google Drive supported?', 'What is Financial Q&A?'];
const classifications = ['Knowledge gap', 'Retrieval gap', 'Clarification gap', 'Decision follow-up',
  'Product request', 'Conflict resolution', 'Audience restriction', 'Outside product knowledge'];
const statuses = ['New', 'Under review', 'Awaiting decision', 'Ready for verification', 'Resolved', 'Duplicate', 'Out of scope'];

export default function AskMonergyDrawer({ session, workspace, onSessionEnded, onClose }: {
  session: AssistantSession; workspace?: string; onSessionEnded: () => void; onClose: () => void;
}) {
  const api = useMemo(() => new AssistantApi(session), [session]);
  const pending = useRef(new Map<AbortController, 'ordinary' | 'review'>());
  const alive = useRef(true);
  const generation = useRef(0);
  const reviewGeneration = useRef(0);
  const [status, setStatus] = useState<AssistantStatus | null>(null);
  const [question, setQuestion] = useState('');
  const [answer, setAnswer] = useState<AssistantAnswer | null>(null);
  const [error, setError] = useState('');
  const [asking, setAsking] = useState(false);
  const [suggesting, setSuggesting] = useState(false);
  const [suggestion, setSuggestion] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [sending, setSending] = useState(false);
  const [saved, setSaved] = useState(false);
  const [suggestionError, setSuggestionError] = useState('');
  const suggestionOperation = useRef<string | null>(null);
  const [reviewing, setReviewing] = useState(false);
  const [page, setPage] = useState<TopicPage | null>(null);
  const [loadingTopics, setLoadingTopics] = useState(false);
  const [selected, setSelected] = useState<HelpTopic | null>(null);
  const [retryUntil, setRetryUntil] = useState(0);

  const clearReview = useCallback(() => {
    reviewGeneration.current++;
    for (const [controller, purpose] of pending.current) if (purpose === 'review') controller.abort();
    setSelected(null); setPage(null); setReviewing(false); setLoadingTopics(false);
  }, []);
  const run = useCallback(async <T,>(operation: (signal: AbortSignal) => Promise<T>, purpose: 'ordinary' | 'review' = 'ordinary'): Promise<T | undefined> => {
    const current = generation.current;
    const controller = new AbortController(); pending.current.set(controller, purpose);
    let timedOut = false;
    const timeout = window.setTimeout(() => { timedOut = true; controller.abort(); }, 12000);
    try {
      const result = await operation(controller.signal);
      return alive.current && current === generation.current && !controller.signal.aborted ? result : undefined;
    } catch (value) {
      if (!alive.current || current !== generation.current || (controller.signal.aborted && !timedOut)) return undefined;
      throw value;
    } finally { window.clearTimeout(timeout); pending.current.delete(controller); }
  }, []);
  const failure = useCallback((value: unknown) => {
    if (!alive.current) return;
    if (value instanceof AssistantError && value.status === 401) { onSessionEnded(); onClose(); return; }
    if (value instanceof AssistantError && value.status === 403) {
      clearReview();
      setStatus(current => current ? { ...current, reviewAllowed: false } : current);
    }
    if (value instanceof AssistantError && value.status === 429)
      setRetryUntil(Date.now() + Math.max(1, value.retryAfter) * 1000);
    setError(value instanceof AssistantError ? value.message : 'Help is temporarily unavailable. Please try again.');
  }, [clearReview, onClose, onSessionEnded]);

  useEffect(() => {
    alive.current = true;
    const current = ++generation.current;
    const controllers = pending.current;
    const refresh = () => {
      void run(signal => api.status(signal)).then(result => {
        if (!result || !alive.current || current !== generation.current) return;
        setStatus(result);
        if (!result.reviewAllowed) clearReview();
      }).catch((value: unknown) => {
        if (!alive.current || current !== generation.current) return;
        clearReview();
        if (value instanceof AssistantError && value.status === 403) { onClose(); return; }
        failure(value);
      });
    };
    refresh();
    const interval = window.setInterval(refresh, 30000);
    const expiry = window.setTimeout(() => { onSessionEnded(); onClose(); }, Math.max(0, Math.min(2147483647, Date.parse(session.expiresAt) - Date.now())));
    return () => {
      alive.current = false; generation.current++; reviewGeneration.current++;
      window.clearInterval(interval); window.clearTimeout(expiry);
      for (const controller of controllers.keys()) controller.abort();
      controllers.clear();
    };
  }, [api, clearReview, failure, onClose, onSessionEnded, run, session.expiresAt]);

  const ask = async () => {
    if (!question.trim() || asking || Date.now() < retryUntil) return;
    setAsking(true); setAnswer(null); setError('');
    const current = generation.current;
    try {
      const result = await run(signal => api.ask(question.trim(), signal));
      if (result && current === generation.current) setAnswer(result);
    } catch (value) {
      if (!alive.current || current !== generation.current) return;
      if (value instanceof AssistantError && value.status === 403) onClose();
      else failure(value);
    }
    finally { if (alive.current && current === generation.current) setAsking(false); }
  };
  const submit = async () => {
    if (!confirmed || !suggestion.trim() || sending || Date.now() < retryUntil) return;
    setSending(true); setSaved(false); setSuggestionError('');
    const current = generation.current;
    suggestionOperation.current ??= crypto.randomUUID();
    try {
      const result = await run(async signal => {
        await api.suggest(suggestion.trim(), suggestionOperation.current ?? '', signal); return true;
      });
      if (result && current === generation.current) { setSaved(true); setSuggestion(''); setConfirmed(false); suggestionOperation.current = null; }
    } catch (value) {
      if (alive.current && current === generation.current) {
        setSuggestionError('Your suggestion was not saved. ' + (value instanceof AssistantError ? value.message : 'Please try again.'));
        if (value instanceof AssistantError && value.status === 403) onClose();
        else if (value instanceof AssistantError && (value.status === 401 || value.status === 429)) failure(value);
      }
    } finally { if (alive.current && current === generation.current) setSending(false); }
  };
  const loadTopics = async (cursor: string | null = null) => {
    const current = generation.current;
    const review = ++reviewGeneration.current;
    for (const [controller, purpose] of pending.current) if (purpose === 'review') controller.abort();
    setLoadingTopics(true); setError(''); setSelected(null); setPage(null);
    try {
      const result = await run(signal => api.topics(cursor, signal), 'review');
      if (result && current === generation.current && review === reviewGeneration.current) { setPage(result); setReviewing(true); }
    } catch (value) { if (alive.current && current === generation.current && review === reviewGeneration.current) failure(value); }
    finally { if (alive.current && current === generation.current && review === reviewGeneration.current) setLoadingTopics(false); }
  };
  const examples = session.audience === 'platform' ? platformExamples : tenantExamples;
  return <Drawer title="Ask Monergy" open onClose={onClose} size="large" destroyOnHidden>
    <p>{session.audience === 'platform'
      ? 'Ask about setting up and managing Monergy, including organizations, access and integrations.'
      : 'Ask about Monergy features and how to use them. This help does not look up customer records.'}</p>
    {error && <Alert type="warning" showIcon title={error} />}
    {!status && !error && <Spin aria-label="Checking help availability" />}
    {status?.enabled === false && <Alert type="info" title="Help is not enabled here." />}
    {status?.enabled && <>
      {!status.available && <Alert type="info" title="Help is temporarily unavailable. You can still suggest a help topic." />}
      <Space wrap style={{ marginBlock: 16 }}>
        {examples.map(example => <Button key={example} size="small" disabled={asking} onClick={() => { setQuestion(example); setAnswer(null); }}>{example}</Button>)}
      </Space>
      <form onSubmit={event => { event.preventDefault(); void ask(); }}>
        <label htmlFor="assistant-question">Your question{workspace ? ` · ${workspace}` : ''}</label>
        <Input.TextArea id="assistant-question" value={question} onChange={event => { setQuestion(event.target.value); }}
          maxLength={2000} rows={4} autoComplete="off" spellCheck={false} disabled={asking} />
        <Button type="primary" htmlType="submit" loading={asking} disabled={!question.trim() || !status.available} style={{ marginBlock: 12 }}>Ask</Button>
      </form>
      <section aria-live="polite" aria-busy={asking}>
        {answer?.state === 'ANSWERED' ? <>
          {answer.answer.map((paragraph, index) => <p key={index} style={{ whiteSpace: 'pre-wrap' }}>{paragraph}</p>)}
          <details><summary>About this answer</summary><p>Based on Monergy’s reviewed guidance.</p>
            {answer.sources.map(source => <p key={source}>{source}</p>)}
          </details>
        </> : answer && <p>{explanations[answer.state] ?? 'I don’t have information about that yet.'}</p>}
      </section>
      <Space wrap style={{ marginBlock: 16 }}>
        <Button onClick={() => { setSuggesting(value => !value); setSaved(false); }}>Suggest a help topic</Button>
        {session.audience === 'platform' && status.reviewAllowed && <Button loading={loadingTopics} onClick={() => { void loadTopics(); }}>Questions to improve</Button>}
      </Space>
      {suggesting && <section aria-label="Suggest a help topic">
        <p>Describe what you need help with. Please leave out names, account numbers and customer details.</p>
        <p>Only the question you explicitly submit here is saved for help improvement. Your ordinary questions and answers are temporary.</p>
        {saved && <Alert type="success" title="Your suggestion has been saved for review." />}
        {suggestionError && <Alert type="warning" title={suggestionError} />}
        <label htmlFor="assistant-suggestion">General help question</label>
        <Input.TextArea id="assistant-suggestion" value={suggestion} maxLength={500} rows={3} autoComplete="off" spellCheck={false} disabled={sending}
          onChange={event => { setSuggestion(event.target.value); setConfirmed(false); setSaved(false); suggestionOperation.current = null; }} />
        <Checkbox checked={confirmed} disabled={sending} onChange={event => { setConfirmed(event.target.checked); }}>I have reviewed this question and want to submit it without personal or customer details.</Checkbox>
        <p><Button onClick={() => { void submit(); }} disabled={!confirmed || !suggestion.trim()} loading={sending}>Submit suggestion</Button></p>
      </section>}
      {reviewing && status.reviewAllowed && <section aria-label="Questions to improve">
        <h2>Questions to improve</h2>
        <p>New submissions are restricted intake. Approve only general product topics after privacy review. This view does not publish guidance.</p>
        <Button loading={loadingTopics} onClick={() => { void loadTopics(); }}>Refresh questions</Button>
        {page?.topics.length === 0 && <p>No questions are waiting here.</p>}
        {page?.topics.map(item => <article key={item.gapId} style={{ marginBlock: 20 }}>
          <Typography.Text type="secondary">{item.sourceAudience === 'platform' ? 'Platform help' : 'Product help'} · {item.approvedGeneralTopic ? 'Reviewed general topic' : 'Restricted intake'} · {item.status}</Typography.Text>
          <p style={{ whiteSpace: 'pre-wrap' }}>{item.generalQuestion}</p>
          {item.topicSubmissionCount != null && <p>Explicit suggestions for this reviewed topic: {item.topicSubmissionCount}</p>}
          {item.matchedOutcomeCount != null && <p>Automatically observed outcomes for this topic: {item.matchedOutcomeCount}. These are events, not individual people or repeated identical questions.</p>}
          <Button onClick={() => { setSelected(item); }}>Review question</Button>
        </article>)}
        {page?.nextCursor && <Button onClick={() => { void loadTopics(page.nextCursor); }}>Next questions</Button>}
        {selected && <TopicEditor key={selected.gapId + ':' + selected.revision} topic={selected}
          onDelivery={async operationId => {
            const current = generation.current; const review = reviewGeneration.current;
            try {
              const result = await run(signal => api.delivery(selected.gapId, signal, operationId), 'review');
              return current === generation.current && review === reviewGeneration.current ? result : undefined;
            } catch (value) {
              if (alive.current && current === generation.current && review === reviewGeneration.current) failure(value);
              return undefined;
            }
          }}
          onSave={async (change, operationId) => {
            const current = generation.current;
            const review = reviewGeneration.current;
            try {
              const result = await run(async signal => { await api.update(selected.gapId, change, operationId, signal); return true; }, 'review');
              if (!result || current !== generation.current || review !== reviewGeneration.current) return false;
              await loadTopics(); return true;
            } catch (value) {
              if (alive.current && current === generation.current && review === reviewGeneration.current) failure(value);
              return false;
            }
          }} />}
      </section>}
    </>}
  </Drawer>;
}

function TopicEditor({ topic, onSave, onDelivery }: {
  topic: HelpTopic; onSave: (change: TopicChange, operationId: string) => Promise<boolean>;
  onDelivery: (operationId?: string) => Promise<DeliveryStatus | undefined>;
}) {
  const [change, setChange] = useState<TopicChange>({
    expectedRevision: topic.revision, status: topic.status, classification: topic.classification,
    approvedGeneralTopic: topic.approvedGeneralTopic, topicId: topic.topicId ?? null,
    ownerReference: topic.ownerReference ?? null, duplicateOf: topic.duplicateOf ?? null,
    privacyDelete: false, resolution: topic.resolution ?? null,
  });
  const [busy, setBusy] = useState(false);
  const operation = useRef<string | null>(null);
  const [delivery, setDelivery] = useState<DeliveryStatus | null>(null);
  const [deliveryBusy, setDeliveryBusy] = useState(false);
  const retryOperation = useRef<string | null>(null);
  const update = (next: Partial<TopicChange>) => { setChange(current => ({ ...current, ...next })); operation.current = null; };
  return <section aria-label="Review question" style={{ marginBlock: 24 }}>
    <h3>Review question</h3><p>{topic.generalQuestion}</p>
    <Space orientation="vertical" style={{ width: '100%' }}>
      <label>Classification <Select value={change.classification} disabled={busy} style={{ minWidth: 240 }}
        options={classifications.map(value => ({ value, label: value }))} onChange={value => { update({ classification: value }); }} /></label>
      <label>Status <Select value={change.status} disabled={busy} style={{ minWidth: 240 }}
        options={statuses.map(value => ({ value, label: value }))} onChange={value => { update({ status: value }); }} /></label>
      <Checkbox checked={change.approvedGeneralTopic} disabled={busy} onChange={event => { update({ approvedGeneralTopic: event.target.checked }); }}>Privacy reviewed: this is a general product topic</Checkbox>
      <label>Reviewed topic reference<Input value={change.topicId ?? ''} maxLength={128} disabled={busy} autoComplete="off" onChange={event => { update({ topicId: event.target.value || null }); }} /></label>
      <label>Responsible owner reference<Input value={change.ownerReference ?? ''} maxLength={128} disabled={busy} autoComplete="off" onChange={event => { update({ ownerReference: event.target.value || null }); }} /></label>
      <label>Duplicate question reference<Input value={change.duplicateOf ?? ''} maxLength={100} disabled={busy} autoComplete="off" onChange={event => { update({ duplicateOf: event.target.value || null }); }} /></label>
      {(change.status === 'Ready for verification' || change.status === 'Resolved') && <>
        <p>Resolution needs an approved change, a published version and verification evidence. The owner verifies these references before accepting Resolved.</p>
        {(['approvedChangeReference', 'publishedVersion', 'verificationReference'] as const).map((field, index) => <label key={field}>
          {['Approved change reference', 'Published version', 'Verification evidence reference'][index]}
          <Input value={change.resolution?.[field] ?? ''} maxLength={256} disabled={busy} autoComplete="off" onChange={event => {
            update({ resolution: { approvedChangeReference: '', publishedVersion: '', verificationReference: '', ...change.resolution, [field]: event.target.value } });
          }} />
        </label>)}
      </>}
      <Checkbox checked={change.privacyDelete} disabled={busy} onChange={event => { update({ privacyDelete: event.target.checked }); }}>Delete this submitted text for privacy. This cannot be undone in the review view.</Checkbox>
      <Button type="primary" danger={change.privacyDelete} loading={busy} onClick={() => {
        setBusy(true); operation.current ??= crypto.randomUUID();
        void onSave(change, operation.current).finally(() => { setBusy(false); });
      }}>{change.privacyDelete ? 'Delete submitted text' : 'Save review'}</Button>
      <details><summary>Review history delivery</summary>
        <p>A saved change remains saved while its review history is waiting for delivery. Retrying history does not repeat the change.</p>
        <Button loading={deliveryBusy} onClick={() => {
          setDeliveryBusy(true);
          void onDelivery().then(result => { if (result) setDelivery(result); }).finally(() => { setDeliveryBusy(false); });
        }}>Check history delivery</Button>
        {delivery && <p>Recorded: {delivery.deliveries.filter(item => item.state === 'Accepted').length}. Waiting: {delivery.deliveries.filter(item => item.state === 'Pending').length}. Needs retry: {delivery.deliveries.filter(item => item.state === 'Paused').length}.</p>}
        {delivery?.deliveries.some(item => item.state === 'Paused' && item.recoveries < delivery.recoveryLimit) && <Button loading={deliveryBusy} onClick={() => {
          retryOperation.current ??= crypto.randomUUID(); setDeliveryBusy(true);
          void onDelivery(retryOperation.current).then(result => { if (result) { setDelivery(result); retryOperation.current = null; } }).finally(() => { setDeliveryBusy(false); });
        }}>Retry waiting history</Button>}
      </details>
    </Space>
  </section>;
}
