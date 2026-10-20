import { Alert, Button, Descriptions, Drawer, Empty, Input, Select, Space, Table, Tag } from 'antd';
import { useEffect, useState } from 'react';
import { AccessApiError, displayError } from './accessApi';
import type { AccessApi } from './accessApi';
import { validLabels } from './processingAdministrationApi';
import type { CustomerLabel, CustomerLabels } from './processingAdministrationApi';
export interface ScanEvidence { contentSha256: string; engineVersion: string; databaseVersion: string; databaseDate: string; checkedAt: string }
export interface MonitoredDocument { tenantId: string; documentId: string; documentVersionId: string; customerId: string; version: number; fileName: string; contentType: string; contentLength: number; receivedAt: string; registeredAt: string; scanStatus: string; scanEvidence: ScanEvidence | null }
export interface DocumentMonitorPage { tenantId: string; items: MonitoredDocument[]; nextCursor: string | null; matching: number; total: number; verifiedAtIntake: number; unrecorded: number }
export interface ClassificationItem { processingId: string; state: string; failureCode: string | null; retryable: boolean; attempt: number; updatedAt: string; detectedCategories: string[] }
export interface ClassificationPage { tenantId: string; customerId: string; documentVersionId: string; items: ClassificationItem[]; nextCursor: string | null }
const id = (x: string) => /^[A-Za-z0-9][A-Za-z0-9._:-]{0,159}$/.test(x);
const date = (x: string) => Number.isFinite(Date.parse(x)) && Date.parse(x) >= 0;
const count = (x: number) => Number.isSafeInteger(x) && x >= 0;
const text = (x: string) => x.trim().length > 0 && x.length <= 255 && !/[\p{Cc}]/u.test(x);
const categories = ['INCOME', 'EXPENSE', 'LOAN', 'INVESTMENT', 'INSURANCE', 'PROPERTY', 'VEHICLE', 'TAX', 'ASSET', 'LIABILITY'];
function validDocument(row: MonitoredDocument, tenant: string) {
 return row.tenantId === tenant && id(row.documentId) && id(row.documentVersionId) && id(row.customerId) && count(row.version) && row.version > 0 && text(row.fileName) && text(row.contentType) && count(row.contentLength) && date(row.receivedAt) && date(row.registeredAt) && (row.scanStatus === 'UNRECORDED' ? row.scanEvidence === null : row.scanStatus === 'VERIFIED_AT_INTAKE' && row.scanEvidence !== null && /^[0-9a-f]{64}$/.test(row.scanEvidence.contentSha256) && row.scanEvidence.engineVersion === '1.4.6' && /^[0-9]{1,12}$/.test(row.scanEvidence.databaseVersion) && date(row.scanEvidence.checkedAt) && date(row.scanEvidence.databaseDate) && Date.parse(row.scanEvidence.checkedAt) <= Date.parse(row.registeredAt) + 60_000 && Date.parse(row.scanEvidence.checkedAt) >= Date.parse(row.registeredAt) - 300_000 && Date.parse(row.scanEvidence.databaseDate) <= Date.parse(row.scanEvidence.checkedAt) + 300_000 && Date.parse(row.scanEvidence.databaseDate) >= Date.parse(row.scanEvidence.checkedAt) - 259_200_000);
}
function validPage(page: DocumentMonitorPage, tenant: string) {
 return page.tenantId === tenant && Array.isArray(page.items) && page.items.length <= 50 && page.items.every(row => validDocument(row, tenant)) && new Set(page.items.map(row => row.documentVersionId)).size === page.items.length && (page.nextCursor === null || page.items.length === 50 && page.nextCursor === page.items.at(-1)?.documentVersionId) && [page.matching, page.total, page.verifiedAtIntake, page.unrecorded].every(count) && page.matching >= page.items.length && page.matching <= page.total && page.total === page.verifiedAtIntake + page.unrecorded;
}
function validClassifications(page: ClassificationPage, tenant: string, doc: MonitoredDocument) {
 return page.tenantId === tenant && page.customerId === doc.customerId && page.documentVersionId === doc.documentVersionId && Array.isArray(page.items) && page.items.length <= 50 && new Set(page.items.map(x => x.processingId)).size === page.items.length && (page.nextCursor === null || page.items.length === 50 && page.nextCursor === page.items.at(-1)?.processingId) && page.items.every(x => id(x.processingId) && ['Accepted', 'Running', 'Completed', 'Failed'].includes(x.state) && (x.failureCode === null || id(x.failureCode)) && typeof x.retryable === 'boolean' && count(x.attempt) && date(x.updatedAt) && Array.isArray(x.detectedCategories) && x.detectedCategories.every(c => categories.includes(c)));
}
function invalid(): never { throw new AccessApiError(503, 'DOCUMENT_MONITOR_RESULT_INVALID'); }
export function DocumentMonitoring(props: { api: AccessApi; tenantId: string; actorId: string }) { return <DocumentMonitoringView key={props.tenantId + ':' + props.actorId} {...props} />; }
function DocumentMonitoringView({ api, tenantId }: { api: AccessApi; tenantId: string; actorId: string }) {
 const initial = new URLSearchParams(window.location.search).get('customerId');
 const [customerId, setCustomerId] = useState(initial); const [search, setSearch] = useState(''); const [query, setQuery] = useState({ search: '', scan: 'all', documentId: '' }); const [after, setAfter] = useState<string | null>(null);
 const [page, setPage] = useState<DocumentMonitorPage | null>(null); const [names, setNames] = useState<Record<string, CustomerLabel>>({}); const [error, setError] = useState(''); const [refresh, setRefresh] = useState(0); const [checked, setChecked] = useState<string | null>(null);
 const [selected, setSelected] = useState<MonitoredDocument | null>(null); const [detail, setDetail] = useState<MonitoredDocument | null>(null); const [activity, setActivity] = useState<ClassificationPage | null>(null); const [classificationAfter, setClassificationAfter] = useState<string | null>(null); const [activityError, setActivityError] = useState('');
 useEffect(() => {
  if (customerId !== null && !id(customerId)) return;
  const controller = new AbortController();
  const params = new URLSearchParams(); if (query.search) params.set('search', query.search); if (query.scan !== 'all') params.set('scan', query.scan); if (customerId) params.set('customerId', customerId); if (after) params.set('after', after); if (query.documentId) params.set('documentId', query.documentId);
  void (async () => {
   const value = await api.documentMonitoring<DocumentMonitorPage>(params.size ? '?' + params.toString() : '', controller.signal); controller.signal.throwIfAborted();
   if (!validPage(value, tenantId) || value.items.some(row => customerId !== null && row.customerId !== customerId || query.scan !== 'all' && row.scanStatus !== query.scan || query.documentId !== '' && row.documentId !== query.documentId)) invalid();
   const ids = [...new Set([...value.items.map(row => row.customerId), ...(customerId ? [customerId] : [])])];
   const labels = ids.length ? await api.customerAdministration<CustomerLabels>('/labels', 'POST', { customerIds: ids }, controller.signal) : { tenantId, items: [], unavailable: [] }; controller.signal.throwIfAborted(); if (!validLabels(labels, tenantId, ids)) invalid();
   setNames(Object.fromEntries(labels.items.map(x => [x.customerId, x]))); setPage(value); setChecked(new Date().toISOString());
  })().catch((e: unknown) => { if (!controller.signal.aborted) { setPage(null); setError(displayError(e)); } });
  return () => { controller.abort(); };
 }, [api, tenantId, customerId, query, after, refresh]);
 useEffect(() => {
  if (!selected) return; const controller = new AbortController();
  void api.documentMonitoring<MonitoredDocument>('/' + encodeURIComponent(selected.documentVersionId), controller.signal).then(row => {
   controller.signal.throwIfAborted(); if (!validDocument(row, tenantId) || row.documentVersionId !== selected.documentVersionId || row.documentId !== selected.documentId || row.customerId !== selected.customerId || row.version !== selected.version) invalid(); setDetail(row);
  }).catch((e: unknown) => { if (!controller.signal.aborted) setError(displayError(e)); });
  const params = new URLSearchParams({ customerId: selected.customerId, versionId: selected.documentVersionId }); if (classificationAfter) params.set('after', classificationAfter);
  void api.classificationMonitoring<ClassificationPage>('?' + params.toString(), controller.signal).then(value => { controller.signal.throwIfAborted(); if (!validClassifications(value, tenantId, selected)) invalid(); setActivity(value); }).catch((e: unknown) => { if (!controller.signal.aborted) setActivityError(displayError(e)); });
  return () => { controller.abort(); };
 }, [api, tenantId, selected, classificationAfter, refresh]);
 const when = (x: string) => new Date(x).toLocaleString(); const name = (x: string) => names[x]?.displayName ?? 'Customer unavailable';
 function first() { setPage(null); setNames({}); setDetail(null); setActivity(null); setError(''); setActivityError(''); setAfter(null); setRefresh(n => n + 1); }
 function open(row: MonitoredDocument) { setDetail(null); setActivity(null); setActivityError(''); setError(''); setSelected(row); setClassificationAfter(null); }
 if (customerId !== null && !id(customerId)) return <Alert type="error" title="Invalid customer selection" action={<Button onClick={() => { setCustomerId(null); }}>All customers</Button>} />;
 return <section className="access-page" aria-labelledby="document-monitoring-title">
  <h1 id="document-monitoring-title">Document Monitoring</h1><p>Inspect registered document versions, historical scan evidence and detected categories. This view contains operational metadata.</p>
  <p>Blocked uploads never become registered documents. Missing scan evidence is unverified. Legacy versions without recorded organization provenance are excluded. Refresh to check current owner status.</p>
  {error && <Alert type="error" title={error} />}
  {customerId && <Tag>Customer: {name(customerId)}</Tag>}{query.documentId && <Tag>Selected document reference: {query.documentId}</Tag>}
  <Space wrap><Input.Search aria-label="Find document" placeholder="File name or document reference" maxLength={128} value={search} onChange={e => { setSearch(e.target.value); }} onSearch={() => { setPage(null); setAfter(null); setQuery(q => ({ ...q, search: search.trim() })); }} enterButton="Search documents" />
   <Select aria-label="Scan evidence filter" value={query.scan} options={[{ value: 'all', label: 'All scan evidence' }, { value: 'VERIFIED_AT_INTAKE', label: 'Verified at intake' }, { value: 'UNRECORDED', label: 'No recorded scan evidence' }]} onChange={scan => { setPage(null); setAfter(null); setQuery(q => ({ ...q, scan })); }} />
   <Button onClick={first}>Refresh documents</Button><Button onClick={() => { setPage(null); setCustomerId(null); setQuery({ search: '', scan: 'all', documentId: '' }); setSearch(''); setAfter(null); window.history.replaceState(null, '', '/access/documents'); }}>Clear filters</Button><Button href="/access/customers">Find a customer</Button>
  </Space>
  {page && <p>{page.total} registered versions · {page.verifiedAtIntake} verified at intake · {page.unrecorded} without recorded scan evidence · {page.matching} matching. Last checked: {checked ? when(checked) : 'Unavailable'}</p>}
  <Table<MonitoredDocument> rowKey="documentVersionId" dataSource={page?.items ?? []} loading={!page && !error} pagination={false} locale={{ emptyText: <Empty description="No registered documents match this view" /> }} columns={[
   { title: 'Document', key: 'document', render: (_, row) => <><strong>{row.fileName}</strong><p>Version {row.version} · {row.contentType}</p><small>Document reference: {row.documentId}</small></> },
   { title: 'Customer', key: 'customer', render: (_, row) => <><strong>{name(row.customerId)}</strong><p>{names[row.customerId]?.secondaryLabel ?? row.customerId}</p></> },
   { title: 'Scan evidence', key: 'scan', render: (_, row) => <Tag color={row.scanStatus === 'VERIFIED_AT_INTAKE' ? 'green' : 'orange'}>{row.scanStatus === 'VERIFIED_AT_INTAKE' ? 'Verified at intake' : 'No recorded scan evidence'}</Tag> },
   { title: 'Registered', key: 'time', render: (_, row) => when(row.registeredAt) },
   { title: 'Actions', key: 'actions', render: (_, row) => <Button aria-label={'Inspect document ' + row.documentVersionId} onClick={() => { open(row); }}>Inspect</Button> },
  ]} />
  <Space><Button disabled={!after} onClick={first}>First document page</Button><Button disabled={!page?.nextCursor} onClick={() => { setPage(null); setAfter(page?.nextCursor ?? null); }}>Next documents</Button></Space>
  <Drawer title="Document status" open={selected !== null} onClose={() => { setSelected(null); setDetail(null); setActivity(null); }} size={680}>
   {detail && <><h2>{detail.fileName}</h2><p>{name(detail.customerId)}</p><Descriptions column={1} items={[
    { key: 'reference', label: 'Version reference', children: detail.documentVersionId }, { key: 'version', label: 'Document version', children: detail.version }, { key: 'size', label: 'File size', children: String(detail.contentLength) + ' bytes' }, { key: 'received', label: 'Received', children: when(detail.receivedAt) }, { key: 'registered', label: 'Registered', children: when(detail.registeredAt) },
   ]} />
    <h3>Historical scan evidence</h3>{detail.scanEvidence ? <p>Verified at intake on {when(detail.scanEvidence.checkedAt)} · Engine {detail.scanEvidence.engineVersion} · Signature database {detail.scanEvidence.databaseVersion}, dated {when(detail.scanEvidence.databaseDate)}. This is recorded intake evidence, not a new scan.</p> : <Alert type="warning" title="No recorded scan evidence" description="This document is unverified by this monitor. A new customer upload may be required. Administrators cannot bypass scanning." />}
    <Space><Button onClick={() => { setSelected(null); setAfter(null); setQuery(q => ({ ...q, documentId: detail.documentId })); }}>All versions of this document</Button><Button href={'/access/processing?customerId=' + encodeURIComponent(detail.customerId) + '&documentVersionId=' + encodeURIComponent(detail.documentVersionId)}>Review processing and permitted recovery</Button></Space>
   </>}
   {detail && <><h3>Classification and processing records</h3><p>Detected categories describe the extraction result. They do not verify or adopt financial information. Processing recovery requires the existing reviewed permission checks.</p>
   {activityError && <Alert type="error" title="Classification status unavailable" description={activityError} />}
   {activity?.items.length === 0 && <p>No processing record is recorded for this version. Use the customer's processing request flow; no successful processing is inferred.</p>}
   {activity?.items.map(row => <article key={row.processingId}><h4>{row.state === 'Running' ? 'Processing' : row.state} · {when(row.updatedAt)}</h4><p>Detected categories: {row.detectedCategories.length ? row.detectedCategories.join(', ') : 'None recorded'}</p><p>Attempt {row.attempt} · Processing reference: {row.processingId}</p>{row.failureCode && <p>Failure: {row.failureCode}. {row.retryable ? 'Review permitted recovery in Processing Status.' : 'A new customer request or owner review may be required.'}</p>}</article>)}
   <Space><Button disabled={!classificationAfter} onClick={() => { setActivity(null); setActivityError(''); setClassificationAfter(null); }}>First processing records</Button><Button disabled={!activity?.nextCursor} onClick={() => { setActivity(null); setActivityError(''); setClassificationAfter(activity?.nextCursor ?? null); }}>More processing records</Button></Space>
   </>}
  </Drawer>
 </section>;
}
