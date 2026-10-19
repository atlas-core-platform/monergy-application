import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createLiabilityRecord, getLiabilityRecords } from './productApi';
import type { ProductFinancialFact, ProductLiabilityDraft, ProductLiabilityRecord, ProductLiabilityRecordList } from './productApi';

const numberFields = [ ['outstandingAmount', 'Outstanding balance'] ] as const;
const dateFields = [ ['balanceAsOfDate', 'Balance as of'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; liability: ProductLiabilityDraft }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);

export default function LiabilityRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const scope = useRef({ api, canRead, canCreate });
  useLayoutEffect(() => { scope.current = { api, canRead, canCreate }; }, [api, canRead, canCreate]);
  const loadSequence = useRef(0);
  const activeLoad = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductLiabilityRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<{ api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null } | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<ProductLiabilityRecord | null>(null);
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductLiabilityRecord } | null>(null);
  const load = useCallback(async (signal?: AbortSignal) => {
    if (!canRead) return;
    const sequence = ++loadSequence.current;
    activeLoad.current?.abort();
    const controller = new AbortController(); activeLoad.current = controller;
    const abort = () => { controller.abort(); };
    signal?.addEventListener('abort', abort, { once: true });
    const active = () => !signal?.aborted && !controller.signal.aborted && sequence === loadSequence.current && scope.current.api === api && scope.current.canRead;
    setLoading(true); setFailure(null);
    try {
      const list = await getLiabilityRecords(api, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean' || list.items.some(item=>item.verificationStatus !== 'CUSTOMER_REVIEWED')) throw new Error('Invalid record list');
      if (active()) setLoaded({ api, list });
    } catch (error) {
      if (active()) { setLoaded(null); setFailure({ api, text: error instanceof AccessApiError && error.status === 403
        ? 'Your current access does not include liability records and their source documents.' : 'Liability records are unavailable. Try again.' }); }
    } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, canRead]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => { void load(controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); activeLoad.current?.abort(); };
  }, [load]);
  const current = editor?.api === api ? editor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'LOAN');
  const valid = current && source && current.values.name?.trim() && current.values.creditor?.trim() &&
    numberFields.every(([key]) => current.values[key] !== undefined && current.values[key] !== '' && Number.isFinite(Number(current.values[key]))) &&
    dateFields.every(([key]) => current.values[key]);
  const open = () => {
    setSaveError(''); setSaved(null);
    if (current?.submission) setEditor({ ...current, open: true });
    else setEditor({ api, open: true, values: {}, operationId: crypto.randomUUID().replaceAll('-', ''), submission: null });
  };
  const change = (key: string, value: string) => {
    if (current && !current.submission && !saving) setEditor({ ...current, values: { ...current.values, [key]: value } });
  };
  const save = async () => {
    if (!current || !canRead || !canCreate || saving || (!current.submission && !valid)) return;
    const submission = current.submission ?? { operationId: current.operationId, liability: {
      name: current.values.name ?? '', creditor: current.values.creditor ?? '',
      outstandingAmount: Number(current.values.outstandingAmount),
      balanceAsOfDate:current.values.balanceAsOfDate ?? '',
      sourceFinancialFactId: source?.financialFactId ?? '', sourceRevision: source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = await createLiabilityRecord(api, submission);
      if (record.operationId !== submission.operationId || record.liability.sourceFinancialFactId !== submission.liability.sourceFinancialFactId ||
          record.liability.sourceRevision !== submission.liability.sourceRevision || !record.liabilityRecordId || record.verificationStatus !== 'CUSTOMER_REVIEWED') throw new Error('Invalid confirmation');
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaved(record); setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, open: false, submission: null } : active); await load();
    } catch (error) {
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaveError(error instanceof AccessApiError && error.status === 400
        ? 'Check the balance, as-of date and required liability details. The balance date must be today or earlier. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (error instanceof AccessApiError && error.status === 400) setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, submission: null } : active);
    } finally { setSaving(false); }
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view liability records and their sources." />;
  return <section aria-label="Liability records" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Liability records</h2>
      {canCreate && <Button onClick={open} disabled={saving || (!current?.submission && facts.every((fact) => fact.factType !== 'LOAN'))}>
        {current?.submission ? 'Continue save confirmation' : 'Add liability record'}</Button>}
    </Space>
    <p>Enter a liability balance, creditor and as-of date using a reviewed loan/debt source. Source figures stay separate. These records do not establish total debt, reconcile duplicate loans or verify repayment.</p>
    {saved && editor?.api === api && <Alert type="success" title={'Liability record saved: ' + saved.liability.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry liability records</Button>} />}
    {loading && <p role="status">Loading liability records…</p>}
    {list?.items.length === 0 && <p>No liability records added yet. Review a loan/debt source figure in Documents, then enter its creditor, balance and as-of date here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added liability records." />}
    {list?.items.map((record) => <Card key={record.liabilityRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.liability.name}</strong><Tag>Customer reviewed</Tag><span>{record.liability.creditor}</span></Space>
      <p>Outstanding balance: {money(record.liability.outstandingAmount, record.currency)} · as of {record.liability.balanceAsOfDate}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View liability record: {record.liability.name}</Button>
    </Card>)}
    <Drawer title="Add liability record" open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      <p>Choose an exact reviewed loan/debt source, then enter the liability balance, creditor and as-of date. The source amount does not fill the form.</p>
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission)} style={{ border: 0, padding: 0 }}>
        <label htmlFor="liability-source">Reviewed debt source</label>
        <Select id="liability-source" aria-label="Reviewed debt source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed debt source"
          options={facts.filter((fact) => fact.factType === 'LOAN').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate + ' · ' + fact.source.sourceName }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter the outstanding balance separately.'} />}
        {(['name', 'creditor'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'liability-' + key}>{key === 'name' ? 'Liability name' : 'Creditor'}</label>
          <Input id={'liability-' + key} maxLength={120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'liability-' + key}>{label}</label>
          <Input id={'liability-' + key} type="number" min={0}
            max={999999999999.99}
            step={0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'liability-' + key}>{label}</label>
          <Input id={'liability-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
      </fieldset>
      <p>Saving retains your review and the exact source revision. It does not verify debt or repayment with the creditor. Loan and liability records may refer to the same debt and are not reconciled or added together.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : 'Save reviewed liability record'}</Button>
    </Drawer>
    <Drawer title="Liability record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.liability.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Creditor</dt><dd>{visible.liability.creditor}</dd>
        <dt>Outstanding balance</dt><dd>{money(visible.liability.outstandingAmount,visible.currency)}</dd><dt>Currency</dt><dd>{visible.currency}</dd>
        <dt>Balance as of</dt><dd>{visible.liability.balanceAsOfDate}</dd>
        <dt>Review status</dt><dd>Customer reviewed</dd>
        <dt>Source revision</dt><dd>{visible.liability.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.liability.sourceFinancialFactId)}>View exact liability source history</a>
      <p>The creditor, balance and as-of date were entered and reviewed by the customer. They have not been independently verified with the creditor.</p></>}
    </Drawer>
  </section>;
}
