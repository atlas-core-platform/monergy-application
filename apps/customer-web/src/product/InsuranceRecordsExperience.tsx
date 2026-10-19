import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createInsuranceRecord, getInsuranceRecords, reviseInsuranceRecord, getInsuranceRecordHistory } from './productApi';
import type { ProductFinancialFact, ProductInsuranceDraft, ProductInsuranceRecord, ProductInsuranceRecordList, ProductInsuranceRecordHistory } from './productApi';

const numberFields = [ ['sumAssured', 'Sum assured'], ['premiumAmount', 'Premium amount'] ] as const;
const dateFields = [ ['policyAsOfDate', 'Policy information as of'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; insurance: ProductInsuranceDraft; expectedRevision?: number }
interface Editor { api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null; original: ProductInsuranceRecord | null; conflict?: boolean }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);
const terms = (record: ProductInsuranceRecord) => record.insurance.policyType;

export default function InsuranceRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const scope = useRef({ api, canRead, canCreate });
  useLayoutEffect(() => { scope.current = { api, canRead, canCreate }; }, [api, canRead, canCreate]);
  const loadSequence = useRef(0);
  const activeLoad = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductInsuranceRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<ProductInsuranceRecord | null>(null);
  const [history, setHistory] = useState<{ api: AccessApi; record: ProductInsuranceRecord } | null>(null);
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductInsuranceRecord } | null>(null);
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
      const list = await getInsuranceRecords(api, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean') throw new Error('Invalid record list');
      if (active()) setLoaded({ api, list });
    } catch (error) {
      if (active()) { setLoaded(null); setFailure({ api, text: error instanceof AccessApiError && error.status === 403
        ? 'Your current access does not include insurance policies and their source documents.' : 'Insurance policies are unavailable. Try again.' }); }
    } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, canRead]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => { void load(controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); activeLoad.current?.abort(); };
  }, [load]);
  const current = editor?.api === api ? editor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'INSURANCE');
  const valid = current && (current.original ?? source) && !current.conflict && current.values.name?.trim() && current.values.provider?.trim() && current.values.policyType?.trim() &&
    numberFields.every(([key]) => current.values[key] !== undefined && current.values[key] !== '' && Number.isFinite(Number(current.values[key]))) &&
    dateFields.every(([key]) => current.values[key]);
  const open = () => {
    setSaveError(''); setSaved(null);
    if (current?.submission) setEditor({ ...current, open: true });
    else setEditor({ api, open: true, values: {}, operationId: crypto.randomUUID().replaceAll('-', ''), submission: null, original: null });
  };
  const edit = (record: ProductInsuranceRecord) => {
    if(current?.submission || saving) { if(current) setEditor({...current,open:true}); return; }
    setSelected(null);setSaved(null);setSaveError('');
    setEditor({api,open:true,original:record,values:Object.fromEntries(Object.entries(record.insurance).map(([key,value])=>[key,value === null ? '' : String(value)])),
      operationId:crypto.randomUUID().replaceAll('-',''),submission:null});
  };
  const change = (key: string, value: string) => {
    if (current && !current.submission && !current.conflict && !saving) setEditor({ ...current, values: { ...current.values, [key]: value } });
  };
  const save = async () => {
    if (!current || !canRead || !canCreate || saving || (!current.submission && !valid)) return;
    const maturity = current.values.maturityDate ?? '';
    const submission = current.submission ?? { operationId: current.operationId, ...(current.original ? {expectedRevision:current.original.revision}:{}), insurance: {
      name: current.values.name ?? '', provider: current.values.provider ?? '', policyType: current.values.policyType ?? '',
      sumAssured: Number(current.values.sumAssured), premiumAmount: Number(current.values.premiumAmount),
      policyAsOfDate: current.values.policyAsOfDate ?? '', maturityDate: maturity.length === 0 ? null : maturity,
      sourceFinancialFactId: current.original?.insurance.sourceFinancialFactId ?? source?.financialFactId ?? '', sourceRevision: current.original?.insurance.sourceRevision ?? source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = current.original && submission.expectedRevision !== undefined
        ? await reviseInsuranceRecord(api,current.original.insuranceRecordId,{...submission,expectedRevision:submission.expectedRevision})
        : await createInsuranceRecord(api, submission);
      if (record.operationId !== submission.operationId || record.insurance.sourceFinancialFactId !== submission.insurance.sourceFinancialFactId ||
          record.insurance.sourceRevision !== submission.insurance.sourceRevision || !record.insuranceRecordId || current.original && (record.insuranceRecordId !== current.original.insuranceRecordId || record.revision !== (submission.expectedRevision ?? 0)+1)) throw new Error('Invalid confirmation');
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaved(record); setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, open: false, submission: null } : active); await load();
    } catch (error) {
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      const conflict = error instanceof AccessApiError && error.code === 'INSURANCE_POLICY_REVISION_CONFLICT';
      setSaveError(conflict ? 'A newer version has been saved. Close this form, refresh insurance policies and review that version before editing again.' : error instanceof AccessApiError && error.status === 400
        ? 'Check the required amounts, dates, provider and policy type. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (conflict || error instanceof AccessApiError && error.status === 400) setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, submission: null, conflict } : active);
    } finally { setSaving(false); }
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view insurance policies and their sources." />;
  return <section aria-label="Insurance policies" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Insurance policies</h2>
      <Button disabled={loading || saving} onClick={() => { void load(); }}>Refresh insurance policies</Button>
      {canCreate && <Button onClick={open} disabled={saving || (!current?.submission && facts.every((fact) => !(fact.factType === 'INSURANCE')))}>
        {current?.submission ? 'Continue save confirmation' : 'Add insurance record'}</Button>}
    </Space>
    <p>Record the policy information you have reviewed. Enter sum assured and premium separately; the source figure does not fill either amount. These are customer-reported policy terms, not verified active cover. Premium frequency and payment status are not inferred.</p>
    {saved && editor?.api === api && <Alert type="success" title={'Insurance record saved: ' + saved.insurance.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry insurance policies</Button>} />}
    {loading && <p role="status">Loading insurance policies…</p>}
    {list?.items.length === 0 && <p>No insurance policies added yet. Review an insurance source figure in Documents, then add the holding and values here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added insurance policies." />}
    {list?.items.map((record) => <Card key={record.insuranceRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.insurance.name}</strong><Tag>Customer reviewed</Tag><span>{record.insurance.provider} · {terms(record)}</span></Space>
      <p>Premium amount: {money(record.insurance.premiumAmount, record.currency)} · as of {record.insurance.policyAsOfDate}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View insurance record: {record.insurance.name}</Button>
      {canCreate && <Button disabled={saving || Boolean(current?.submission)} onClick={() => {edit(record);}}>Edit insurance record: {record.insurance.name}</Button>}
      <Button onClick={() => {setHistory({api,record});}}>View insurance history: {record.insurance.name}</Button>
    </Card>)}
    <Drawer title={current?.original ? "Edit insurance record" : "Add insurance record"} open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      {current?.original && <Alert type="info" title={'Editing version '+String(current.original.revision)+' · '+current.original.currency+' · Source revision '+String(current.original.insurance.sourceRevision)} description="Corrections retain the original source and currency. Every saved version remains in history." />}
      <p>Choose an exact reviewed insurance source. Enter the provider, policy type, sum assured, premium amount and information date explicitly. Leave maturity or expiry blank if unknown. The source amount is not used to fill either value.</p>
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission) || current?.conflict} style={{ border: 0, padding: 0 }}>
        {!current?.original && <><label htmlFor="insurance-source">Reviewed insurance source</label>
        <Select id="insurance-source" aria-label="Reviewed insurance source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed insurance source"
          options={facts.filter((fact) => fact.factType === 'INSURANCE').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter sum assured and premium separately.'} />}</>}
        {(['name', 'provider', 'policyType'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'insurance-' + key}>{key === 'name' ? 'Policy name' : key === 'provider' ? 'Insurance provider' : 'Policy type'}</label>
          <Input id={'insurance-' + key} maxLength={120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'insurance-' + key}>{label}</label>
          <Input id={'insurance-' + key} type="number" min={0}
            max={999999999999999}
            step={0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'insurance-' + key}>{label}</label>
          <Input id={'insurance-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        <label htmlFor="insurance-maturity">Maturity or expiry date (optional)</label>
        <Input id="insurance-maturity" type="date" min="1900-01-01" value={current?.values.maturityDate ?? ''} onChange={event=>{change('maturityDate',event.target.value);}} />
      </fieldset>
      <p>Saving retains your reviewed terms and exact source. It does not verify active coverage,provider confirmation,premium payment or benefit eligibility. Maturity does not determine coverage status.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : current?.original ? 'Save reviewed corrections' : 'Save reviewed insurance record'}</Button>
    </Drawer>
    <Drawer title="Insurance record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.insurance.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Insurance provider</dt><dd>{visible.insurance.provider}</dd><dt>Policy type</dt><dd>{terms(visible)}</dd>
        <dt>Maturity or expiry date</dt><dd>{visible.insurance.maturityDate ?? 'Not supplied'}</dd><dt>Currency</dt><dd>{visible.currency}</dd><dt>Sum assured</dt><dd>{money(visible.insurance.sumAssured, visible.currency)}</dd>
        <dt>Premium amount</dt><dd>{money(visible.insurance.premiumAmount, visible.currency)} as of {visible.insurance.policyAsOfDate}</dd>
        <dt>Record version</dt><dd>{visible.revision}</dd><dt>Source revision</dt><dd>{visible.insurance.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.insurance.sourceFinancialFactId)}>View exact insurance source history</a>
      <p>Policy terms were entered and reviewed by the customer. Active cover,premium frequency,payment and benefit eligibility have not been verified.</p></>}
    </Drawer>
    <Drawer title="Insurance record history" open={history?.api === api} onClose={() => {setHistory(null);}} size={560} destroyOnHidden>
      {history?.api === api && <InsuranceHistory key={history.record.insuranceRecordId} api={api} record={history.record} />}
    </Drawer>
  </section>;
}

function InsuranceHistory({ api, record }: { api: AccessApi; record: ProductInsuranceRecord }) {
  const sequence = useRef(0);const controller = useRef<AbortController | null>(null);
  const [page, setPage] = useState<ProductInsuranceRecordHistory | null>(null);
  const [failure, setFailure] = useState(false);
  const [loading, setLoading] = useState(false);
  const [version, setVersion] = useState<ProductInsuranceRecord | null>(null);
  const load = useCallback(async (before?: number, signal?: AbortSignal) => {
    const generation=++sequence.current;controller.current?.abort();const pending=new AbortController();controller.current=pending;
    const abort=()=>{pending.abort();};signal?.addEventListener('abort',abort,{once:true});
    const active=()=>!signal?.aborted && !pending.signal.aborted && generation===sequence.current;
    setLoading(true); setFailure(false); setPage(null); setVersion(null);
    try {
      const value = await getInsuranceRecordHistory(api, record.insuranceRecordId, before, pending.signal);
      if (!Array.isArray(value.items) || value.items.length > 50 || value.items.some((item) => item.insuranceRecordId !== record.insuranceRecordId || item.customerId !== record.customerId ||
          !Number.isInteger(item.revision) || item.revision < 1 || item.currency !== record.currency || item.sourceProvenanceId !== record.sourceProvenanceId || item.documentVersionId !== record.documentVersionId || item.evidenceId !== record.evidenceId ||
          item.insurance.sourceFinancialFactId !== record.insurance.sourceFinancialFactId || item.insurance.sourceRevision !== record.insurance.sourceRevision)) throw new Error('Invalid history');
      if (active()) setPage(value);
    } catch { if (active()) setFailure(true); }
    finally {signal?.removeEventListener('abort',abort);if (active()) setLoading(false);}
  }, [api, record]);
  useEffect(() => {
    const initial = new AbortController(); const start = window.setTimeout(() => { void load(undefined, initial.signal); }, 0);
    return () => { window.clearTimeout(start); initial.abort();controller.current?.abort(); };
  }, [load]);
  return <section aria-label="Reviewed insurance change history"><h2>{record.insurance.name}</h2>
    <p>Each version retains the reviewed policy terms and original source. History does not verify active cover,premium payment or benefit eligibility.</p>
    {loading && <p role="status">Loading insurance history…</p>}
    {failure && <Alert type="error" title="Insurance history is unavailable." action={<Button onClick={() => { void load(); }}>Retry insurance history</Button>} />}
    {page?.items.map((item) => <Card key={item.revision} size="small" style={{ marginBlock: 12 }}>
      <strong>Version {item.revision}</strong><p>{item.insurance.name} · {item.insurance.provider} · {money(item.insurance.premiumAmount,item.currency)} as of {item.insurance.policyAsOfDate}</p>
      <p>Reviewed {item.recordedAt}</p><Button onClick={() => { setVersion(item); }}>View version {item.revision}</Button>
    </Card>)}
    {page?.hasMore && page.nextBeforeRevision !== null && <Button disabled={loading} onClick={() => { void load(page.nextBeforeRevision ?? undefined); }}>Earlier versions</Button>}
    {page && <Button disabled={loading} onClick={() => { void load(); }}>Latest versions</Button>}
    {version && <section aria-label={'Saved insurance version ' + String(version.revision)}><h3>Saved version {version.revision}</h3><dl>
      <dt>Policy name</dt><dd>{version.insurance.name}</dd><dt>Insurance provider</dt><dd>{version.insurance.provider}</dd><dt>Policy type</dt><dd>{terms(version)}</dd>
      <dt>Maturity or expiry date</dt><dd>{version.insurance.maturityDate ?? 'Not supplied'}</dd><dt>Currency</dt><dd>{version.currency}</dd><dt>Sum assured</dt><dd>{money(version.insurance.sumAssured,version.currency)}</dd>
      <dt>Premium amount</dt><dd>{money(version.insurance.premiumAmount,version.currency)} as of {version.insurance.policyAsOfDate}</dd>
      <dt>Source revision</dt><dd>{version.insurance.sourceRevision}</dd></dl>
      <a href={'/finances?fact=' + encodeURIComponent(version.insurance.sourceFinancialFactId)}>View exact insurance source history</a></section>}
  </section>;
}
