import IncomePeriodComparisonExperience from './IncomePeriodComparisonExperience';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createIncomeRecord, getIncomeRecords, reviseIncomeRecord, getIncomeRecordHistory } from './productApi';
import type { ProductFinancialFact, ProductIncomeDraft, ProductIncomeRecord, ProductIncomeRecordList, ProductIncomeRecordHistory } from './productApi';

const numberFields = [ ['amount', 'Amount received in this period'] ] as const;
const frequencies = [{value:'ONE_OFF',label:'One-off'},{value:'DAILY',label:'Daily'},{value:'WEEKLY',label:'Weekly'},{value:'MONTHLY',label:'Monthly'},{value:'QUARTERLY',label:'Quarterly'},{value:'ANNUAL',label:'Yearly'},{value:'IRREGULAR',label:'Irregular'}];
const dateFields = [ ['periodStart', 'Period start'], ['periodEnd', 'Period end'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; income: ProductIncomeDraft; expectedRevision?: number }
interface Editor { api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null; original: ProductIncomeRecord | null; conflict?: boolean }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);
const frequencyName = (value: string) => frequencies.find(option => option.value === value)?.label ?? value;

export default function IncomeRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const scope = useRef({ api, canRead, canCreate });
  useLayoutEffect(() => { scope.current = { api, canRead, canCreate }; }, [api, canRead, canCreate]);
  const loadSequence = useRef(0);
  const activeLoad = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductIncomeRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<ProductIncomeRecord | null>(null);
  const [history, setHistory] = useState<{ api: AccessApi; record: ProductIncomeRecord } | null>(null);
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductIncomeRecord } | null>(null);
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
      const list = await getIncomeRecords(api, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean' || list.items.some(item=>item.verificationStatus !== 'CUSTOMER_REVIEWED')) throw new Error('Invalid record list');
      if (active()) setLoaded({ api, list });
    } catch (error) {
      if (active()) { setLoaded(null); setFailure({ api, text: error instanceof AccessApiError && error.status === 403
        ? 'Your current access does not include income records and their source documents.' : 'Income records are unavailable. Try again.' }); }
    } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, canRead]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => { void load(controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); activeLoad.current?.abort(); };
  }, [load]);
  const current = editor?.api === api ? editor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'INCOME');
  const valid = current && (current.original ?? source) && !current.conflict && current.values.name?.trim() && current.values.incomeSource?.trim() && current.values.incomeType?.trim() && current.values.financialYear?.trim() && current.values.frequency &&
    numberFields.every(([key]) => current.values[key] !== undefined && current.values[key] !== '' && Number.isFinite(Number(current.values[key]))) &&
    dateFields.every(([key]) => current.values[key]);
  const open = () => {
    setSaveError(''); setSaved(null);
    if (current?.submission) setEditor({ ...current, open: true });
    else setEditor({ api, open: true, values: {}, operationId: crypto.randomUUID().replaceAll('-', ''), submission: null, original: null });
  };
  const edit = (record: ProductIncomeRecord) => {
    if(current?.submission || saving) { if(current) setEditor({...current,open:true}); return; }
    setSelected(null);setSaved(null);setSaveError('');
    setEditor({api,open:true,original:record,values:Object.fromEntries(Object.entries(record.income).map(([key,value])=>[key,String(value)])),
      operationId:crypto.randomUUID().replaceAll('-',''),submission:null});
  };
  const change = (key: string, value: string) => {
    if (current && !current.submission && !current.conflict && !saving) setEditor({ ...current, values: { ...current.values, [key]: value } });
  };
  const save = async () => {
    if (!current || !canRead || !canCreate || saving || (!current.submission && !valid)) return;
    const submission = current.submission ?? { operationId: current.operationId, ...(current.original ? {expectedRevision:current.original.revision}:{}), income: {
      name: current.values.name ?? '', incomeSource: current.values.incomeSource ?? '', incomeType: current.values.incomeType ?? '',
      amount: Number(current.values.amount),frequency:current.values.frequency ?? '',financialYear:current.values.financialYear ?? '',
      periodStart:current.values.periodStart ?? '',periodEnd:current.values.periodEnd ?? '',
      sourceFinancialFactId: current.original?.income.sourceFinancialFactId ?? source?.financialFactId ?? '', sourceRevision: current.original?.income.sourceRevision ?? source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = current.original && submission.expectedRevision !== undefined
        ? await reviseIncomeRecord(api,current.original.incomeRecordId,{...submission,expectedRevision:submission.expectedRevision})
        : await createIncomeRecord(api, submission);
      if (record.operationId !== submission.operationId || record.income.sourceFinancialFactId !== submission.income.sourceFinancialFactId ||
          record.income.sourceRevision !== submission.income.sourceRevision || !record.incomeRecordId || record.verificationStatus !== 'CUSTOMER_REVIEWED' || current.original && (record.incomeRecordId !== current.original.incomeRecordId || record.revision !== (submission.expectedRevision ?? 0)+1)) throw new Error('Invalid confirmation');
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaved(record); setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, open: false, submission: null } : active); await load();
    } catch (error) {
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      const conflict = error instanceof AccessApiError && error.code === 'INCOME_REVISION_CONFLICT';
      setSaveError(conflict ? 'A newer version has been saved. Close this form, refresh income records and review that version before editing again.' : error instanceof AccessApiError && error.status === 400
        ? 'Check the amount, reporting period and required income details. The period must end by today. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (conflict || error instanceof AccessApiError && error.status === 400) setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, submission: null, conflict } : active);
    } finally { setSaving(false); }
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view income records and their sources." />;
  return <section aria-label="Income records" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Income records</h2>
      <Button disabled={loading || saving} onClick={() => { void load(); }}>Refresh income records</Button>
      {canCreate && <Button onClick={open} disabled={saving || (!current?.submission && facts.every((fact) => fact.factType !== 'INCOME'))}>
        {current?.submission ? 'Continue save confirmation' : 'Add income record'}</Button>}
    </Space>
    <p>Record the total received for a stated past reporting period. Frequency describes the payment schedule. Your financial-year label and income details are customer reported; no totals or annualisation are calculated.</p>
    {saved && editor?.api === api && <Alert type="success" title={'Income record saved: ' + saved.income.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry income records</Button>} />}
    {loading && <p role="status">Loading income records…</p>}
    {list?.items.length === 0 && <p>No income records added yet. Review an income source figure in Documents, then add its income details and reporting period here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added income records." />}
    {list && <IncomePeriodComparisonExperience api={api} records={list.items} hasMore={list.hasMore} />}
    {list?.items.map((record) => <Card key={record.incomeRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.income.name}</strong><Tag>Customer reviewed</Tag><span>{record.income.incomeSource} · {record.income.incomeType}</span></Space>
      <p>Period total: {money(record.income.amount, record.currency)} · {record.income.periodStart} to {record.income.periodEnd}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View income record: {record.income.name}</Button>
      {canCreate && <Button disabled={saving || Boolean(current?.submission)} onClick={() => {edit(record);}}>Edit income record: {record.income.name}</Button>}
      <Button onClick={() => {setHistory({api,record});}}>View income history: {record.income.name}</Button>
    </Card>)}
    <Drawer title={current?.original ? "Edit income record" : "Add income record"} open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      {current?.original && <Alert type="info" title={'Editing version '+String(current.original.revision)+' · '+current.original.currency+' · Source revision '+String(current.original.income.sourceRevision)} description="Corrections retain the original source, currency and customer-reviewed status. Every saved version remains in history." />}
      <p>Choose an exact reviewed income source figure, then enter the income details and total for its reporting period. The source amount does not fill the form.</p>
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission) || current?.conflict} style={{ border: 0, padding: 0 }}>
        {!current?.original && <><label htmlFor="income-source">Reviewed income source</label>
        <Select id="income-source" aria-label="Reviewed income source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed income source"
          options={facts.filter((fact) => fact.factType === 'INCOME').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate + ' · ' + fact.source.sourceName }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter the total received for this period separately.'} />}</>}
        {(['name', 'incomeSource', 'incomeType', 'financialYear'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'income-' + key}>{key === 'name' ? 'Income name' : key === 'incomeSource' ? 'Income source (employer or payer)' : key === 'financialYear' ? 'Financial year (as reported)' : 'Income type'}</label>
          <Input id={'income-' + key} maxLength={key === 'financialYear' ? 30 : 120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        <label htmlFor="income-frequency">Payment frequency</label><Select id="income-frequency" aria-label="Payment frequency" style={{width:'100%'}}
          disabled={saving || Boolean(current?.submission)} options={frequencies} value={current?.values.frequency} onChange={(value:string)=>{change('frequency',value);}} />
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'income-' + key}>{label}</label>
          <Input id={'income-' + key} type="number" min={0}
            max={999999999999.99}
            step={0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'income-' + key}>{label}</label>
          <Input id={'income-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
      </fieldset>
      <p>Saving records your review and the exact source revision. It does not verify income with the payer or a tax authority. The financial-year label does not establish tax-calendar membership.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : current?.original ? 'Save reviewed corrections' : 'Save reviewed income record'}</Button>
    </Drawer>
    <Drawer title="Income record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.income.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Income source</dt><dd>{visible.income.incomeSource}</dd><dt>Income type</dt><dd>{visible.income.incomeType}</dd>
        <dt>Period total</dt><dd>{money(visible.income.amount,visible.currency)}</dd><dt>Currency</dt><dd>{visible.currency}</dd>
        <dt>Reporting period</dt><dd>{visible.income.periodStart} to {visible.income.periodEnd}</dd>
        <dt>Payment frequency</dt><dd>{frequencyName(visible.income.frequency)}</dd><dt>Financial year (as reported)</dt><dd>{visible.income.financialYear}</dd>
        <dt>Review status</dt><dd>Customer reviewed</dd>
        <dt>Record version</dt><dd>{visible.revision}</dd><dt>Source revision</dt><dd>{visible.income.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.income.sourceFinancialFactId)}>View exact income source history</a>
      <p>Income details and period total were entered and reviewed by the customer. They have not been independently verified with the payer or a tax authority.</p></>}
    </Drawer>
    <Drawer title="Income record history" open={history?.api === api} onClose={() => {setHistory(null);}} size={560} destroyOnHidden>
      {history?.api === api && <IncomeHistory key={history.record.incomeRecordId} api={api} record={history.record} />}
    </Drawer>
  </section>;
}

