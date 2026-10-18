import { useCallback, useEffect, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createLoanRecord, getLoanRecords } from './productApi';
import type { ProductFinancialFact, ProductLoanDraft, ProductLoanRecord, ProductLoanRecordList } from './productApi';

const loanTypes = [ { value: 'HOME', label: 'Home loan' }, { value: 'PERSONAL', label: 'Personal loan' },
  { value: 'VEHICLE', label: 'Vehicle loan' }, { value: 'EDUCATION', label: 'Education loan' },
  { value: 'BUSINESS', label: 'Business loan' }, { value: 'OTHER', label: 'Other loan' } ];
const numberFields = [ ['sanctionAmount', 'Sanction amount'], ['outstanding', 'Outstanding balance'],
  ['interestRatePercent', 'Annual interest rate (%)'], ['emi', 'Monthly EMI'], ['tenureMonths', 'Tenure (months)'] ] as const;
const dateFields = [ ['startDate', 'Start date'], ['endDate', 'End date'], ['balanceAsOfDate', 'Balance as of'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; loan: ProductLoanDraft }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);
const terms = (record: ProductLoanRecord) => loanTypes.find((type) => type.value === record.loan.loanType)?.label ?? 'Loan';

export default function LoanRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductLoanRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<{ api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null } | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<ProductLoanRecord | null>(null);
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductLoanRecord } | null>(null);
  const load = useCallback(async (signal?: AbortSignal) => {
    if (!canRead) return;
    setLoading(true); setFailure(null);
    try {
      const list = await getLoanRecords(api, signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean') throw new Error('Invalid record list');
      if (!signal?.aborted) setLoaded({ api, list });
    } catch (error) {
      if (!signal?.aborted) { setLoaded(null); setFailure({ api, text: error instanceof AccessApiError && error.status === 403
        ? 'Your current access does not include loan records and their source documents.' : 'Loan records are unavailable. Try again.' }); }
    } finally { if (!signal?.aborted) setLoading(false); }
  }, [api, canRead]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => { void load(controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); };
  }, [load]);
  const current = editor?.api === api ? editor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'LOAN');
  const valid = current && source && current.values.name?.trim() && current.values.lender?.trim() && current.values.loanType &&
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
    if (!current || !canCreate || saving || (!current.submission && !valid)) return;
    const submission = current.submission ?? { operationId: current.operationId, loan: {
      name: current.values.name ?? '', lender: current.values.lender ?? '', loanType: current.values.loanType ?? '',
      sanctionAmount: Number(current.values.sanctionAmount), outstanding: Number(current.values.outstanding),
      interestRatePercent: Number(current.values.interestRatePercent), emi: Number(current.values.emi), tenureMonths: Number(current.values.tenureMonths),
      startDate: current.values.startDate ?? '', endDate: current.values.endDate ?? '', balanceAsOfDate: current.values.balanceAsOfDate ?? '',
      sourceFinancialFactId: source?.financialFactId ?? '', sourceRevision: source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = await createLoanRecord(api, submission);
      if (record.operationId !== submission.operationId || record.loan.sourceFinancialFactId !== submission.loan.sourceFinancialFactId ||
          record.loan.sourceRevision !== submission.loan.sourceRevision || !record.loanRecordId) throw new Error('Invalid confirmation');
      setSaved(record); setEditor({ ...current, open: false, submission: null }); await load();
    } catch (error) {
      setSaveError(error instanceof AccessApiError && error.status === 400
        ? 'Check the required amounts, dates and loan terms. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (error instanceof AccessApiError && error.status === 400) setEditor({ ...current, submission: null });
    } finally { setSaving(false); }
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view loan records and their sources." />;
  return <section aria-label="Loan records" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Loan records</h2>
      {canCreate && <Button onClick={open} disabled={facts.every((fact) => fact.factType !== 'LOAN')}>
        {current?.submission ? 'Continue save confirmation' : 'Add loan record'}</Button>}
    </Space>
    <p>Record the terms you have reviewed. Document figures remain separate from your loan record. Ownership and lender balances are not independently verified.</p>
    {saved && editor?.api === api && <Alert type="success" title={'Loan record saved: ' + saved.loan.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry loan records</Button>} />}
    {loading && <p role="status">Loading loan records…</p>}
    {list?.items.length === 0 && <p>No loan records added yet. Review a loan source figure in Documents, then add the loan terms here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added loan records." />}
    {list?.items.map((record) => <Card key={record.loanRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.loan.name}</strong><Tag>Customer reviewed</Tag><span>{record.loan.lender} · {terms(record)}</span></Space>
      <p>Outstanding balance: {money(record.loan.outstanding, record.currency)} · as of {record.loan.balanceAsOfDate}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View loan record: {record.loan.name}</Button>
    </Card>)}
    <Drawer title="Add loan record" open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      <p>Choose an exact reviewed loan source and enter each term. No balance or repayment term is filled in from the source figure.</p>
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission)} style={{ border: 0, padding: 0 }}>
        <label htmlFor="loan-source">Reviewed loan source</label>
        <Select id="loan-source" aria-label="Reviewed loan source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed loan source"
          options={facts.filter((fact) => fact.factType === 'LOAN').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter the outstanding balance separately.'} />}
        {(['name', 'lender'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'loan-' + key}>{key === 'name' ? 'Loan name' : 'Lender'}</label>
          <Input id={'loan-' + key} maxLength={120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        <label htmlFor="loan-type">Loan type</label><Select id="loan-type" aria-label="Loan type" style={{ width: '100%' }} options={loanTypes}
          disabled={saving || Boolean(current?.submission)} value={current?.values.loanType} onChange={(value: string) => { change('loanType', value); }} />
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'loan-' + key}>{label}</label>
          <Input id={'loan-' + key} type="number" min={key === 'tenureMonths' ? 1 : key === 'sanctionAmount' ? 0.01 : 0}
            max={key === 'interestRatePercent' ? 100 : key === 'tenureMonths' ? 1200 : 999999999999.99}
            step={key === 'tenureMonths' ? 1 : key === 'interestRatePercent' ? 0.0001 : 0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'loan-' + key}>{label}</label>
          <Input id={'loan-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
      </fieldset>
      <p>Saving records your review of these terms and the exact source revision. It does not verify the loan with the lender.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : 'Save reviewed loan record'}</Button>
    </Drawer>
    <Drawer title="Loan record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.loan.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Lender</dt><dd>{visible.loan.lender}</dd><dt>Loan type</dt><dd>{terms(visible)}</dd>
        <dt>Currency</dt><dd>{visible.currency}</dd><dt>Sanction amount</dt><dd>{money(visible.loan.sanctionAmount, visible.currency)}</dd>
        <dt>Outstanding balance</dt><dd>{money(visible.loan.outstanding, visible.currency)} as of {visible.loan.balanceAsOfDate}</dd>
        <dt>Annual interest rate</dt><dd>{visible.loan.interestRatePercent}%</dd><dt>Monthly EMI</dt><dd>{money(visible.loan.emi, visible.currency)}</dd>
        <dt>Tenure</dt><dd>{visible.loan.tenureMonths} months</dd><dt>Start date</dt><dd>{visible.loan.startDate}</dd><dt>End date</dt><dd>{visible.loan.endDate}</dd>
        <dt>Source revision</dt><dd>{visible.loan.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.loan.sourceFinancialFactId)}>View exact loan source history</a>
      <p>Terms were entered and reviewed by the customer. Ownership and current lender balances have not been independently verified.</p></>}
    </Drawer>
  </section>;
}
