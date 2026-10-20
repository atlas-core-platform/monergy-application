import { Alert, Button, Checkbox, Descriptions, Drawer, Empty, Input, Modal, Select, Space, Table, Tag } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { AccessApiError, displayError } from './accessApi';
import type { AccessApi } from './accessApi';
import { identifier, invalidProcessingResponse, processingPending, processingStates, stateLabel, validAttempts, validHistory, validJob, validLabels, validQueue, validReceipt } from './processingAdministrationApi';
import type { AdminJob, AttemptPage, CustomerLabel, CustomerLabels, JobChange, JobHistory, JobReceipt, QueuePage } from './processingAdministrationApi';

export function ProcessingStatus(props: { api: AccessApi; tenantId: string; actorId: string }) {
 return <ProcessingStatusView key={props.tenantId + ':' + props.actorId} {...props} />;
}
function ProcessingStatusView({ api, tenantId, actorId }: { api: AccessApi; tenantId: string; actorId: string }) {
 const scope = tenantId + ':' + actorId;
 const current = useRef({ api, scope }); const mounted = useRef(true); const loading = useRef(false);
 useEffect(() => { current.current = { api, scope }; }, [api, scope]);
 useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
 const same = () => mounted.current && current.current.api === api && current.current.scope === scope;
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
  if (customerId !== null && !identifier(customerId)) return;
  const controller = new AbortController(); loading.current = true;
  const params = new URLSearchParams(); if (query.search) params.set('search', query.search); if (query.state !== 'all') params.set('state', query.state); if (customerId) params.set('customerId', customerId); if (after) params.set('after', after);
  void (async () => {
   const value = await api.processingAdministration<QueuePage>(params.size ? '?' + params.toString() : '', 'GET', undefined, controller.signal);
   controller.signal.throwIfAborted(); if (!validQueue(value, tenantId) || value.items.some(row => customerId !== null && row.customerId !== customerId || query.state !== 'all' && row.state !== query.state || !row.jobId.toLowerCase().includes(query.search.toLowerCase()))) invalidProcessingResponse();
   const ids = [...new Set([...value.items.map(row => row.customerId), ...(customerId ? [customerId] : [])])];
   const names = ids.length ? await api.customerAdministration<CustomerLabels>('/labels', 'POST', { customerIds: ids }, controller.signal) : { tenantId, items: [], unavailable: [] };
   controller.signal.throwIfAborted(); if (!validLabels(names, tenantId, ids)) invalidProcessingResponse();
   setLabels(Object.fromEntries(names.items.map(row => [row.customerId, row]))); setPage(value); setCheckedAt(new Date().toISOString()); setError('');
  })().catch((e: unknown) => { if (!controller.signal.aborted) { setPage(null); setLabels({}); setError(displayError(e)); } }).finally(() => { if (!controller.signal.aborted) loading.current = false; });
  return () => { controller.abort(); loading.current = false; };
 }, [api, tenantId, customerId, query, after, revision]);
 useEffect(() => {
  if (!detailId) return; const controller = new AbortController(); const root = '/' + encodeURIComponent(detailId);
  void Promise.all([
   api.processingAdministration<AdminJob>(root, 'GET', undefined, controller.signal),
   api.processingAdministration<AttemptPage>(root + '/attempts' + (beforeAttempt ? '?beforeAttempt=' + String(beforeAttempt) : ''), 'GET', undefined, controller.signal),
   api.processingAdministration<JobHistory>(root + '/history' + (beforeHistory ? '?beforeId=' + beforeHistory : ''), 'GET', undefined, controller.signal),
  ]).then(([job, activity, changes]) => {
   if (controller.signal.aborted) return;
   if (!validJob(job, tenantId, detailId) || !validAttempts(activity, tenantId, detailId, beforeAttempt) || !validHistory(changes, tenantId, detailId)) invalidProcessingResponse();
   setDetail(job); setAttempts(activity); setHistory(changes);
  }).catch((e: unknown) => { if (!controller.signal.aborted) { setDetail(null); setAttempts(null); setHistory(null); setError(displayError(e)); } });
  return () => { controller.abort(); };
 }, [api, tenantId, detailId, beforeAttempt, beforeHistory, revision]);
 useEffect(() => {
  if (draft || busy || pending) return;
  const timer = setInterval(() => { if (!document.hidden && !loading.current) setRevision(n => n + 1); }, 15_000);
  return () => { clearInterval(timer); };
 }, [draft, busy, pending]);
 function refresh() { setAfter(null); setRevision(n => n + 1); }
 function openReview(job: AdminJob, action: 'retry' | 'cancel') { if (pendingRef.current || busy) return; setDraft({ job, action, customerName: labels[job.customerId]?.displayName ?? 'Customer unavailable' }); setReason(''); setReviewed(false); setError(''); }
 async function save() {
  if (busy || !pending && (!draft || !reviewed || !reason.trim())) return;
  const operation = pending ?? (draft ? { job: draft.job, customerName: draft.customerName, change: { operationId: crypto.randomUUID().replaceAll('-', ''), expectedRevision: draft.job.revision, action: draft.action, reason: reason.trim() } satisfies JobChange } : null);
  if (!operation) return; processingPending.set(scope, operation); pendingRef.current = operation; setPending(operation); setBusy(true); setError('');
  try {
   const root = '/' + encodeURIComponent(operation.job.jobId); const result = await api.processingAdministration<JobReceipt>(root + '/changes', 'POST', operation.change);
   if (!same()) return;
   if (!validReceipt(result, tenantId, operation.job.jobId) || result.operationId !== operation.change.operationId || result.action !== operation.change.action || result.reason !== operation.change.reason || result.before.revision !== operation.change.expectedRevision || result.recordedBy !== actorId) invalidProcessingResponse();
   const saved = await api.processingAdministration<JobReceipt>(root + '/changes/' + operation.change.operationId);
   if (!same()) return; if (JSON.stringify(saved) !== JSON.stringify(result)) invalidProcessingResponse();
   processingPending.delete(scope); pendingRef.current = null; setPending(null); setDraft(null);
   setNotice(result.action === 'retry' ? 'Retry requested. Customer permission is checked again before execution.' : result.after.cancellationPending ? 'Cancellation requested. The worker has not confirmed cancellation yet.' : 'Job cancellation confirmed.');
   setBeforeAttempt(null); setBeforeHistory(null); refresh();
  } catch (e) {
   if (!same()) return;
   if (e instanceof AccessApiError && [400, 409].includes(e.status)) { processingPending.delete(scope); pendingRef.current = null; setPending(null); if (e.status === 409) { setDraft(null); setDetail(null); setDetailId(null); refresh(); } }
   setError(e instanceof AccessApiError && e.status === 409 ? 'The job or its customer permission has changed. Refresh and review the job. A customer may need to sign in and request processing again.' : displayError(e));
  } finally { if (same()) setBusy(false); }
 }
 function openDetail(jobId: string) { setDetail(null); setAttempts(null); setHistory(null); setBeforeAttempt(null); setBeforeHistory(null); setDetailId(jobId); }
 const review = pending ? { job: pending.job, customerName: pending.customerName, action: pending.change.action } : draft;
 const when = (value: string | null) => value ? new Date(value).toLocaleString() : 'Not recorded';
 const currentName = (id: string) => labels[id]?.displayName ?? 'Customer unavailable';
 if (customerId !== null && !identifier(customerId)) return <Alert type="error" title="Invalid customer selection. Open Processing Status from Customers or clear the selection." action={<Button onClick={() => { setCustomerId(null); }}>All customers</Button>} />;
 return <section className="access-page" aria-labelledby="processing-title">
  <h1 id="processing-title">Processing Status</h1><p>Review jobs recorded for your organization, inspect failures and manage permitted recovery.</p>
  <p>Refreshes every 15 seconds while this page is visible. Retry uses the original customer permission. Jobs with an uncertain outcome require owner review.</p>
  {error && <Alert type="error" showIcon title={error} />}{notice && <Alert type="success" showIcon title={notice} />}
  {pending && <Alert type="warning" showIcon title="Confirm the pending processing change" description={`The request for ${pending.customerName} has not been confirmed. Retry the same request before another recovery action.`} action={<Button disabled={busy} onClick={() => { setDraft({ job: pending.job, customerName: pending.customerName, action: pending.change.action }); }}>Review pending request</Button>} />}
  {customerId && <Space><Tag>Customer: {currentName(customerId)}</Tag><Button onClick={() => { window.history.replaceState(null, '', '/access/processing'); setCustomerId(null); setAfter(null); setPage(null); }}>All customers</Button></Space>}
  <Space wrap><Input.Search aria-label="Find job reference" placeholder="Job reference" value={search} maxLength={128} onChange={e => { setSearch(e.target.value); }} onSearch={() => { setAfter(null); setPage(null); setQuery(q => ({ ...q, search: search.trim() })); }} enterButton="Search jobs" />
   <Select aria-label="Processing state filter" value={query.state} options={[{ value: 'all', label: 'All states' }, ...processingStates.map(state => ({ value: state, label: stateLabel(state) }))]} onChange={state => { setAfter(null); setPage(null); setQuery(q => ({ ...q, state })); }} />
   <Button onClick={refresh}>Refresh processing</Button><Button href="/access/customers">Find a customer</Button>
  </Space>
  {page && <><Space wrap>{processingStates.map(state => <Tag key={state}>{stateLabel(state)}: {page.counts[state.toLowerCase() as keyof QueuePage['counts']]}</Tag>)}</Space><p>{page.matching} matching jobs. Last checked: {when(checkedAt)}</p></>}
  <Table<AdminJob> rowKey="jobId" dataSource={page?.items ?? []} loading={!page && !error} pagination={false} locale={{ emptyText: <Empty description="No jobs match this view" /> }} columns={[
   { title: 'Task', key: 'task', render: (_, row) => <><strong>{row.task}</strong><br /><small>Job reference: {row.jobId}</small></> },
   { title: 'Customer', key: 'customer', render: (_, row) => <><strong>{currentName(row.customerId)}</strong><br /><small>{labels[row.customerId]?.secondaryLabel ?? row.customerId}</small></> },
   { title: 'Status', key: 'state', render: (_, row) => <><Tag color={row.state === 'FAILED' ? 'red' : row.state === 'COMPLETED' ? 'green' : 'blue'}>{stateLabel(row.state)}</Tag>{row.cancellationPending && <p>Cancellation requested</p>}{row.needsOwnerReview && <p>Owner review required</p>}</> },
   { title: 'Attempts', dataIndex: 'attempt', key: 'attempt' },
   { title: 'Updated', key: 'updated', render: (_, row) => when(row.updatedAt) },
   { title: 'Details', key: 'detail', render: (_, row) => <Button aria-label={'View job ' + row.jobId} onClick={() => { openDetail(row.jobId); }}>View details</Button> },
  ]} />
  {after && <Button onClick={() => { setAfter(null); setPage(null); }}>First job page</Button>}{page?.nextCursor && <Button onClick={() => { setAfter(page.nextCursor); setPage(null); }}>Next jobs</Button>}
  <Drawer title="Job details" open={detailId !== null} size={720} onClose={() => { setDetailId(null); setDetail(null); }}>
   {detail && <><h2>{detail.task} — {currentName(detail.customerId)}</h2><Descriptions column={1} items={[
    { key: 'job', label: 'Job reference', children: detail.jobId }, { key: 'state', label: 'Status', children: stateLabel(detail.state) }, { key: 'requested', label: 'Requested', children: when(detail.requestedAt) }, { key: 'updated', label: 'Updated', children: when(detail.updatedAt) }, { key: 'next', label: 'Next attempt', children: when(detail.nextAttemptAt) }, { key: 'attempts', label: 'Attempts', children: detail.attempt }, { key: 'retries', label: 'Automatic retries', children: detail.retryCount }, { key: 'replays', label: 'Requested retries', children: detail.replayCount }, { key: 'failure', label: 'Failure code', children: detail.failureCode ?? 'None' },
   ]} />
    {detail.cancellationPending && <Alert type="info" title="Cancellation requested — awaiting worker outcome" />}{detail.needsOwnerReview && <Alert type="warning" title="Owner review required" description="The outcome of an earlier attempt is uncertain. Recovery stays unavailable until the owner confirms what happened." />}
    <Space>{detail.canRetry && <Button disabled={!!pending || busy} onClick={() => { openReview(detail, 'retry'); }}>Retry now</Button>}{detail.canCancel && <Button danger disabled={!!pending || busy} onClick={() => { openReview(detail, 'cancel'); }}>Request cancellation</Button>}</Space>
    {!detail.canRetry && !detail.canCancel && !detail.cancellationPending && !detail.needsOwnerReview && <p>No recovery action is available for this job. Failed or cancelled tasks may need a new customer request.</p>}
    <h3>Execution attempts</h3>{attempts?.items.length === 0 && <p>No execution attempts recorded.</p>}{attempts?.items.map(row => <article key={row.attempt}><h4>Attempt {row.attempt}</h4><p>Started: {when(row.startedAt)} · Ended: {when(row.endedAt)}</p><p>Outcome: {row.outcome?.replaceAll('_', ' ') ?? 'In progress'} · Failure code: {row.failureCode ?? 'None'}</p></article>)}
    {beforeAttempt && <Button onClick={() => { setAttempts(null); setBeforeAttempt(null); }}>Latest attempts</Button>}{attempts?.nextBeforeAttempt && <Button onClick={() => { setAttempts(null); setBeforeAttempt(attempts.nextBeforeAttempt); }}>Earlier attempts</Button>}
    <h3>Administrator recovery history</h3>{history?.items.length === 0 && <p>No administrator recovery requests recorded.</p>}{history?.items.map(row => <article key={row.operationId}><h4>{row.action === 'retry' ? 'Retry requested' : row.after.cancellationPending ? 'Cancellation requested' : 'Cancelled'} · {when(row.recordedAt)}</h4><p>{row.reason}</p><p>Organization administrator · User reference: {row.recordedBy} · Change reference: {row.operationId}</p></article>)}
    {beforeHistory && <Button onClick={() => { setHistory(null); setBeforeHistory(null); }}>Latest recovery requests</Button>}{history?.nextBeforeId && <Button onClick={() => { setHistory(null); setBeforeHistory(history.nextBeforeId); }}>Earlier recovery requests</Button>}
   </>}
  </Drawer>
  <Modal title={review?.action === 'retry' ? 'Review job retry' : 'Review job cancellation'} open={draft !== null} onCancel={() => { if (!busy) setDraft(null); }} footer={<Space><Button disabled={busy} onClick={() => { setDraft(null); }}>Close review</Button><Button type="primary" danger={review?.action === 'cancel'} loading={busy} disabled={!pending && (!reviewed || !reason.trim())} onClick={() => { void save(); }}>{pending ? 'Retry confirmation' : review?.action === 'retry' ? 'Confirm retry request' : 'Confirm cancellation request'}</Button></Space>}>
   {review && <><h3>{review.customerName}</h3><p>{review.job.task} · Job reference: {review.job.jobId} · Current reviewed status: {stateLabel(review.job.state)}</p><p>{review.action === 'retry' ? 'This schedules the same task again. The original customer permission is checked before execution; this does not confirm a successful result.' : 'Queued work can be cancelled immediately. Running work remains processing until the worker confirms its outcome.'}</p>
   {pending ? <p>Reason: {pending.change.reason}</p> : <><label htmlFor="processing-change-reason">Reason for recovery</label><Input.TextArea id="processing-change-reason" maxLength={500} value={reason} onChange={e => { setReason(e.target.value); }} /><Checkbox checked={reviewed} onChange={e => { setReviewed(e.target.checked); }}>I have reviewed this customer, task and recovery action.</Checkbox></>}</>}
  </Modal>
 </section>;
}
