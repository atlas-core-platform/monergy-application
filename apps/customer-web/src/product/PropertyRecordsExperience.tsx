import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createPropertyRecord, getPropertyRecords } from './productApi';
import type { ProductFinancialFact, ProductPropertyDraft, ProductPropertyRecord, ProductPropertyRecordList } from './productApi';

const numberFields = [ ['purchaseValue', 'Purchase value'], ['currentValue', 'Current value'] ] as const;
const dateFields = [ ['valueAsOfDate', 'Value as of'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; property: ProductPropertyDraft }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);
const terms = (record: ProductPropertyRecord) => record.property.propertyType;

export default function PropertyRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const scope = useRef({ api, canRead, canCreate });
  useLayoutEffect(() => { scope.current = { api, canRead, canCreate }; }, [api, canRead, canCreate]);
  const loadSequence = useRef(0);
  const activeLoad = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductPropertyRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<{ api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null } | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<ProductPropertyRecord | null>(null);
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductPropertyRecord } | null>(null);
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
      const list = await getPropertyRecords(api, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean') throw new Error('Invalid record list');
      if (active()) setLoaded({ api, list });
    } catch (error) {
      if (active()) { setLoaded(null); setFailure({ api, text: error instanceof AccessApiError && error.status === 403
        ? 'Your current access does not include property records and their source documents.' : 'Property records are unavailable. Try again.' }); }
    } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, canRead]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => { void load(controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); activeLoad.current?.abort(); };
  }, [load]);
  const current = editor?.api === api ? editor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'ASSET' && fact.assetType === 'PROPERTY');
  const valid = current && source && current.values.name?.trim() && current.values.location?.trim() && current.values.propertyType?.trim() &&
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
    const submission = current.submission ?? { operationId: current.operationId, property: {
      name: current.values.name ?? '', location: current.values.location ?? '', propertyType: current.values.propertyType ?? '',
      purchaseValue: Number(current.values.purchaseValue), currentValue: Number(current.values.currentValue),
      valueAsOfDate: current.values.valueAsOfDate ?? '',
      sourceFinancialFactId: source?.financialFactId ?? '', sourceRevision: source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = await createPropertyRecord(api, submission);
      if (record.operationId !== submission.operationId || record.property.sourceFinancialFactId !== submission.property.sourceFinancialFactId ||
          record.property.sourceRevision !== submission.property.sourceRevision || !record.propertyRecordId) throw new Error('Invalid confirmation');
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaved(record); setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, open: false, submission: null } : active); await load();
    } catch (error) {
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaveError(error instanceof AccessApiError && error.status === 400
        ? 'Check the required amounts, date, location and property type. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (error instanceof AccessApiError && error.status === 400) setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, submission: null } : active);
    } finally { setSaving(false); }
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view property records and their sources." />;
  return <section aria-label="Property records" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Property records</h2>
      {canCreate && <Button onClick={open} disabled={saving || (!current?.submission && facts.every((fact) => !(fact.factType === 'ASSET' && fact.assetType === 'PROPERTY')))}>
        {current?.submission ? 'Continue save confirmation' : 'Add property record'}</Button>}
    </Space>
    <p>Record the property details and values you have reviewed. Source figures stay separate. Ownership and valuations are customer reported; these records do not calculate total assets or net worth.</p>
    {saved && editor?.api === api && <Alert type="success" title={'Property record saved: ' + saved.property.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry property records</Button>} />}
    {loading && <p role="status">Loading property records…</p>}
    {list?.items.length === 0 && <p>No property records added yet. Review an property source figure in Documents, then add the property and values here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added property records." />}
    {list?.items.map((record) => <Card key={record.propertyRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.property.name}</strong><Tag>Customer reviewed</Tag><span>{record.property.location} · {terms(record)}</span></Space>
      <p>Current value: {money(record.property.currentValue, record.currency)} · as of {record.property.valueAsOfDate}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View property record: {record.property.name}</Button>
    </Card>)}
    <Drawer title="Add property record" open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      <p>Choose an exact reviewed property source. Enter a location summary, property type and both values explicitly. The source amount is not used to fill either value.</p>
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission)} style={{ border: 0, padding: 0 }}>
        <label htmlFor="property-source">Reviewed property source</label>
        <Select id="property-source" aria-label="Reviewed property source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed property source"
          options={facts.filter((fact) => fact.factType === 'ASSET' && fact.assetType === 'PROPERTY').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter purchase and current values separately.'} />}
        {(['name', 'location', 'propertyType'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'property-' + key}>{key === 'name' ? 'Property name' : key === 'location' ? 'Location summary' : 'Property type'}</label>
          <Input id={'property-' + key} maxLength={120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'property-' + key}>{label}</label>
          <Input id={'property-' + key} type="number" min={0}
            max={999999999999999}
            step={0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'property-' + key}>{label}</label>
          <Input id={'property-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
      </fieldset>
      <p>Saving records your review and the exact source revision. It does not verify ownership or legal title, ownership or current market value.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : 'Save reviewed property record'}</Button>
    </Drawer>
    <Drawer title="Property record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.property.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Location summary</dt><dd>{visible.property.location}</dd><dt>Property type</dt><dd>{terms(visible)}</dd>
        <dt>Currency</dt><dd>{visible.currency}</dd><dt>Purchase value</dt><dd>{money(visible.property.purchaseValue, visible.currency)}</dd>
        <dt>Current value</dt><dd>{money(visible.property.currentValue, visible.currency)} as of {visible.property.valueAsOfDate}</dd>
        <dt>Source revision</dt><dd>{visible.property.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.property.sourceFinancialFactId)}>View exact property source history</a>
      <p>Values were entered and reviewed by the customer. Ownership and current valuation have not been independently verified.</p></>}
    </Drawer>
  </section>;
}
