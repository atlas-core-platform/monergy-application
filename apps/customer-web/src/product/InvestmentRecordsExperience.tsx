import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createInvestmentRecord, getInvestmentRecords } from './productApi';
import type { ProductFinancialFact, ProductInvestmentDraft, ProductInvestmentRecord, ProductInvestmentRecordList } from './productApi';

const numberFields = [ ['purchaseValue', 'Purchase value'], ['currentValue', 'Current value'] ] as const;
const dateFields = [ ['valueAsOfDate', 'Value as of'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; investment: ProductInvestmentDraft }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);
const terms = (record: ProductInvestmentRecord) => record.investment.investmentType;

export default function InvestmentRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const scope = useRef({ api, canRead, canCreate });
  useLayoutEffect(() => { scope.current = { api, canRead, canCreate }; }, [api, canRead, canCreate]);
  const loadSequence = useRef(0);
  const activeLoad = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductInvestmentRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<{ api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null } | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<ProductInvestmentRecord | null>(null);
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductInvestmentRecord } | null>(null);
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
      const list = await getInvestmentRecords(api, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean') throw new Error('Invalid record list');
      if (active()) setLoaded({ api, list });
    } catch (error) {
      if (active()) { setLoaded(null); setFailure({ api, text: error instanceof AccessApiError && error.status === 403
        ? 'Your current access does not include investment records and their source documents.' : 'Investment records are unavailable. Try again.' }); }
    } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, canRead]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => { void load(controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); activeLoad.current?.abort(); };
  }, [load]);
  const current = editor?.api === api ? editor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'INVESTMENT');
  const valid = current && source && current.values.name?.trim() && current.values.institution?.trim() && current.values.investmentType?.trim() &&
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
    const submission = current.submission ?? { operationId: current.operationId, investment: {
      name: current.values.name ?? '', institution: current.values.institution ?? '', investmentType: current.values.investmentType ?? '',
      purchaseValue: Number(current.values.purchaseValue), currentValue: Number(current.values.currentValue),
      valueAsOfDate: current.values.valueAsOfDate ?? '',
      sourceFinancialFactId: source?.financialFactId ?? '', sourceRevision: source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = await createInvestmentRecord(api, submission);
      if (record.operationId !== submission.operationId || record.investment.sourceFinancialFactId !== submission.investment.sourceFinancialFactId ||
          record.investment.sourceRevision !== submission.investment.sourceRevision || !record.investmentRecordId) throw new Error('Invalid confirmation');
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaved(record); setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, open: false, submission: null } : active); await load();
    } catch (error) {
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaveError(error instanceof AccessApiError && error.status === 400
        ? 'Check the required amounts, date, institution and investment type. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (error instanceof AccessApiError && error.status === 400) setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, submission: null } : active);
    } finally { setSaving(false); }
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view investment records and their sources." />;
  return <section aria-label="Investment records" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Investment records</h2>
      {canCreate && <Button onClick={open} disabled={saving || (!current?.submission && facts.every((fact) => fact.factType !== 'INVESTMENT'))}>
        {current?.submission ? 'Continue save confirmation' : 'Add investment record'}</Button>}
    </Space>
    <p>Record the holding and values you have reviewed. Source figures stay separate. Ownership and valuations are customer reported; these records do not calculate a portfolio total or net worth.</p>
    {saved && editor?.api === api && <Alert type="success" title={'Investment record saved: ' + saved.investment.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry investment records</Button>} />}
    {loading && <p role="status">Loading investment records…</p>}
    {list?.items.length === 0 && <p>No investment records added yet. Review an investment source figure in Documents, then add the holding and values here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added investment records." />}
    {list?.items.map((record) => <Card key={record.investmentRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.investment.name}</strong><Tag>Customer reviewed</Tag><span>{record.investment.institution} · {terms(record)}</span></Space>
      <p>Current value: {money(record.investment.currentValue, record.currency)} · as of {record.investment.valueAsOfDate}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View investment record: {record.investment.name}</Button>
    </Card>)}
    <Drawer title="Add investment record" open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      <p>Choose an exact reviewed investment source. Enter the institution, type and both values explicitly. The source amount is not used to fill either value.</p>
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission)} style={{ border: 0, padding: 0 }}>
        <label htmlFor="investment-source">Reviewed investment source</label>
        <Select id="investment-source" aria-label="Reviewed investment source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed investment source"
          options={facts.filter((fact) => fact.factType === 'INVESTMENT').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter purchase and current values separately.'} />}
        {(['name', 'institution', 'investmentType'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'investment-' + key}>{key === 'name' ? 'Investment name' : key === 'institution' ? 'Institution' : 'Investment type'}</label>
          <Input id={'investment-' + key} maxLength={120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'investment-' + key}>{label}</label>
          <Input id={'investment-' + key} type="number" min={0}
            max={999999999999999}
            step={0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'investment-' + key}>{label}</label>
          <Input id={'investment-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
      </fieldset>
      <p>Saving records your review and the exact source revision. It does not verify ownership or current market value with the institution.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : 'Save reviewed investment record'}</Button>
    </Drawer>
    <Drawer title="Investment record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.investment.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Institution</dt><dd>{visible.investment.institution}</dd><dt>Investment type</dt><dd>{terms(visible)}</dd>
        <dt>Currency</dt><dd>{visible.currency}</dd><dt>Purchase value</dt><dd>{money(visible.investment.purchaseValue, visible.currency)}</dd>
        <dt>Current value</dt><dd>{money(visible.investment.currentValue, visible.currency)} as of {visible.investment.valueAsOfDate}</dd>
        <dt>Source revision</dt><dd>{visible.investment.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.investment.sourceFinancialFactId)}>View exact investment source history</a>
      <p>Values were entered and reviewed by the customer. Ownership and current valuation have not been independently verified.</p></>}
    </Drawer>
  </section>;
}