function IncomeHistory({ api, record }: { api: AccessApi; record: ProductIncomeRecord }) {
  const sequence = useRef(0);const controller = useRef<AbortController | null>(null);
  const [page, setPage] = useState<ProductIncomeRecordHistory | null>(null);
  const [failure, setFailure] = useState(false);
  const [loading, setLoading] = useState(false);
  const [version, setVersion] = useState<ProductIncomeRecord | null>(null);
  const load = useCallback(async (before?: number, signal?: AbortSignal) => {
    const generation=++sequence.current;controller.current?.abort();const pending=new AbortController();controller.current=pending;
    const abort=()=>{pending.abort();};signal?.addEventListener('abort',abort,{once:true});
    const active=()=>!signal?.aborted && !pending.signal.aborted && generation===sequence.current;
    setLoading(true); setFailure(false); setPage(null); setVersion(null);
    try {
      const value = await getIncomeRecordHistory(api, record.incomeRecordId, before, pending.signal);
      if (!Array.isArray(value.items) || value.items.length > 50 || value.items.some((item) => item.incomeRecordId !== record.incomeRecordId || item.verificationStatus !== 'CUSTOMER_REVIEWED')) throw new Error('Invalid history');
      if (active()) setPage(value);
    } catch { if (active()) setFailure(true); }
    finally {signal?.removeEventListener('abort',abort);if (active()) setLoading(false);}
  }, [api, record.incomeRecordId]);
  useEffect(() => {
    const initial = new AbortController(); const start = window.setTimeout(() => { void load(undefined, initial.signal); }, 0);
    return () => { window.clearTimeout(start); initial.abort();controller.current?.abort(); };
  }, [load]);
  return <section aria-label="Reviewed income change history"><h2>{record.income.name}</h2>
    <p>Each saved version retains its reviewed values and original source. Amounts are period totals; frequency and the reported financial year do not annualise them or establish tax-calendar membership. Payer and tax verification remain separate.</p>
    {loading && <p role="status">Loading income history…</p>}
    {failure && <Alert type="error" title="Income history is unavailable." action={<Button onClick={() => { void load(); }}>Retry income history</Button>} />}
    {page?.items.map((item) => <Card key={item.revision} size="small" style={{ marginBlock: 12 }}>
      <strong>Version {item.revision}</strong><p>{item.income.name} · {item.income.incomeSource} · {money(item.income.amount,item.currency)} · {item.income.periodStart} to {item.income.periodEnd}</p>
      <p>Reviewed {item.recordedAt}</p><Button onClick={() => { setVersion(item); }}>View version {item.revision}</Button>
    </Card>)}
    {page?.hasMore && page.nextBeforeRevision !== null && <Button disabled={loading} onClick={() => { void load(page.nextBeforeRevision ?? undefined); }}>Earlier versions</Button>}
    {page && <Button disabled={loading} onClick={() => { void load(); }}>Latest versions</Button>}
    {version && <section aria-label={'Saved income version ' + String(version.revision)}><h3>Saved version {version.revision}</h3><dl>
      <dt>Income name</dt><dd>{version.income.name}</dd><dt>Income source</dt><dd>{version.income.incomeSource}</dd><dt>Income type</dt><dd>{version.income.incomeType}</dd>
      <dt>Currency</dt><dd>{version.currency}</dd><dt>Period total</dt><dd>{money(version.income.amount,version.currency)}</dd>
      <dt>Reporting period</dt><dd>{version.income.periodStart} to {version.income.periodEnd}</dd>
      <dt>Payment frequency</dt><dd>{frequencyName(version.income.frequency)}</dd><dt>Financial year (as reported)</dt><dd>{version.income.financialYear}</dd>
      <dt>Review status</dt><dd>Customer reviewed</dd><dt>Recorded</dt><dd>{version.recordedAt}</dd>
      <dt>Source revision</dt><dd>{version.income.sourceRevision}</dd></dl>
      <a href={'/finances?fact=' + encodeURIComponent(version.income.sourceFinancialFactId)}>View exact income source history</a></section>}
  </section>;
}
