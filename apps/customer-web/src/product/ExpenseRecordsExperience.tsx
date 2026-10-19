import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createExpenseRecord, getExpenseRecords } from './productApi';
import type { ProductFinancialFact, ProductExpenseDraft, ProductExpenseRecord, ProductExpenseRecordList } from './productApi';

const numberFields = [ ['amount', 'Amount spent in this period'] ] as const;
const dateFields = [ ['periodStart', 'Period start'], ['periodEnd', 'Period end'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; expense: ProductExpenseDraft }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);

export default function ExpenseRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const scope = useRef({ api, canRead, canCreate });
  useLayoutEffect(() => { scope.current = { api, canRead, canCreate }; }, [api, canRead, canCreate]);
  const loadSequence = useRef(0);
  const activeLoad = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductExpenseRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<{ api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null } | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<ProductExpenseRecord | null>(null);
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductExpenseRecord } | null>(null);
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
      const list = await getExpenseRecords(api, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean' || list.items.some(item=>item.verificationStatus !== 'CUSTOMER_REVIEWED')) throw new Error('Invalid record list');
      if (active()) setLoaded({ api, list });
    } catch (error) {
      if (active()) { setLoaded(null); setFailure({ api, text: error instanceof AccessApiError && error.status === 403
        ? 'Your current access does not include expense records and their source documents.' : 'Expense records are unavailable. Try again.' }); }
    } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, canRead]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => { void load(controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); activeLoad.current?.abort(); };
  }, [load]);
  const current = editor?.api === api ? editor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'EXPENSE');
  const valid = current && source && current.values.name?.trim() && current.values.category?.trim() &&
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
    const submission = current.submission ?? { operationId: current.operationId, expense: {
      name: current.values.name ?? '', category: current.values.category ?? '',
      amount: Number(current.values.amount),
      periodStart:current.values.periodStart ?? '',periodEnd:current.values.periodEnd ?? '',
      sourceFinancialFactId: source?.financialFactId ?? '', sourceRevision: source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = await createExpenseRecord(api, submission);
      if (record.operationId !== submission.operationId || record.expense.sourceFinancialFactId !== submission.expense.sourceFinancialFactId ||
          record.expense.sourceRevision !== submission.expense.sourceRevision || !record.expenseRecordId || record.verificationStatus !== 'CUSTOMER_REVIEWED') throw new Error('Invalid confirmation');
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaved(record); setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, open: false, submission: null } : active); await load();
    } catch (error) {
      if (scope.current.api !== api || !scope.current.canRead || !scope.current.canCreate) return;
      setSaveError(error instanceof AccessApiError && error.status === 400
        ? 'Check the amount, reporting period and required expense details. The period must end by today. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (error instanceof AccessApiError && error.status === 400) setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, submission: null } : active);
    } finally { setSaving(false); }
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view expense records and their sources." />;
  return <section aria-label="Expense records" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Expense records</h2>
      {canCreate && <Button onClick={open} disabled={saving || (!current?.submission && facts.every((fact) => fact.factType !== 'EXPENSE'))}>
        {current?.submission ? 'Continue save confirmation' : 'Add expense record'}</Button>}
    </Space>
    <p>Record the total spent in a stated past reporting period and the category you reviewed. Source figures stay separate. These customer-reviewed records do not calculate spending totals or infer recurring expenses.</p>
    {saved && editor?.api === api && <Alert type="success" title={'Expense record saved: ' + saved.expense.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry expense records</Button>} />}
    {loading && <p role="status">Loading expense records…</p>}
    {list?.items.length === 0 && <p>No expense records added yet. Review an expense source figure in Documents, then add its expense details and reporting period here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added expense records." />}
    {list?.items.map((record) => <Card key={record.expenseRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.expense.name}</strong><Tag>Customer reviewed</Tag><span>{record.expense.category}</span></Space>
      <p>Period total: {money(record.expense.amount, record.currency)} · {record.expense.periodStart} to {record.expense.periodEnd}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View expense record: {record.expense.name}</Button>
    </Card>)}
    <Drawer title="Add expense record" open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      <p>Choose an exact reviewed expense source figure, then enter the expense category and total for its reporting period. The source amount does not fill the form.</p>
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission)} style={{ border: 0, padding: 0 }}>
        <label htmlFor="expense-source">Reviewed expense source</label>
        <Select id="expense-source" aria-label="Reviewed expense source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed expense source"
          options={facts.filter((fact) => fact.factType === 'EXPENSE').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate + ' · ' + fact.source.sourceName }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter the total spent for this period separately.'} />}
        {(['name', 'category'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'expense-' + key}>{key === 'name' ? 'Expense name' : 'Expense category'}</label>
          <Input id={'expense-' + key} maxLength={120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'expense-' + key}>{label}</label>
          <Input id={'expense-' + key} type="number" min={0}
            max={999999999999.99}
            step={0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'expense-' + key}>{label}</label>
          <Input id={'expense-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
      </fieldset>
      <p>Saving records your review and the exact source revision. It does not verify payment with a merchant or bank. Overlapping records are not reconciled or added together.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : 'Save reviewed expense record'}</Button>
    </Drawer>
    <Drawer title="Expense record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.expense.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Expense category</dt><dd>{visible.expense.category}</dd>
        <dt>Period total</dt><dd>{money(visible.expense.amount,visible.currency)}</dd><dt>Currency</dt><dd>{visible.currency}</dd>
        <dt>Reporting period</dt><dd>{visible.expense.periodStart} to {visible.expense.periodEnd}</dd>
        <dt>Review status</dt><dd>Customer reviewed</dd>
        <dt>Source revision</dt><dd>{visible.expense.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.expense.sourceFinancialFactId)}>View exact expense source history</a>
      <p>Expense category and period total were entered and reviewed by the customer. They have not been independently verified with a merchant or bank.</p></>}
    </Drawer>
  </section>;
}
