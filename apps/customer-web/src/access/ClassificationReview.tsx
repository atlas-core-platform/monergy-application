import { Alert, Button, Checkbox, Drawer, Empty, Select, Space, Table, Tag } from 'antd';
import { useEffect, useState } from 'react';
import { AccessApiError, displayError } from './accessApi';
import type { AccessApi } from './accessApi';
const classes = ['ITR', 'FORM_16', 'BANK_STATEMENT', 'SALARY_SLIP', 'CREDIT_REPORT', 'LOAN', 'INSURANCE', 'INVESTMENT', 'PROPERTY', 'VEHICLE', 'GST', 'PAN', 'AADHAAR', 'OTHER', 'UNCLASSIFIED'];
const reasons = ['SOURCE_REVIEWED', 'CUSTOMER_CONFIRMED', 'OTHER_DOCUMENT'];
const label = (value: string) => value.replaceAll('_', ' ');
const id = (value: string) => /^[A-Za-z0-9][A-Za-z0-9._:-]{0,159}$/.test(value);
export interface ClassificationRecord { tenantId: string; customerId: string; documentVersionId: string; sourceHash: string; classifierVersion: string; automaticClass: string; documentClass: string; reason: string; classifiedAt: string; revision: number; needsReview: boolean; reviews: { revision: number; actorId: string; documentClass: string; reason: string; reviewedAt: string }[] }
export interface ReviewPage { tenantId: string; items: ClassificationRecord[]; nextCursor: string | null; canReview: boolean }
function valid(row: ClassificationRecord, tenant: string) {
 return row.tenantId === tenant && id(row.customerId) && id(row.documentVersionId) && /^[a-f0-9]{64}$/.test(row.sourceHash) && row.classifierVersion === 'document-classification/1.0.0' && classes.includes(row.automaticClass) && classes.includes(row.documentClass) && ['NO_TEXT', 'TEXT_LIMIT', 'AMBIGUOUS_TITLES', 'NO_SUPPORTED_TITLE', 'LOW_CONFIDENCE', 'SUPPORTED_TITLE', 'UNSUPPORTED_LAYOUT'].includes(row.reason) && Number.isFinite(Date.parse(row.classifiedAt)) && Array.isArray(row.reviews) && row.reviews.length <= 100 && row.revision === row.reviews.length + 1 && row.reviews.every((x, i) => x.revision === i + 2 && id(x.actorId) && classes.includes(x.documentClass) && x.documentClass !== 'UNCLASSIFIED' && reasons.includes(x.reason) && Number.isFinite(Date.parse(x.reviewedAt))) && row.documentClass === (row.reviews.at(-1)?.documentClass ?? row.automaticClass) && row.needsReview === (row.reviews.length === 0 && row.automaticClass === 'UNCLASSIFIED');
}
const invalid = () => new AccessApiError(503, 'CLASSIFICATION_RESULT_INVALID');
export function ClassificationReview({ api, tenantId }: { api: AccessApi; tenantId: string }) {
 const [status, setStatus] = useState('unknown'); const [after, setAfter] = useState<string | null>(null); const [page, setPage] = useState<ReviewPage | null>(null); const [error, setError] = useState(''); const [refresh, setRefresh] = useState(0);
 const [selected, setSelected] = useState<ClassificationRecord | null>(null); const [documentClass, setDocumentClass] = useState('OTHER'); const [reason, setReason] = useState('SOURCE_REVIEWED'); const [confirmed, setConfirmed] = useState(false); const [saving, setSaving] = useState(false); const [reviewError, setReviewError] = useState('');
 useEffect(() => {
  const controller = new AbortController(); const query = new URLSearchParams({ status }); if (after) query.set('after', after);
  void api.classificationReviews<ReviewPage>('?' + query.toString(), 'GET', undefined, controller.signal).then(value => {
   controller.signal.throwIfAborted();
   if (value.tenantId !== tenantId || !Array.isArray(value.items) || value.items.length > 50 || typeof value.canReview !== 'boolean' || !value.items.every(x => valid(x, tenantId) && (status !== 'unknown' || x.needsReview)) || new Set(value.items.map(x => x.customerId + ':' + x.documentVersionId)).size !== value.items.length || value.nextCursor !== null && (value.items.length !== 50 || !/^[a-f0-9]{64}$/.test(value.nextCursor))) throw invalid();
   setPage(value);
  }).catch((e: unknown) => { if (!controller.signal.aborted) setError(displayError(e)); });
  return () => { controller.abort(); };
 }, [api, tenantId, status, after, refresh]);
 function reload() { setPage(null); setError(''); setAfter(null); setRefresh(n => n + 1); }
 async function save() {
  if (!selected || !confirmed || !page?.canReview || saving) return; setSaving(true); setReviewError('');
  try {
   const result = await api.classificationReviews<ClassificationRecord>('/' + encodeURIComponent(selected.customerId) + '/' + encodeURIComponent(selected.documentVersionId), 'POST', { expectedRevision: selected.revision, documentClass, reason });
   if (!valid(result, tenantId) || result.customerId !== selected.customerId || result.documentVersionId !== selected.documentVersionId || result.sourceHash !== selected.sourceHash || result.revision !== selected.revision + 1 || result.documentClass !== documentClass) throw invalid();
   setSelected(result); setConfirmed(false); reload();
  } catch (e: unknown) { setReviewError(e instanceof AccessApiError && e.status === 409 ? 'This classification changed. Close this review and refresh before submitting again.' : displayError(e)); setConfirmed(false); }
  finally { setSaving(false); }
 }
 return <section className="access-page" aria-labelledby="classification-title">
  <h1 id="classification-title">Document classification review</h1><p>Review document types independently of financial extraction. A classification does not verify identity, ownership or financial information.</p>
  <Button href="/access/documents">Back to Document Monitoring</Button>
  {error && <Alert type="error" title={error} />}
  {page && !page.canReview && <Alert type="info" title="Review permission is not configured for your administrator account." />}
  <Space><Select aria-label="Classification filter" value={status} options={[{ value: 'unknown', label: 'Needs review' }, { value: 'all', label: 'All classifications' }]} onChange={value => { setStatus(value); setAfter(null); setPage(null); setError(''); }} /><Button onClick={reload}>Refresh classifications</Button></Space>
  <Table<ClassificationRecord> rowKey={row => row.customerId + ':' + row.documentVersionId} dataSource={page?.items ?? []} loading={!page && !error} pagination={false} locale={{ emptyText: <Empty description="No classifications match this view" /> }} columns={[
   { title: 'Document version', dataIndex: 'documentVersionId' }, { title: 'Customer reference', key: 'customer', render: (_, row) => <a href={'/access/documents?customerId=' + encodeURIComponent(row.customerId)}>{row.customerId}</a> },
   { title: 'Document type', key: 'class', render: (_, row) => <><strong>{label(row.documentClass)}</strong>{row.needsReview && <Tag color="orange">Needs review</Tag>}<p>{row.reviews.length ? 'Administrator reviewed' : 'Automatic classification'}</p></> },
   { title: 'Actions', key: 'actions', render: (_, row) => <Button aria-label={'Review classification ' + row.documentVersionId} onClick={() => { setSelected(row); setDocumentClass(row.documentClass === 'UNCLASSIFIED' ? 'OTHER' : row.documentClass); setReason('SOURCE_REVIEWED'); setConfirmed(false); setReviewError(''); }}>Review</Button> },
  ]} />
  <Space><Button disabled={!after} onClick={reload}>First classification page</Button><Button disabled={!page?.nextCursor} onClick={() => { setAfter(page?.nextCursor ?? null); setPage(null); setError(''); }}>Next classifications</Button></Space>
  <Drawer title="Review document type" open={selected !== null} onClose={() => { if (!saving) setSelected(null); }} size={640}>
   {selected && <><p>Version: {selected.documentVersionId} · Customer: {selected.customerId}</p><p>Automatic result: {label(selected.automaticClass)} · {label(selected.reason)}</p><p>Revision {selected.revision} · Source hash: {selected.sourceHash}</p>
    <p>This view contains metadata. Use an independently authorized source review or customer confirmation before changing the type.</p>
    <Button href={'/access/processing?customerId=' + encodeURIComponent(selected.customerId) + '&documentVersionId=' + encodeURIComponent(selected.documentVersionId)}>View processing for this version</Button>
    <h2>Review history</h2>{selected.reviews.length === 0 ? <p>No review recorded.</p> : selected.reviews.map(x => <p key={x.revision}>Revision {x.revision}: {label(x.documentClass)} · {label(x.reason)} · {x.actorId} · {new Date(x.reviewedAt).toLocaleString()}</p>)}
    {reviewError && <Alert type="error" title={reviewError} />}
    <Space orientation="vertical"><Select aria-label="Reviewed document type" value={documentClass} options={classes.filter(x => x !== 'UNCLASSIFIED').map(value => ({ value, label: label(value) }))} onChange={setDocumentClass} disabled={!page?.canReview || saving} />
     <Select aria-label="Review reason" value={reason} options={reasons.map(value => ({ value, label: label(value) }))} onChange={setReason} disabled={!page?.canReview || saving} />
     <Checkbox checked={confirmed} disabled={!page?.canReview || saving} onChange={e => { setConfirmed(e.target.checked); }}>I confirm the document type for this exact version.</Checkbox>
     <Button type="primary" disabled={!page?.canReview || !confirmed} loading={saving} onClick={() => { void save(); }}>Save classification review</Button>
    </Space>
   </>}
  </Drawer>
 </section>;
}
