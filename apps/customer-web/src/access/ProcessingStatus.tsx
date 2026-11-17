import { Alert, Button, Checkbox, Descriptions, Drawer, Empty, Input, Modal, Select, Space, Table, Tag } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { AccessApiError, displayError } from './accessApi';
import type { AccessApi } from './accessApi';
import { identifier, invalidProcessingResponse, processingPending, processingStates, stateLabel, validAttempts, validHistory, validJob, validLabels, validQueue, validReceipt } from './processingAdministrationApi';
import type { AdminJob, AttemptPage, CustomerLabel, CustomerLabels, JobChange, JobHistory, JobReceipt, QueuePage } from './processingAdministrationApi';

export function ProcessingStatus(props: { api: AccessApi; tenantId: string; actorId: string; audience?: 'administrator' | 'operations-support' }) {
 return <ProcessingStatusView key={props.tenantId + ':' + props.actorId + ':' + (props.audience ?? 'administrator')} {...props} />;
}
function ProcessingStatusView({ api, tenantId, actorId, audience = 'administrator' }: { api: AccessApi; tenantId: string; actorId: string; audience?: 'administrator' | 'operations-support' }) {
 const scope = tenantId + ':' + actorId;
 const current = useRef({ api, scope }); const mounted = useRef(true); const loading = useRef(false);
 useEffect(() => { current.current = { api, scope }; }, [api, scope]);
 useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
 const same = () => mounted.current && current.current.api === api && current.current.scope === scope;
 const [documentVersionId, setDocumentVersionId] = useState(() => new URLSearchParams(window.location.search).get('documentVersionId'));
 const [customerId, setCustomerId] = useState(() => new URLSearchParams(window.location.search).get('customerId'));
 const [page, setPage] = useState<QueuePage | null>(null); const [labels, setLabels] = useState<Record<string, CustomerLabel>>({});
 const [search, setSearch] = useState(''); const [query, setQuery] = useState({ search: '', state: 'all' }); const [after, setAfter] = useState<string | null>(null);
 const [revision, setRevision] = useState(0); const [checkedAt, setCheckedAt] = useState<string | null>(null);
 const [error, setError] = useState(''); const [notice, setNotice] = useState(''); const [busy, setBusy] = useState(false);
 const [detailId, setDetailId] = useState<string | null>(null); const [detail, setDetail] = useState<AdminJob | null>(null);
 const [attempts, setAttempts] = useState<AttemptPage | null>(null); const [history, setHistory] = useState<JobHistory | null>(null);
 const [beforeAttempt, setBeforeAttempt] = useState<number | null>(null); const [beforeHistory, setBeforeHistory] = useState<string | null>(null);
 const [draft, setDraft] = useState<{ job: AdminJob; customerName: string; action: 'retry' | 'cancel' } | null>(null);
 const [reason, setReason] = useState(''); const [reviewed, setReviewed] = useState(false);
 const [pending, setPending] = useState(processingPending.get(scope) ?? null); const pendingRef = useRef(pending);
 useEffect(() => {
  if (customerId !== null && !identifier(customerId) || documentVersionId !== null && !/^[A-Za-z0-9][A-Za-z0-9._:-]{0,159}$/.test(documentVersionId)) return;
  const controller = new AbortController(); loading.current = true;
  const params = new URLSearchParams(); if (query.search) params.set('search', query.search); if (query.state !== 'all') params.set('state', query.state); if (customerId) params.set('customerId', customerId); if (after) params.set('after', after); if (documentVersionId) params.set('documentVersionId', documentVersionId);
  void (async () => {
   const value = audience === 'operations-support'
    ? await api.processingOperations<QueuePage>(params.size ? '?' + params.toString() : '', 'GET', undefined, controller.signal)
    : await api.processingAdministration<QueuePage>(params.size ? '?' + params.toString() : '', 'GET', undefined, controller.signal);
   controller.signal.throwIfAborted(); if (!validQueue(value, tenantId) || value.items.some(row => documentVersionId !== null && row.documentVersionId !== documentVersionId || customerId !== null && row.customerId !== customerId || query.state !== 'all' && row.state !== query.state || !row.jobId.toLowerCase().includes(query.search.toLowerCase()))) invalidProcessingResponse();
   const ids = [...new Set([...value.items.map(row => row.customerId), ...(customerId ? [customerId] : [])])];
   const names = audience === 'administrator' && ids.length
    ? await api.customerAdministration<CustomerLabels>('/labels', 'POST', { customerIds: ids }, controller.signal)
    : { tenantId, items: [], unavailable: ids };
   controller.signal.throwIfAborted(); if (!validLabels(names, tenantId, ids)) invalidProcessingResponse();
   setLabels(Object.fromEntries(names.items.map(row => [row.customerId, row]))); setPage(value); setCheckedAt(new Date().toISOString()); setError('');
  })().catch((e: unknown) => { if (!controller.signal.aborted) { setPage(null); setLabels({}); setError(displayError(e)); } }).finally(() => { if (!controller.signal.aborted) loading.current = false; });
  return () => { controller.abort(); loading.current = false; };
 }, [api, tenantId, customerId, documentVersionId, query, after, revision, audience]);
 useEffect(() => {
  if (!detailId) return; const controller = new AbortController(); const root = '/' + encodeURIComponent(detailId);
  void Promise.all([
   audience === 'operations-support' ? api.processingOperations<AdminJob>(root, 'GET', undefined, controller.signal) : api.processingAdministration<AdminJob>(root, 'GET', undefined, controller.signal),
   audience === 'operations-support' ? api.processingOperations<AttemptPage>(root + '/attempts' + (beforeAttempt ? '?beforeAttempt=' + String(beforeAttempt) : ''), 'GET', undefined, controller.signal) : api.processingAdministration<AttemptPage>(root + '/attempts' + (beforeAttempt ? '?beforeAttempt=' + String(beforeAttempt) : ''), 'GET', undefined, controller.signal),
   audience === 'operations-support' ? api.processingOperations<JobHistory>(root + '/history' + (beforeHistory ? '?beforeId=' + beforeHistory : ''), 'GET', undefined, controller.signal) : api.processingAdministration<JobHistory>(root + '/history' + (beforeHistory ? '?beforeId=' + beforeHistory : ''), 'GET', undefined, controller.signal),
  ]).then(([job, activity, changes]) => {
   if (controller.signal.aborted) return;
   if (!validJob(job, tenantId, detailId) || !validAttempts(activity, tenantId, detailId, beforeAttempt) || !validHistory(changes, tenantId, detailId)) invalidProcessingResponse();
   setDetail(job); setAttempts(activity); setHistory(changes);
  }).catch((e: unknown) => { if (!controller.signal.aborted) { setDetail(null); setAttempts(null); setHistory(null); setError(displayError(e)); } });
  return () => { controller.abort(); };
 }, [api, tenantId, detailId, beforeAttempt, beforeHistory, revision, audience]);
 useEffect(() => {
  if (draft || busy || pending) return;
  const timer = setInterval(() => { if (!document.hidden && !loading.current) setRevision(n => n + 1); }, 15_000);
  return () => { clearInterval(timer); };
 }, [draft, busy, pending]);
 function refresh() { setAfter(null); setRevision(n => n + 1); }
 function openReview(job: AdminJob, action: 'retry' | 'cancel') { if (pendingRef.current || busy) return; setDraft({ job, action, customerName: labels[job.customerId]?.displayName ?? 'Customer reference ' + job.customerId }); setReason(''); setReviewed(false); setError(''); }
 async function save() {
  if (busy || !pending && (!draft || !reviewed || !reason.trim())) return;
  const operation = pending ?? (draft ? { job: draft.job, customerName: draft.customerName, change: { operationId: crypto.randomUUID().replaceAll('-', ''), expectedRevision: draft.job.revision, action: draft.action, reason: reason.trim() } satisfies JobChange } : null);
  if (!operation) return; processingPending.set(scope, operation); pendingRef.current = operation; setPending(operation); setBusy(true); setError('');
  try {
   const root = '/' + encodeURIComponent(operation.job.jobId); const result = audience === 'operations-support' ? await api.processingOperations<JobReceipt>(root + '/changes', 'POST', operation.change) : await api.processingAdministration<JobReceipt>(root + '/changes', 'POST', operation.change);
   if (!same()) return;
   if (!validReceipt(result, tenantId, operation.job.jobId) || result.operationId !== operation.change.operationId || result.action !== operation.change.action || result.reason !== operation.change.reason || result.before.revision !== operation.change.expectedRevision || result.recordedBy !== actorId) invalidProcessingResponse();
   const saved = audience === 'operations-support' ? await api.processingOperations<JobReceipt>(root + '/changes/' + operation.change.operationId) : await api.processingAdministration<JobReceipt>(root + '/changes/' + operation.change.operationId);
   if (!same()) return; if (JSON.stringify(saved) !== JSON.stringify(result)) invalidProcessingResponse();
   processingPending.delete(scope); pendingRef.current = null; setPending(null); setDraft(null);
   setNotice(result.action === 'retry' ? 'Try again requested. Customer permission will be checked before the task starts.' : result.after.cancellationPending ? 'Cancellation requested. We are waiting for the task to stop.' : 'Task cancelled.');
   setBeforeAttempt(null); setBeforeHistory(null); refresh();
  } catch (e) {
   if (!same()) return;
   if (e instanceof AccessApiError && [400, 409].includes(e.status)) { processingPending.delete(scope); pendingRef.current = null; setPending(null); if (e.status === 409) { setDraft(null); setDetail(null); setDetailId(null); refresh(); } }
   setError(e instanceof AccessApiError && e.status === 409 ? 'This task changed while you were working. Refresh and review the latest status.' : displayError(e));
  } finally { if (same()) setBusy(false); }
 }
 function openDetail(jobId: string) { setDetail(null); setAttempts(null); setHistory(null); setBeforeAttempt(null); setBeforeHistory(null); setDetailId(jobId); }
 const review = pending ? { job: pending.job, customerName: pending.customerName, action: pending.change.action } : draft;
 const when = (value: string | null) => value ? new Date(value).toLocaleString() : 'Not recorded';
 const currentName = (id: string) => labels[id]?.displayName ?? (audience === 'operations-support' ? 'Customer reference ' + id : 'Customer unavailable');
 if (customerId !== null && !identifier(customerId) || documentVersionId !== null && !/^[A-Za-z0-9][A-Za-z0-9._:-]{0,159}$/.test(documentVersionId)) return <Alert type="error" title="Invalid customer selection. Open Processing Status from Customers or clear the selection." action={<Button onClick={() => { setCustomerId(null); setDocumentVersionId(null); }}>All customers</Button>} />;
 return <section className="access-page" aria-labelledby="processing-title">
  <p className="workspace-eyebrow">{audience === 'operations-support' ? 'OPERATIONS SUPPORT WORKSPACE' : 'ORGANIZATION SETTINGS'}</p>
  <h1 id="processing-title">{audience === 'operations-support' ? 'Tasks needing attention' : 'Document processing'}</h1><p>Review processing tasks, understand what happened and take an available recovery action.</p>
  <p>This list refreshes every 15 seconds. Trying again uses the customer's original permission. Tasks with an uncertain outcome need review before another action can be taken.{audience === 'operations-support' && ' Customer financial information and professional decisions are not shown here.'}</p>
  {error && <Alert type="error" showIcon title={error} />}{notice && <Alert type="success" showIcon title={notice} />}
  {pending && <Alert type="warning" showIcon title="Confirm the unfinished change" description={`The change for ${pending.customerName} has not been confirmed. Check it before starting another recovery action.`} action={<Button disabled={busy} onClick={() => { setDraft({ job: pending.job, customerName: pending.customerName, action: pending.change.action }); }}>Review change</Button>} />}
  {documentVersionId && <Space><Tag>One document selected</Tag><Button onClick={() => { setDocumentVersionId(null); setAfter(null); window.history.replaceState(null, '', '/access/processing'); }}>All documents</Button></Space>}
  {customerId && <Space><Tag>Customer: {currentName(customerId)}</Tag><Button onClick={() => { window.history.replaceState(null, '', '/access/processing'); setCustomerId(null); setAfter(null); setPage(null); }}>All customers</Button></Space>}
  <Space wrap><Input.Search aria-label="Find task" placeholder="Find a task" value={search} maxLength={128} onChange={e => { setSearch(e.target.value); }} onSearch={() => { setAfter(null); setPage(null); setQuery(q => ({ ...q, search: search.trim() })); }} enterButton="Search tasks" />
   <Select aria-label="Processing state filter" value={query.state} options={[{ value: 'all', label: 'All states' }, ...processingStates.map(state => ({ value: state, label: stateLabel(state) }))]} onChange={state => { setAfter(null); setPage(null); setQuery(q => ({ ...q, state })); }} />
   <Button onClick={refresh}>Refresh tasks</Button>{audience === 'administrator' && <Button href="/access/customers">Find a customer</Button>}
  </Space>
  {page && <><Space wrap>{processingStates.map(state => <Tag key={state}>{stateLabel(state)}: {page.counts[state.toLowerCase() as keyof QueuePage['counts']]}</Tag>)}</Space><p>{page.matching} matching tasks. Last checked: {when(checkedAt)}</p></>}
  <Table<AdminJob> rowKey="jobId" dataSource={page?.items ?? []} loading={!page && !error} pagination={false} locale={{ emptyText: <Empty description="There are no tasks matching this view." /> }} columns={[
   { title: 'Task', key: 'task', render: (_, row) => <strong>{row.task}</strong> },
   { title: audience === 'operations-support' ? 'Customer reference' : 'Customer', key: 'customer', render: (_, row) => <><strong>{currentName(row.customerId)}</strong>{audience === 'administrator' && <><br /><small>{labels[row.customerId]?.secondaryLabel ?? row.customerId}</small></>}</> },
   { title: 'Status', key: 'state', render: (_, row) => <><Tag color={row.state === 'FAILED' ? 'red' : row.state === 'COMPLETED' ? 'green' : 'blue'}>{stateLabel(row.state)}</Tag>{row.cancellationPending && <p>Cancellation requested</p>}{row.needsOwnerReview && <p>Owner review required</p>}</> },
   { title: 'Attempts', dataIndex: 'attempt', key: 'attempt' },
   { title: 'Updated', key: 'updated', render: (_, row) => when(row.updatedAt) },
   { title: 'Details', key: 'detail', render: (_, row) => <Button aria-label={'View task ' + row.task} onClick={() => { openDetail(row.jobId); }}>View details</Button> },
  ]} />
  {after && <Button onClick={() => { setAfter(null); setPage(null); }}>First page</Button>}{page?.nextCursor && <Button onClick={() => { setAfter(page.nextCursor); setPage(null); }}>Next tasks</Button>}
  <Drawer title="Task details" open={detailId !== null} size={720} onClose={() => { setDetailId(null); setDetail(null); }}>
   {detail && <><h2>{detail.task} — {currentName(detail.customerId)}</h2><Descriptions column={1} items={[
    { key: 'state', label: 'Status', children: stateLabel(detail.state) }, { key: 'requested', label: 'Requested', children: when(detail.requestedAt) }, { key: 'updated', label: 'Updated', children: when(detail.updatedAt) }, { key: 'next', label: 'Next attempt', children: when(detail.nextAttemptAt) }, { key: 'attempts', label: 'Attempts', children: detail.attempt }, { key: 'retries', label: 'Automatic retries', children: detail.retryCount }, { key: 'replays', label: 'Requested retries', children: detail.replayCount },
   ]} />
    {detail.cancellationPending && <Alert type="info" title="Cancellation requested — awaiting worker outcome" />}{detail.needsOwnerReview && <Alert type="warning" title="Owner review required" description="The outcome of an earlier attempt is uncertain. Recovery stays unavailable until the owner confirms what happened." />}
    <Space>{detail.canRetry && <Button disabled={!!pending || busy} onClick={() => { openReview(detail, 'retry'); }}>Try again</Button>}{detail.canCancel && <Button danger disabled={!!pending || busy} onClick={() => { openReview(detail, 'cancel'); }}>Cancel task</Button>}</Space>
    {!detail.canRetry && !detail.canCancel && !detail.cancellationPending && !detail.needsOwnerReview && <p>No recovery action is available for this job. Failed or cancelled tasks may need a new customer request.</p>}
    <h3>Attempt history</h3>{attempts?.items.length === 0 && <p>No attempts have been recorded.</p>}{attempts?.items.map(row => <article key={row.attempt}><h4>Attempt {row.attempt}</h4><p>Started: {when(row.startedAt)} · Ended: {when(row.endedAt)}</p><p>Outcome: {row.outcome?.replaceAll('_', ' ') ?? 'In progress'}</p></article>)}
    {beforeAttempt && <Button onClick={() => { setAttempts(null); setBeforeAttempt(null); }}>Latest attempts</Button>}{attempts?.nextBeforeAttempt && <Button onClick={() => { setAttempts(null); setBeforeAttempt(attempts.nextBeforeAttempt); }}>Earlier attempts</Button>}
    <h3>Recovery history</h3>{history?.items.length === 0 && <p>No recovery actions have been recorded.</p>}{history?.items.map(row => <article key={row.operationId}><h4>{row.action === 'retry' ? 'Try again requested' : row.after.cancellationPending ? 'Cancellation requested' : 'Cancelled'} · {when(row.recordedAt)}</h4><p>{row.reason}</p><p>{audience === 'operations-support' ? 'Operations Support' : 'Organization Administrator'}</p></article>)}
    {beforeHistory && <Button onClick={() => { setHistory(null); setBeforeHistory(null); }}>Latest recovery requests</Button>}{history?.nextBeforeId && <Button onClick={() => { setHistory(null); setBeforeHistory(history.nextBeforeId); }}>Earlier recovery requests</Button>}
   </>}
  </Drawer>
  <Modal title={review?.action === 'retry' ? 'Try this task again?' : 'Cancel this task?'} open={draft !== null} onCancel={() => { if (!busy) setDraft(null); }} footer={<Space><Button disabled={busy} onClick={() => { setDraft(null); }}>Close review</Button><Button type="primary" danger={review?.action === 'cancel'} loading={busy} disabled={!pending && (!reviewed || !reason.trim())} onClick={() => { void save(); }}>{pending ? 'Check again' : review?.action === 'retry' ? 'Try again' : 'Cancel task'}</Button></Space>}>
   {review && <><h3>{review.customerName}</h3><p>{review.job.task} · Status: {stateLabel(review.job.state)}</p><p>{review.action === 'retry' ? 'This schedules the same task again. Customer permission is checked before it starts, and success is confirmed separately.' : 'Queued work can be cancelled immediately. Work already in progress will remain visible until cancellation is confirmed.'}</p>
   {pending ? <p>Reason: {pending.change.reason}</p> : <><label htmlFor="processing-change-reason">Reason</label><Input.TextArea id="processing-change-reason" maxLength={500} value={reason} onChange={e => { setReason(e.target.value); }} /><Checkbox checked={reviewed} onChange={e => { setReviewed(e.target.checked); }}>I have reviewed this customer, task and action.</Checkbox></>}</>}
  </Modal>
 </section>;
}
