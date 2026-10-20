import { Alert, Button, Checkbox, Descriptions, Drawer, Empty, Input, Modal, Select, Space, Table, Tag } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { AccessApiError, displayError } from './accessApi';
import type { AccessApi } from './accessApi';
import { boundCustomer, boundReceipt, invalidCustomerResponse, pendingCustomerChanges } from './customerManagementApi';
import type { AdminCustomer, AdminCustomerPage, StatusChange, StatusHistory, StatusReceipt } from './customerManagementApi';

export function CustomerManagement(props: { api: AccessApi; tenantId: string; actorId: string }) {
 return <CustomerManagementView key={props.tenantId + ':' + props.actorId} {...props} />;
}
function CustomerManagementView({ api, tenantId, actorId }: { api: AccessApi; tenantId: string; actorId: string }) {
 const scope = tenantId + ':' + actorId;
 const context = useRef({ api, scope });
 useEffect(() => { context.current = { api, scope }; }, [api, scope]);
 const mounted = useRef(true);
 const [page, setPage] = useState<AdminCustomerPage | null>(null);
 const [search, setSearch] = useState(''); const [filter, setFilter] = useState({ search: '', status: 'all' });
 const [after, setAfter] = useState<string | null>(null); const [revision, setRevision] = useState(0);
 const [error, setError] = useState(''); const [notice, setNotice] = useState(''); const [busy, setBusy] = useState(false);
 const [detail, setDetail] = useState<AdminCustomer | null>(null); const [history, setHistory] = useState<StatusHistory | null>(null);
 const [detailId, setDetailId] = useState<string | null>(null); const [before, setBefore] = useState<number | null>(null);
 const [draft, setDraft] = useState<AdminCustomer | null>(null); const [reason, setReason] = useState(''); const [confirmed, setConfirmed] = useState(false);
 const [pending, setPending] = useState(pendingCustomerChanges.get(scope) ?? null);
 const pendingRef = useRef(pending);
 const same = () => mounted.current && context.current.api === api && context.current.scope === scope;
 useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
 useEffect(() => {
  const controller = new AbortController(); const params = new URLSearchParams(); if (filter.search) params.set('search', filter.search); if (filter.status !== 'all') params.set('status', filter.status); if (after) params.set('after', after);
  void api.customerAdministration<AdminCustomerPage>(params.size ? '?' + params.toString() : '', 'GET', undefined, controller.signal).then(value => {
   if (controller.signal.aborted) return;
   if (value.tenantId !== tenantId || !Array.isArray(value.items) || value.items.length > 50 || value.items.some(c => !boundCustomer(c, tenantId)) || new Set(value.items.map(c => c.customerId)).size !== value.items.length || value.nextCursor !== null && (value.items.length !== 50 || value.nextCursor !== value.items.at(-1)?.customerId) || [value.total, value.active, value.inactive, value.matching].some(n => !Number.isSafeInteger(n) || n < 0) || value.active + value.inactive !== value.total || value.matching < value.items.length || value.matching > value.total) invalidCustomerResponse();
   setPage(value); setError('');
  }).catch((e: unknown) => { if (!controller.signal.aborted) { setPage(null); setError(displayError(e)); } });
  return () => { controller.abort(); };
 }, [api, tenantId, filter, after, revision]);
 useEffect(() => {
  if (!detailId) return; const controller = new AbortController(); const suffix = '/' + encodeURIComponent(detailId);
  void Promise.all([api.customerAdministration<AdminCustomer>(suffix, 'GET', undefined, controller.signal), api.customerAdministration<StatusHistory>(suffix + '/history' + (before ? '?beforeVersion=' + String(before) : ''), 'GET', undefined, controller.signal)]).then(([row, entries]) => {
   if (controller.signal.aborted) return;
   if (!boundCustomer(row, tenantId, detailId) || entries.tenantId !== tenantId || entries.customerId !== detailId || !Array.isArray(entries.items) || entries.items.length > 50 || entries.items.some(r => !boundReceipt(r, tenantId, detailId)) || new Set(entries.items.map(r => r.after.version)).size !== entries.items.length || entries.nextBeforeVersion !== null && (entries.items.length !== 50 || entries.nextBeforeVersion !== entries.items.at(-1)?.after.version)) invalidCustomerResponse();
   let last = before ?? Number.MAX_SAFE_INTEGER; for (const r of entries.items) { if (r.after.version >= last) invalidCustomerResponse(); last = r.after.version; }
   setDetail(row); setHistory(entries);
  }).catch((e: unknown) => { if (!controller.signal.aborted) { setDetail(null); setHistory(null); setError(displayError(e)); } });
  return () => { controller.abort(); };
 }, [api, tenantId, detailId, before, revision]);
 function changeDraft(row: AdminCustomer) { if (pendingRef.current || busy) return; setDraft(row); setReason(''); setConfirmed(false); setError(''); }
 async function save() {
  if (busy || (!pending && (!draft || !confirmed || !reason.trim()))) return;
  const operation = pending ?? (draft ? { customer: draft, change: { operationId: crypto.randomUUID().replaceAll('-', ''), expectedVersion: draft.version, active: !draft.active, reason: reason.trim() } satisfies StatusChange } : null);
  if (!operation) return;
  pendingCustomerChanges.set(scope, operation); pendingRef.current = operation; setPending(operation); setBusy(true); setError('');
  try {
   const suffix = '/' + encodeURIComponent(operation.customer.customerId);
   const receipt = await api.customerAdministration<StatusReceipt>(suffix + '/status', 'POST', operation.change);
   if (!same()) return;
   if (!boundReceipt(receipt, tenantId, operation.customer.customerId) || receipt.operationId !== operation.change.operationId || receipt.recordedBy !== actorId || receipt.before.version !== operation.change.expectedVersion || receipt.after.active !== operation.change.active || receipt.reason !== operation.change.reason) invalidCustomerResponse();
   const saved = await api.customerAdministration<StatusReceipt>(suffix + '/changes/' + operation.change.operationId);
   if (!same()) return;
   if (JSON.stringify(saved) !== JSON.stringify(receipt)) invalidCustomerResponse();
   pendingCustomerChanges.delete(scope); pendingRef.current = null; setPending(null); setDraft(null); setNotice(operation.change.active ? 'Customer reactivation confirmed.' : 'Customer suspension confirmed.'); setDetail(null); setHistory(null); setBefore(null); setAfter(null); setRevision(n => n + 1);
  } catch (e) {
   if (!same()) return;
   if (e instanceof AccessApiError && [400, 409].includes(e.status)) { pendingCustomerChanges.delete(scope); pendingRef.current = null; setPending(null); if (e.status === 409) { setDraft(null); setDetail(null); setHistory(null); setDetailId(null); setAfter(null); setRevision(n => n + 1); } }
   setError(e instanceof AccessApiError && e.status === 409 ? 'Customer status changed or the request conflicts. Refresh and review the current customer before another change.' : displayError(e));
  } finally { if (same()) setBusy(false); }
 }
 const activeDraft = pending?.customer ?? draft;
 return <section className="access-page" aria-labelledby="customers-title">
  <h1 id="customers-title">Customers</h1><p>Find customers in your organization, review their details and manage customer availability.</p>
  <p>Suspension stops customer workspace access. Customer data and history are retained. Customer ownership, professional assignments and sharing permissions are managed separately.</p>
  {error && <Alert type="error" showIcon title={error} />}{notice && <Alert type="success" showIcon title={notice} />}
  {pending && <Alert type="warning" showIcon title="Confirm the pending customer change" description={`The change for ${pending.customer.displayName} has not been confirmed. Retry the same request before starting another change.`} action={<Button disabled={busy} onClick={() => { setDraft(pending.customer); }}>Review pending change</Button>} />}
  {page && <p>{page.total} total customers · {page.active} active · {page.inactive} inactive · {page.matching} matching this search</p>}
  <Space wrap><Input.Search aria-label="Find customer" placeholder="Name, email or customer reference" maxLength={120} value={search} onChange={e => { setSearch(e.target.value); }} onSearch={() => { setPage(null); setAfter(null); setFilter(f => ({ ...f, search: search.trim() })); }} enterButton="Search" />
  <Select aria-label="Customer status filter" value={filter.status} options={[{ value: 'all', label: 'All customers' }, { value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} onChange={status => { setPage(null); setAfter(null); setFilter(f => ({ ...f, status })); }} />
  <Button onClick={() => { setPage(null); setAfter(null); setRevision(n => n + 1); }}>Refresh customers</Button></Space>
  <Table<AdminCustomer> rowKey="customerId" dataSource={page?.items ?? []} loading={!page && !error} pagination={false} locale={{ emptyText: <Empty description="No customers match this search" /> }} columns={[
   { title: 'Customer', key: 'customer', render: (_, row) => <><strong>{row.displayName}</strong><br />{row.email ?? row.secondaryLabel ?? row.customerId}</> },
   { title: 'Status', key: 'status', render: (_, row) => <Tag color={row.active ? 'green' : 'default'}>{row.active ? 'Active' : 'Inactive'}</Tag> },
   { title: 'Details', key: 'actions', render: (_, row) => <Button onClick={() => { setDetail(null); setHistory(null); setBefore(null); setDetailId(row.customerId); }}>View {row.displayName}</Button> },
  ]} />
  {after && <Button onClick={() => { setPage(null); setAfter(null); }}>First page</Button>}{page?.nextCursor && <Button onClick={() => { setAfter(page.nextCursor); setPage(null); }}>Next customers</Button>}
  <Drawer title="Customer details" open={detailId !== null} onClose={() => { setDetailId(null); setDetail(null); setHistory(null); }} size={680}>
   {detail && <><h2>{detail.displayName}</h2><Descriptions column={1} items={[{ key: 'email', label: 'Email', children: detail.email ?? 'Not linked' }, { key: 'reference', label: 'Customer reference', children: detail.customerId }, { key: 'label', label: 'Additional detail', children: detail.secondaryLabel ?? 'None' }, { key: 'status', label: 'Status', children: detail.active ? 'Active' : 'Inactive' }, { key: 'access', label: 'Customer access', children: detail.ownerLinked ? 'Owner link recorded' : 'No owner link recorded' }]} />
    {(detail.active || detail.activationAvailable) ? <Button disabled={!!pending || busy} danger={detail.active} onClick={() => { changeDraft(detail); }}>{detail.active ? 'Suspend customer' : 'Reactivate customer'}</Button> : <p>This customer cannot be reactivated here.</p>}
    <Button href={'/access/documents?customerId=' + encodeURIComponent(detail.customerId)}>View documents</Button><Button href={'/access/processing?customerId=' + encodeURIComponent(detail.customerId)}>View processing status</Button>
    <h3>Status history</h3>{history?.items.length === 0 && <p>No administrator status changes have been recorded.</p>}
    {history?.items.map(r => <article key={r.operationId}><h4>{r.after.active ? 'Reactivated' : 'Suspended'} · {new Date(r.recordedAt).toLocaleString()}</h4><p>{r.reason}</p><p>Changed by {r.recordedByLabel} · Previous status: {r.before.active ? 'Active' : 'Inactive'} · Change reference: {r.operationId}</p></article>)}
    {before && <Button onClick={() => { setHistory(null); setBefore(null); }}>Latest status changes</Button>}{history?.nextBeforeVersion && <Button onClick={() => { setHistory(null); setBefore(history.nextBeforeVersion); }}>Earlier status changes</Button>}
   </>}
  </Drawer>
  <Modal title={activeDraft?.active ? 'Suspend customer' : 'Reactivate customer'} open={draft !== null} onCancel={() => { if (!busy) setDraft(null); }} footer={<Space><Button disabled={busy} onClick={() => { setDraft(null); }}>Close review</Button><Button type="primary" danger={!!activeDraft?.active} loading={busy} disabled={!pending && (!confirmed || !reason.trim())} onClick={() => { void save(); }}>{pending ? 'Retry confirmation' : activeDraft?.active ? 'Confirm suspension' : 'Confirm reactivation'}</Button></Space>}>
   {activeDraft && <><h3>{activeDraft.displayName}</h3><p>{activeDraft.email ?? activeDraft.customerId}</p><p>{activeDraft.active ? 'This stops customer workspace access. Financial records and history are retained.' : 'This restores customer availability. Current identity, role and sharing checks still apply. Sharing may require renewed customer approval.'}</p>
   {pending ? <p>Reason: {pending.change.reason}</p> : <><label htmlFor="customer-status-reason">Reason for the change</label><Input.TextArea id="customer-status-reason" maxLength={500} value={reason} onChange={e => { setReason(e.target.value); }} /><Checkbox checked={confirmed} onChange={e => { setConfirmed(e.target.checked); }}>I have reviewed this customer and the effect of this change.</Checkbox></>}</>}
  </Modal>
 </section>;
}
