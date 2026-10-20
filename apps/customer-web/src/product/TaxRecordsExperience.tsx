import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createTaxRecord, getTaxRecords, reviseTaxRecord, getTaxRecordHistory } from './productApi';
import type { ProductFinancialFact, ProductTaxDraft, ProductTaxRecord, ProductTaxRecordList, ProductTaxRecordHistory } from './productApi';

const numberFields = [ ['amount', 'Tax amount for this record'] ] as const;
const amountKinds = [{value:'PAID',label:'Tax paid'},{value:'DUE',label:'Tax due'},{value:'WITHHELD',label:'Tax withheld'}];
const dateFields = [ ['periodStart', 'Period start'], ['periodEnd', 'Period end'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; tax: ProductTaxDraft; expectedRevision?: number }
interface Editor { api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null; original: ProductTaxRecord | null; conflict?: boolean }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);
const amountKindName = (value: string) => amountKinds.find(option => option.value === value)?.label ?? value;

export default function TaxRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const scope = useRef({ api, canRead, canCreate });
  useLayoutEffect(() => { scope.current = { api, canRead, canCreate }; }, [api, canRead, canCreate]);
  const loadSequence = useRef(0);
  const activeLoad = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductTaxRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<ProductTaxRecord | null>(null);
  const [history, setHistory] = useState<{ api: AccessApi; record: ProductTaxRecord } | null>(null);
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductTaxRecord } | null>(null);
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
      const list = await getTaxRecords(api, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean' || list.items.some(item=>item.verificationStatus !== 'CUSTOMER_REVIEWED')) throw new Error('Invalid record list');
      if (active()) setLoaded({ api, list });
    } catch (error) {
      if (active()) { setLoaded(null); setFailure({ api, text: error instanceof AccessApiError && error.status === 403
        ? 'Your current access does not include tax records and their source documents.' : 'Tax records are unavailable. Try again.' }); }
    } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, canRead]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => { void load(controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); activeLoad.current?.abort(); };
  }, [load]);
  const current = editor?.api === api ? editor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'TAX');
  const valid = current && (current.original ?? source) && !current.conflict && current.values.name?.trim() && current.values.jurisdiction?.trim() && current.values.taxType?.trim() && current.values.taxYearLabel?.trim() && current.values.amountKind &&
    numberFields.every(([key]) => current.values[key] !== undefined && current.values[key] !== '' && Number.isFinite(Number(current.values[key]))) &&
    dateFields.every(([key]) => current.values[key]);
  const open = () => {
    setSaveError(''); setSaved(null);
    if (current?.submission) setEditor({ ...current, open: true });
    else setEditor({ api, open: true, values: {}, operationId: crypto.randomUUID().replaceAll('-', ''), submission: null, original: null });
  };
  const edit = (record: ProductTaxRecord) => {
    if(current?.submission || saving) { if(current) setEditor({...current,open:true}); return; }
    setSelected(null);setSaved(null);setSaveError('');
    setEditor({api,open:true,original:record,values:Object.fromEntries(Object.entries(record.tax).map(([key,value])=>[key,String(value)])),
      operationId:crypto.randomUUID().replaceAll('-',''),submission:null});
  };
  const change = (key: string, value: string) => {
    if (current && !current.submission && !current.conflict && !saving) setEditor({ ...current, values: { ...current.values, [key]: value } });
  };
  const save = async () => {
    if (!current || !canRead || !canCreate || saving || (!current.submission && !valid)) return;
    const submission = current.submission ?? { operationId: current.operationId, ...(current.original ? {expectedRevision:current.original.revision}:{}), tax: {
      name: current.values.name ?? '', jurisdiction: current.values.jurisdiction ?? '', taxType: current.values.taxType ?? '',
      amount: Number(current.values.amount),amountKind:current.values.amountKind ?? '',taxYearLabel:current.values.taxYearLabel ?? '',
      periodStart:current.values.periodStart ?? '',periodEnd:current.values.periodEnd ?? '',
      sourceFinancialFactId: current.original?.tax.sourceFinancialFactId ?? source?.financialFactId ?? '', sourceRevision: current.original?.tax.sourceRevision ?? source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = current.original && submission.expectedRevision !== undefined
        ? await reviseTaxRecord(api,current.original.taxRecordId,{...submission,expectedRevision:submission.expectedRevision})
        : await createTaxRecord(api, submission);
      if (record.operationId !== submission.operationId || record.tax.sourceFinancialFactId !== submission.tax.sourceFinancialFactId ||
          record.tax.sourceRevision !== submission.tax.sourceRevision || !record.taxRecordId || record.verificationStatus !== 'CUSTOMER_REVIEWED' || current.original && (record.taxRecordId !== current.original.taxRecordId || record.revision !== (submission.expectedRevision ?? 0)+1)) throw new Error('Invalid confirmation');
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaved(record); setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, open: false, submission: null } : active); await load();
    } catch (error) {
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      const conflict = error instanceof AccessApiError && error.code === 'TAX_RECORD_REVISION_CONFLICT';
      setSaveError(conflict ? 'A newer version has been saved. Close this form, refresh tax records and review that version before editing again.' : error instanceof AccessApiError && error.status === 400
        ? 'Check the amount, reporting period and required tax details. The period must end by today. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (conflict || error instanceof AccessApiError && error.status === 400) setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, submission: null, conflict } : active);
    } finally { setSaving(false); }
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view tax records and their sources." />;
  return <section aria-label="Tax records" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Tax records</h2>
      <Button disabled={loading || saving} onClick={() => { void load(); }}>Refresh tax records</Button>
      {canCreate && <Button onClick={open} disabled={saving || (!current?.submission && facts.every((fact) => fact.factType !== 'TAX'))}>
        {current?.submission ? 'Continue save confirmation' : 'Add tax record'}</Button>}
    </Space>
    <p>Record the tax amount for an explicit period and choose whether it represents tax paid,due or withheld. Jurisdiction,tax type and tax-year label are customer reported. This does not verify payment,filing,compliance or refund entitlement,and does not calculate tax.</p>
    {saved && editor?.api === api && <Alert type="success" title={'Tax record saved: ' + saved.tax.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry tax records</Button>} />}
    {loading && <p role="status">Loading tax records…</p>}
    {list?.items.length === 0 && <p>No tax records added yet. Review a tax source figure in Documents, then add its tax details and reporting period here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added tax records." />}
    {list?.items.map((record) => <Card key={record.taxRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.tax.name}</strong><Tag>Customer reviewed</Tag><span>{record.tax.jurisdiction} · {record.tax.taxType}</span></Space>
      <p>Reported tax amount: {money(record.tax.amount, record.currency)} · {record.tax.periodStart} to {record.tax.periodEnd}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View tax record: {record.tax.name}</Button>
      {canCreate && <Button disabled={saving || Boolean(current?.submission)} onClick={() => {edit(record);}}>Edit tax record: {record.tax.name}</Button>}
      <Button onClick={() => {setHistory({api,record});}}>View tax history: {record.tax.name}</Button>
    </Card>)}
    <Drawer title={current?.original ? "Edit tax record" : "Add tax record"} open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      {current?.original && <Alert type="info" title={'Editing version '+String(current.original.revision)+' · '+current.original.currency+' · Source revision '+String(current.original.tax.sourceRevision)} description="Corrections retain the original source, currency and customer-reviewed status. Every saved version remains in history." />}
      <p>Choose an exact reviewed tax source figure,then enter the jurisdiction,type,amount meaning,amount,tax-year label and period explicitly. The source amount does not fill the form.</p>
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission) || current?.conflict} style={{ border: 0, padding: 0 }}>
        {!current?.original && <><label htmlFor="tax-source">Reviewed tax source</label>
        <Select id="tax-source" aria-label="Reviewed tax source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed tax source"
          options={facts.filter((fact) => fact.factType === 'TAX').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate + ' · ' + fact.source.sourceName }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter the total received for this period separately.'} />}</>}
        {(['name', 'jurisdiction', 'taxType', 'taxYearLabel'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'tax-' + key}>{key === 'name' ? 'Tax name' : key === 'jurisdiction' ? 'Tax jurisdiction' : key === 'taxYearLabel' ? 'Tax year label' : 'Tax type'}</label>
          <Input id={'tax-' + key} maxLength={key === 'taxYearLabel' ? 30 : 120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        <label htmlFor="tax-amountKind">Amount represents</label><Select id="tax-amountKind" aria-label="Amount represents" style={{width:'100%'}}
          disabled={saving || Boolean(current?.submission)} options={amountKinds} value={current?.values.amountKind} onChange={(value:string)=>{change('amountKind',value);}} />
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'tax-' + key}>{label}</label>
          <Input id={'tax-' + key} type="number" min={0}
            max={999999999999.99}
            step={0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'tax-' + key}>{label}</label>
          <Input id={'tax-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
      </fieldset>
      <p>Saving retains your reviewed amount,meaning,period and exact source. Payment,filing,compliance and refund entitlement are unverified. The tax-year label does not establish tax-calendar membership.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : current?.original ? 'Save reviewed corrections' : 'Save reviewed tax record'}</Button>
    </Drawer>
    <Drawer title="Tax record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.tax.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Tax jurisdiction</dt><dd>{visible.tax.jurisdiction}</dd><dt>Tax type</dt><dd>{visible.tax.taxType}</dd>
        <dt>Period total</dt><dd>{money(visible.tax.amount,visible.currency)}</dd><dt>Currency</dt><dd>{visible.currency}</dd>
        <dt>Reporting period</dt><dd>{visible.tax.periodStart} to {visible.tax.periodEnd}</dd>
        <dt>Amount represents</dt><dd>{amountKindName(visible.tax.amountKind)}</dd><dt>Tax year label</dt><dd>{visible.tax.taxYearLabel}</dd>
        <dt>Review status</dt><dd>Customer reviewed</dd>
        <dt>Record version</dt><dd>{visible.revision}</dd><dt>Source revision</dt><dd>{visible.tax.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.tax.sourceFinancialFactId)}>View exact tax source history</a>
      <p>Tax details,amount meaning and period were reviewed by the customer. Payment,filing,compliance and refunds have not been independently verified.</p></>}
    </Drawer>
    <Drawer title="Tax record history" open={history?.api === api} onClose={() => {setHistory(null);}} size={560} destroyOnHidden>
      {history?.api === api && <TaxHistory key={history.record.taxRecordId} api={api} record={history.record} />}
    </Drawer>
  </section>;
}

