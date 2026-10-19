import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createVehicleRecord, getVehicleRecords, reviseVehicleRecord, getVehicleRecordHistory } from './productApi';
import type { ProductFinancialFact, ProductVehicleDraft, ProductVehicleRecord, ProductVehicleRecordList, ProductVehicleRecordHistory } from './productApi';

const numberFields = [ ['purchaseValue', 'Purchase value'], ['currentValue', 'Current value'] ] as const;
const dateFields = [ ['valueAsOfDate', 'Value as of'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; vehicle: ProductVehicleDraft; expectedRevision?: number }
interface Editor { api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null; original: ProductVehicleRecord | null; conflict?: boolean }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);
const terms = (record: ProductVehicleRecord) => record.vehicle.vehicleType;

export default function VehicleRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const scope = useRef({ api, canRead, canCreate });
  useLayoutEffect(() => { scope.current = { api, canRead, canCreate }; }, [api, canRead, canCreate]);
  const loadSequence = useRef(0);
  const activeLoad = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductVehicleRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<ProductVehicleRecord | null>(null);
  const [history, setHistory] = useState<{ api: AccessApi; record: ProductVehicleRecord } | null>(null);
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductVehicleRecord } | null>(null);
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
      const list = await getVehicleRecords(api, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean') throw new Error('Invalid record list');
      if (active()) setLoaded({ api, list });
    } catch (error) {
      if (active()) { setLoaded(null); setFailure({ api, text: error instanceof AccessApiError && error.status === 403
        ? 'Your current access does not include vehicle records and their source documents.' : 'Vehicle records are unavailable. Try again.' }); }
    } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, canRead]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => { void load(controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); activeLoad.current?.abort(); };
  }, [load]);
  const current = editor?.api === api ? editor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'ASSET' && fact.assetType === 'VEHICLE');
  const valid = current && (current.original ?? source) && !current.conflict && current.values.name?.trim() && current.values.vehicleType?.trim() &&
    numberFields.every(([key]) => current.values[key] !== undefined && current.values[key] !== '' && Number.isFinite(Number(current.values[key]))) &&
    dateFields.every(([key]) => current.values[key]);
  const open = () => {
    setSaveError(''); setSaved(null);
    if (current?.submission) setEditor({ ...current, open: true });
    else setEditor({ api, open: true, values: {}, operationId: crypto.randomUUID().replaceAll('-', ''), submission: null, original: null });
  };
  const edit = (record: ProductVehicleRecord) => {
    if(current?.submission || saving) { if(current) setEditor({...current,open:true}); return; }
    setSelected(null);setSaved(null);setSaveError('');
    setEditor({api,open:true,original:record,values:Object.fromEntries(Object.entries(record.vehicle).map(([key,value])=>[key,value === null ? '' : String(value)])),
      operationId:crypto.randomUUID().replaceAll('-',''),submission:null});
  };
  const change = (key: string, value: string) => {
    if (current && !current.submission && !current.conflict && !saving) setEditor({ ...current, values: { ...current.values, [key]: value } });
  };
  const save = async () => {
    if (!current || !canRead || !canCreate || saving || (!current.submission && !valid)) return;
    const registration = current.values.registrationNumber?.trim() ?? '';
    const submission = current.submission ?? { operationId: current.operationId, ...(current.original ? {expectedRevision:current.original.revision}:{}), vehicle: {
      name: current.values.name ?? '', registrationNumber: registration.length === 0 ? null : registration, vehicleType: current.values.vehicleType ?? '',
      purchaseValue: Number(current.values.purchaseValue), currentValue: Number(current.values.currentValue),
      valueAsOfDate: current.values.valueAsOfDate ?? '',
      sourceFinancialFactId: current.original?.vehicle.sourceFinancialFactId ?? source?.financialFactId ?? '', sourceRevision: current.original?.vehicle.sourceRevision ?? source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = current.original && submission.expectedRevision !== undefined
        ? await reviseVehicleRecord(api,current.original.vehicleRecordId,{...submission,expectedRevision:submission.expectedRevision})
        : await createVehicleRecord(api, submission);
      if (record.operationId !== submission.operationId || record.vehicle.sourceFinancialFactId !== submission.vehicle.sourceFinancialFactId ||
          record.vehicle.sourceRevision !== submission.vehicle.sourceRevision || !record.vehicleRecordId || current.original && (record.vehicleRecordId !== current.original.vehicleRecordId || record.revision !== (submission.expectedRevision ?? 0)+1)) throw new Error('Invalid confirmation');
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaved(record); setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, open: false, submission: null } : active); await load();
    } catch (error) {
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      const conflict = error instanceof AccessApiError && error.code === 'VEHICLE_REVISION_CONFLICT';
      setSaveError(conflict ? 'A newer version has been saved. Close this form, refresh vehicle records and review that version before editing again.' : error instanceof AccessApiError && error.status === 400
        ? 'Check the required amounts, date, registration reference and vehicle type. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (conflict || error instanceof AccessApiError && error.status === 400) setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, submission: null, conflict } : active);
    } finally { setSaving(false); }
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view vehicle records and their sources." />;
  return <section aria-label="Vehicle records" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Vehicle records</h2>
      <Button disabled={loading || saving} onClick={() => { void load(); }}>Refresh vehicle records</Button>
      {canCreate && <Button onClick={open} disabled={saving || (!current?.submission && facts.every((fact) => !(fact.factType === 'ASSET' && fact.assetType === 'VEHICLE')))}>
        {current?.submission ? 'Continue save confirmation' : 'Add vehicle record'}</Button>}
    </Space>
    <p>Record the vehicle details and values you have reviewed. Source figures stay separate. Registration references, ownership and valuations are customer reported; these records do not calculate total assets or net worth.</p>
    {saved && editor?.api === api && <Alert type="success" title={'Vehicle record saved: ' + saved.vehicle.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry vehicle records</Button>} />}
    {loading && <p role="status">Loading vehicle records…</p>}
    {list?.items.length === 0 && <p>No vehicle records added yet. Review a vehicle source figure in Documents, then add the holding and values here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added vehicle records." />}
    {list?.items.map((record) => <Card key={record.vehicleRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.vehicle.name}</strong><Tag>Customer reviewed</Tag><span>{record.vehicle.registrationNumber ?? 'Registration not supplied'} · {terms(record)}</span></Space>
      <p>Current value: {money(record.vehicle.currentValue, record.currency)} · as of {record.vehicle.valueAsOfDate}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View vehicle record: {record.vehicle.name}</Button>
      {canCreate && <Button disabled={saving || Boolean(current?.submission)} onClick={() => {edit(record);}}>Edit vehicle record: {record.vehicle.name}</Button>}
      <Button onClick={() => {setHistory({api,record});}}>View vehicle history: {record.vehicle.name}</Button>
    </Card>)}
    <Drawer title={current?.original ? "Edit vehicle record" : "Add vehicle record"} open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      {current?.original && <Alert type="info" title={'Editing version '+String(current.original.revision)+' · '+current.original.currency+' · Source revision '+String(current.original.vehicle.sourceRevision)} description="Corrections retain the original source and currency. Every saved version remains in history." />}
      <p>Choose an exact reviewed vehicle source. Enter the vehicle type, both values and value date explicitly. Registration reference is optional. The source amount is not used to fill either value.</p>
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission) || current?.conflict} style={{ border: 0, padding: 0 }}>
        {!current?.original && <><label htmlFor="vehicle-source">Reviewed vehicle source</label>
        <Select id="vehicle-source" aria-label="Reviewed vehicle source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed vehicle source"
          options={facts.filter((fact) => fact.factType === 'ASSET' && fact.assetType === 'VEHICLE').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter purchase and current values separately.'} />}</>}
        {(['name', 'registrationNumber', 'vehicleType'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'vehicle-' + key}>{key === 'name' ? 'Vehicle name' : key === 'registrationNumber' ? 'Registration reference (optional)' : 'Vehicle type'}</label>
          <Input id={'vehicle-' + key} maxLength={key === 'registrationNumber' ? 40 : 120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'vehicle-' + key}>{label}</label>
          <Input id={'vehicle-' + key} type="number" min={0}
            max={999999999999999}
            step={0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'vehicle-' + key}>{label}</label>
          <Input id={'vehicle-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
      </fieldset>
      <p>Saving records your review and the exact source revision. It does not verify ownership or registration, ownership or current market value.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : current?.original ? 'Save reviewed corrections' : 'Save reviewed vehicle record'}</Button>
    </Drawer>
    <Drawer title="Vehicle record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.vehicle.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Registration reference (optional)</dt><dd>{visible.vehicle.registrationNumber ?? 'Not supplied'}</dd><dt>Vehicle type</dt><dd>{terms(visible)}</dd>
        <dt>Currency</dt><dd>{visible.currency}</dd><dt>Purchase value</dt><dd>{money(visible.vehicle.purchaseValue, visible.currency)}</dd>
        <dt>Current value</dt><dd>{money(visible.vehicle.currentValue, visible.currency)} as of {visible.vehicle.valueAsOfDate}</dd>
        <dt>Record version</dt><dd>{visible.revision}</dd><dt>Source revision</dt><dd>{visible.vehicle.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.vehicle.sourceFinancialFactId)}>View exact vehicle source history</a>
      <p>Values were entered and reviewed by the customer. Ownership and current valuation have not been independently verified.</p></>}
    </Drawer>
    <Drawer title="Vehicle record history" open={history?.api === api} onClose={() => {setHistory(null);}} size={560} destroyOnHidden>
      {history?.api === api && <VehicleHistory key={history.record.vehicleRecordId} api={api} record={history.record} />}
    </Drawer>
  </section>;
}

function VehicleHistory({ api, record }: { api: AccessApi; record: ProductVehicleRecord }) {
  const sequence = useRef(0);const controller = useRef<AbortController | null>(null);
  const [page, setPage] = useState<ProductVehicleRecordHistory | null>(null);
  const [failure, setFailure] = useState(false);
  const [loading, setLoading] = useState(false);
  const [version, setVersion] = useState<ProductVehicleRecord | null>(null);
  const load = useCallback(async (before?: number, signal?: AbortSignal) => {
    const generation=++sequence.current;controller.current?.abort();const pending=new AbortController();controller.current=pending;
    const abort=()=>{pending.abort();};signal?.addEventListener('abort',abort,{once:true});
    const active=()=>!signal?.aborted && !pending.signal.aborted && generation===sequence.current;
    setLoading(true); setFailure(false); setPage(null); setVersion(null);
    try {
      const value = await getVehicleRecordHistory(api, record.vehicleRecordId, before, pending.signal);
      if (!Array.isArray(value.items) || value.items.length > 50 || value.items.some((item) => item.vehicleRecordId !== record.vehicleRecordId || item.customerId !== record.customerId ||
          !Number.isInteger(item.revision) || item.revision < 1 || item.currency !== record.currency || item.sourceProvenanceId !== record.sourceProvenanceId || item.documentVersionId !== record.documentVersionId || item.evidenceId !== record.evidenceId ||
          item.vehicle.sourceFinancialFactId !== record.vehicle.sourceFinancialFactId || item.vehicle.sourceRevision !== record.vehicle.sourceRevision)) throw new Error('Invalid history');
      if (active()) setPage(value);
    } catch { if (active()) setFailure(true); }
    finally {signal?.removeEventListener('abort',abort);if (active()) setLoading(false);}
  }, [api, record]);
  useEffect(() => {
    const initial = new AbortController(); const start = window.setTimeout(() => { void load(undefined, initial.signal); }, 0);
    return () => { window.clearTimeout(start); initial.abort();controller.current?.abort(); };
  }, [load]);
  return <section aria-label="Reviewed vehicle change history"><h2>{record.vehicle.name}</h2>
    <p>Each saved version retains its reviewed values and original source. This history does not verify registration, ownership or professional valuations.</p>
    {loading && <p role="status">Loading vehicle history…</p>}
    {failure && <Alert type="error" title="Vehicle history is unavailable." action={<Button onClick={() => { void load(); }}>Retry vehicle history</Button>} />}
    {page?.items.map((item) => <Card key={item.revision} size="small" style={{ marginBlock: 12 }}>
      <strong>Version {item.revision}</strong><p>{item.vehicle.name} · {item.vehicle.registrationNumber ?? 'Not supplied'} · {money(item.vehicle.currentValue,item.currency)} as of {item.vehicle.valueAsOfDate}</p>
      <p>Reviewed {item.recordedAt}</p><Button onClick={() => { setVersion(item); }}>View version {item.revision}</Button>
    </Card>)}
    {page?.hasMore && page.nextBeforeRevision !== null && <Button disabled={loading} onClick={() => { void load(page.nextBeforeRevision ?? undefined); }}>Earlier versions</Button>}
    {page && <Button disabled={loading} onClick={() => { void load(); }}>Latest versions</Button>}
    {version && <section aria-label={'Saved vehicle version ' + String(version.revision)}><h3>Saved version {version.revision}</h3><dl>
      <dt>Vehicle name</dt><dd>{version.vehicle.name}</dd><dt>Registration reference (optional)</dt><dd>{version.vehicle.registrationNumber ?? 'Not supplied'}</dd><dt>Vehicle type</dt><dd>{terms(version)}</dd>
      <dt>Currency</dt><dd>{version.currency}</dd><dt>Purchase value</dt><dd>{money(version.vehicle.purchaseValue,version.currency)}</dd>
      <dt>Current value</dt><dd>{money(version.vehicle.currentValue,version.currency)} as of {version.vehicle.valueAsOfDate}</dd>
      <dt>Source revision</dt><dd>{version.vehicle.sourceRevision}</dd></dl>
      <a href={'/finances?fact=' + encodeURIComponent(version.vehicle.sourceFinancialFactId)}>View exact vehicle source history</a></section>}
  </section>;
}