function TaxHistory({ api, record }: { api: AccessApi; record: ProductTaxRecord }) {
  const sequence = useRef(0);const controller = useRef<AbortController | null>(null);
  const [page, setPage] = useState<ProductTaxRecordHistory | null>(null);
  const [failure, setFailure] = useState(false);
  const [loading, setLoading] = useState(false);
  const [version, setVersion] = useState<ProductTaxRecord | null>(null);
  const load = useCallback(async (before?: number, signal?: AbortSignal) => {
    const generation=++sequence.current;controller.current?.abort();const pending=new AbortController();controller.current=pending;
    const abort=()=>{pending.abort();};signal?.addEventListener('abort',abort,{once:true});
    const active=()=>!signal?.aborted && !pending.signal.aborted && generation===sequence.current;
    setLoading(true); setFailure(false); setPage(null); setVersion(null);
    try {
      const value = await getTaxRecordHistory(api, record.taxRecordId, before, pending.signal);
      if (!Array.isArray(value.items) || value.items.length > 50 || value.items.some((item) =>  item.taxRecordId !== record.taxRecordId || item.customerId !== record.customerId || item.verificationStatus !== 'CUSTOMER_REVIEWED' ||
          !Number.isInteger(item.revision) || item.revision < 1 || item.currency !== record.currency || item.sourceProvenanceId !== record.sourceProvenanceId || item.documentVersionId !== record.documentVersionId || item.evidenceId !== record.evidenceId ||
          item.tax.sourceFinancialFactId !== record.tax.sourceFinancialFactId || item.tax.sourceRevision !== record.tax.sourceRevision)) throw new Error('Invalid history');
      if (active()) setPage(value);
    } catch { if (active()) setFailure(true); }
    finally {signal?.removeEventListener('abort',abort);if (active()) setLoading(false);}
  }, [api, record]);
  useEffect(() => {
    const initial = new AbortController(); const start = window.setTimeout(() => { void load(undefined, initial.signal); }, 0);
    return () => { window.clearTimeout(start); initial.abort();controller.current?.abort(); };
  }, [load]);
  return <section aria-label="Reviewed tax change history"><h2>{record.tax.name}</h2>
    <p>Each version retains the reviewed amount,meaning,period and original source. The tax-year label does not establish tax-calendar membership. No filing,compliance,payment or refund verification is implied. Payer and tax verification remain separate.</p>
    {loading && <p role="status">Loading tax history…</p>}
    {failure && <Alert type="error" title="Tax history is unavailable." action={<Button onClick={() => { void load(); }}>Retry tax history</Button>} />}
    {page?.items.map((item) => <Card key={item.revision} size="small" style={{ marginBlock: 12 }}>
      <strong>Version {item.revision}</strong><p>{item.tax.name} · {item.tax.jurisdiction} · {money(item.tax.amount,item.currency)} · {item.tax.periodStart} to {item.tax.periodEnd}</p>
      <p>Reviewed {item.recordedAt}</p><Button onClick={() => { setVersion(item); }}>View version {item.revision}</Button>
    </Card>)}
    {page?.hasMore && page.nextBeforeRevision !== null && <Button disabled={loading} onClick={() => { void load(page.nextBeforeRevision ?? undefined); }}>Earlier versions</Button>}
    {page && <Button disabled={loading} onClick={() => { void load(); }}>Latest versions</Button>}
    {version && <section aria-label={'Saved tax version ' + String(version.revision)}><h3>Saved version {version.revision}</h3><dl>
      <dt>Tax name</dt><dd>{version.tax.name}</dd><dt>Tax jurisdiction</dt><dd>{version.tax.jurisdiction}</dd><dt>Tax type</dt><dd>{version.tax.taxType}</dd>
      <dt>Currency</dt><dd>{version.currency}</dd><dt>Period total</dt><dd>{money(version.tax.amount,version.currency)}</dd>
      <dt>Reporting period</dt><dd>{version.tax.periodStart} to {version.tax.periodEnd}</dd>
      <dt>Amount represents</dt><dd>{amountKindName(version.tax.amountKind)}</dd><dt>Tax year label</dt><dd>{version.tax.taxYearLabel}</dd>
      <dt>Review status</dt><dd>Customer reviewed</dd><dt>Recorded</dt><dd>{version.recordedAt}</dd>
      <dt>Source revision</dt><dd>{version.tax.sourceRevision}</dd></dl>
      <a href={'/finances?fact=' + encodeURIComponent(version.tax.sourceFinancialFactId)}>View exact tax source history</a></section>}
  </section>;
}
