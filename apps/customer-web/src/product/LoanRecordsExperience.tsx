import { useCallback, useEffect, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi} from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { createLoanRecord, getLoanRecords, getLoanRecordHistory, reviseLoanRecord, reviewLoanRecordStatus } from './productApi';
import type { ProductFinancialFact, ProductLoanDraft, ProductLoanRecord, ProductLoanRecordList, ProductLoanRecordHistory, ProductLoanStatusReview } from './productApi';

const loanTypes = [ { value: 'HOME', label: 'Home loan' }, { value: 'PERSONAL', label: 'Personal loan' },
  { value: 'VEHICLE', label: 'Vehicle loan' }, { value: 'EDUCATION', label: 'Education loan' },
  { value: 'BUSINESS', label: 'Business loan' }, { value: 'OTHER', label: 'Other loan' } ];
const numberFields = [ ['sanctionAmount', 'Sanction amount'], ['outstanding', 'Outstanding balance'],
  ['interestRatePercent', 'Annual interest rate (%)'], ['emi', 'Monthly EMI'], ['tenureMonths', 'Tenure (months)'] ] as const;
const dateFields = [ ['startDate', 'Start date'], ['endDate', 'End date'], ['balanceAsOfDate', 'Balance as of'] ] as const;
type FormValues = Record<string, string>;
interface Submission { operationId: string; loan: ProductLoanDraft; expectedRevision?: number }
interface Editor { api: AccessApi; open: boolean; values: FormValues; operationId: string; submission: Submission | null; original: ProductLoanRecord | null; conflict?: boolean }
interface StatusEditor { api: AccessApi; open: boolean; record: ProductLoanRecord; status?: 'ACTIVE' | 'CLOSED'; asOfDate: string; operationId: string; submission: ProductLoanStatusReview | null; conflict?: boolean }
const money = (value: number, currency: string) => new Intl.NumberFormat('en', { style: 'currency', currency }).format(value);
const terms = (record: ProductLoanRecord) => loanTypes.find((type) => type.value === record.loan.loanType)?.label ?? 'Loan';
const statusLabel = (record: ProductLoanRecord) => record.lifecycleStatus === 'ACTIVE' ? 'Active — customer reported' : record.lifecycleStatus === 'CLOSED' ? 'Closed — customer reported' : 'Status not reviewed';

export default function LoanRecordsExperience({ api, facts, canRead, canCreate }: {
  api: AccessApi; facts: ProductFinancialFact[]; canRead: boolean; canCreate: boolean;
}) {
  const [loaded, setLoaded] = useState<{ api: AccessApi; list: ProductLoanRecordList } | null>(null);
  const [failure, setFailure] = useState<{ api: AccessApi; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [saved, setSaved] = useState<{ api: AccessApi; record: ProductLoanRecord; statusReview: boolean } | null>(null);
  const [statusEditor, setStatusEditor] = useState<StatusEditor | null>(null);
  const [statusError, setStatusError] = useState('');
  const [selected, setSelected] = useState<{ api: AccessApi; record: ProductLoanRecord } | null>(null);
  const [history, setHistory] = useState<{ api: AccessApi; record: ProductLoanRecord } | null>(null);
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
  const currentStatus = statusEditor?.api === api ? statusEditor : null;
  const list = loaded?.api === api && canRead ? loaded.list : null;
  const source = facts.find((fact) => fact.financialFactId === current?.values.sourceFinancialFactId && fact.revision === Number(current.values.sourceRevision) && fact.factType === 'LOAN');
  const valid = current && (current.original ?? source) && !current.conflict && current.values.name?.trim() && current.values.lender?.trim() && current.values.loanType &&
    numberFields.every(([key]) => current.values[key] !== undefined && current.values[key] !== '' && Number.isFinite(Number(current.values[key]))) &&
    dateFields.every(([key]) => current.values[key]);
  const open = () => {
    setSaveError(''); setSaved(null);
    if (current?.submission) setEditor({ ...current, open: true });
    else setEditor({ api, open: true, values: {}, operationId: crypto.randomUUID().replaceAll('-', ''), submission: null, original: null });
  };
  const edit = (record: ProductLoanRecord) => {
    if (current?.submission || saving) { if (current) setEditor({ ...current, open: true }); return; }
    setSelected(null); setSaved(null); setSaveError('');
    setEditor({ api, open: true, original: record, values: Object.fromEntries(Object.entries(record.loan).map(([key,value]) => [key,String(value)])),
      operationId: crypto.randomUUID().replaceAll('-', ''), submission: null });
  };
  const change = (key: string, value: string) => {
    if (current && !current.submission && !saving) setEditor({ ...current, values: { ...current.values, [key]: value } });
  };
  const save = async () => {
    if (!current || !canCreate || saving || currentStatus?.submission || (!current.submission && !valid)) return;
    const submission = current.submission ?? { operationId: current.operationId, ...(current.original ? { expectedRevision: current.original.revision } : {}), loan: {
      name: current.values.name ?? '', lender: current.values.lender ?? '', loanType: current.values.loanType ?? '',
      sanctionAmount: Number(current.values.sanctionAmount), outstanding: Number(current.values.outstanding),
      interestRatePercent: Number(current.values.interestRatePercent), emi: Number(current.values.emi), tenureMonths: Number(current.values.tenureMonths),
      startDate: current.values.startDate ?? '', endDate: current.values.endDate ?? '', balanceAsOfDate: current.values.balanceAsOfDate ?? '',
      sourceFinancialFactId: current.original?.loan.sourceFinancialFactId ?? source?.financialFactId ?? '', sourceRevision: current.original?.loan.sourceRevision ?? source?.revision ?? 0,
    } };
    setEditor({ ...current, submission }); setSaving(true); setSaveError('');
    try {
      const record = current.original && submission.expectedRevision !== undefined
        ? await reviseLoanRecord(api, current.original.loanRecordId, { ...submission, expectedRevision: submission.expectedRevision })
        : await createLoanRecord(api, submission);
      if (record.operationId !== submission.operationId || record.loan.sourceFinancialFactId !== submission.loan.sourceFinancialFactId ||
          record.loan.sourceRevision !== submission.loan.sourceRevision || !record.loanRecordId || current.original &&
          (record.loanRecordId !== current.original.loanRecordId || record.revision !== (submission.expectedRevision ?? 0) + 1)) throw new Error('Invalid confirmation');
      setSaved({api,record,statusReview:false}); setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, open: false, submission: null } : active); await load();
    } catch (error) {
      const conflict = error instanceof AccessApiError && error.code === 'LOAN_REVISION_CONFLICT';
      setSaveError(conflict ? 'A newer version has been saved. Close this form, refresh loan records and review that version before editing again.' : error instanceof AccessApiError && error.status === 400
        ? 'Check the required amounts, dates and loan terms. Review and edit the form before saving again.'
        : 'The save has not been confirmed. Retry confirmation to check the same request before creating another record.');
      if (conflict || error instanceof AccessApiError && error.status === 400) setEditor((active) => active?.api === api && active.operationId === current.operationId ? { ...current, submission: null, conflict } : active);
    } finally { setSaving(false); }
  };
  const openStatus = (record: ProductLoanRecord) => {
    if (saving || current?.submission) return;
    setSaved(null);setStatusError('');
    if(current) setEditor({...current,open:false});
    if (currentStatus?.submission) setStatusEditor({...currentStatus,open:true});
    else setStatusEditor({api,record,open:true,asOfDate:'',operationId:crypto.randomUUID().replaceAll('-',''),submission:null});
  };
  const saveStatus = async () => {
    if (!currentStatus || !canCreate || saving || current?.submission || currentStatus.conflict || (!currentStatus.submission && (!currentStatus.status || !currentStatus.asOfDate))) return;
    let submission = currentStatus.submission;
    if(!submission) {
      if(!currentStatus.status) return;
      submission={operationId:currentStatus.operationId,expectedRevision:currentStatus.record.revision,status:currentStatus.status,asOfDate:currentStatus.asOfDate};
    }
    setStatusEditor({...currentStatus,submission});setSaving(true);setStatusError('');
    try {
      const record=await reviewLoanRecordStatus(api,currentStatus.record.loanRecordId,submission);
      if(record.loanRecordId!==currentStatus.record.loanRecordId || record.operationId!==submission.operationId || record.revision!==submission.expectedRevision+1 || record.lifecycleStatus!==submission.status || record.lifecycleAsOfDate!==submission.asOfDate)
        throw new Error('Invalid confirmation');
      setSaved({api,record,statusReview:true});setStatusEditor(active=>active?.api===api && active.operationId===currentStatus.operationId?{...currentStatus,open:false,submission:null}:active);await load();
    } catch(error) {
      const conflict=error instanceof AccessApiError && error.code==='LOAN_REVISION_CONFLICT';
      setStatusError(conflict?'A newer version has been saved. Close this form, refresh loan records and review that version before changing status.':error instanceof AccessApiError && error.status===400
        ?'Check the status and date. The date must follow the loan start, recorded balance and latest reviewed status, and cannot be in the future.'
        :'The status save has not been confirmed. Retry confirmation using the same request.');
      if(conflict || error instanceof AccessApiError && error.status===400) setStatusEditor(active=>active?.api===api && active.operationId===currentStatus.operationId?{...currentStatus,submission:null,conflict}:active);
    } finally {setSaving(false);}
  };
  const visible = selected?.api === api && canRead ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Document access is required to view loan records and their sources." />;
  return <section aria-label="Loan records" style={{ marginBlock: 24 }}>
    <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}><h2>Loan records</h2>
      {canCreate && <Button onClick={open} disabled={Boolean(currentStatus?.submission) || saving || !current?.submission && facts.every((fact) => fact.factType !== 'LOAN')}>
        {current?.submission ? 'Continue save confirmation' : 'Add loan record'}</Button>}
    </Space>
    {canCreate && currentStatus?.submission && <Button onClick={()=>{setStatusEditor({...currentStatus,open:true});}}>Continue loan status confirmation</Button>}
    <Button onClick={() => { void load(); }} disabled={loading}>Refresh loan records</Button>
    <p>Record the terms you have reviewed. Document figures remain separate from your loan record. Ownership and lender balances are not independently verified.</p>
    {saved?.api === api && <Alert type="success" title={(saved.statusReview?'Loan status saved: ':'Loan record saved: ') + saved.record.loan.name} />}
    {failure?.api === api && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry loan records</Button>} />}
    {loading && <p role="status">Loading loan records…</p>}
    {list?.items.length === 0 && <p>No loan records added yet. Review a loan source figure in Documents, then add the loan terms here.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added loan records." />}
    {list?.items.map((record) => <Card key={record.loanRecordId} size="small" style={{ marginBlock: 12 }}>
      <Space wrap><strong>{record.loan.name}</strong><Tag>Customer reviewed</Tag><Tag>{statusLabel(record)}</Tag><span>{record.loan.lender} · {terms(record)}</span></Space>
      {record.lifecycleAsOfDate && <p>Loan status as of {record.lifecycleAsOfDate}</p>}
      <p>{record.lifecycleStatus==='CLOSED'?'Recorded balance: ':'Outstanding balance: '}{money(record.loan.outstanding, record.currency)} · as of {record.loan.balanceAsOfDate}</p>
      <Button onClick={() => { setSelected({ api, record }); }}>View loan record: {record.loan.name}</Button>
      {canCreate && <Button disabled={saving || Boolean(current?.submission) || Boolean(currentStatus?.submission)} onClick={() => { edit(record); }}>Edit loan record: {record.loan.name}</Button>}
      {canCreate && <Button disabled={saving || Boolean(current?.submission) || Boolean(currentStatus?.submission)} onClick={()=>{openStatus(record);}}>Review loan status: {record.loan.name}</Button>}
      <Button onClick={() => { setHistory({ api, record }); }}>View change history: {record.loan.name}</Button>
    </Card>)}
    <Drawer title={current?.original ? 'Edit loan record' : 'Add loan record'} open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }}
      size={560} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
      <p>{current?.original ? 'Review the saved terms before confirming your corrections. The exact original source and currency remain attached; prior versions are retained.' : 'Choose an exact reviewed loan source and enter each term. No balance or repayment term is filled in from the source figure.'}</p>
      {current?.original && <Alert type="info" title={'Editing version ' + String(current.original.revision) + ' · ' + current.original.currency + ' · Source revision ' + String(current.original.loan.sourceRevision)} />}
      {current?.original?.lifecycleStatus==='CLOSED' && <p>This record is reported closed as of {current.original.lifecycleAsOfDate}. A balance dated after closure requires a new status review.</p>}
      {saveError && <Alert type="error" title={saveError} />}
      <fieldset disabled={saving || Boolean(current?.submission) || current?.conflict} style={{ border: 0, padding: 0 }}>
        {!current?.original && <><label htmlFor="loan-source">Reviewed loan source</label>
        <Select id="loan-source" aria-label="Reviewed loan source" style={{ width: '100%', marginBottom: 16 }}
          disabled={saving || Boolean(current?.submission)} value={source ? current?.values.sourceSelection : undefined} placeholder="Choose reviewed loan source"
          options={facts.filter((fact) => fact.factType === 'LOAN').map((fact) => ({ value: fact.financialFactId + '@' + String(fact.revision),
            label: fact.label + ' · ' + money(fact.value, fact.currency) + ' · ' + fact.effectiveDate }))}
          onChange={(value: string) => { const chosen = facts.find((fact) => fact.financialFactId + '@' + String(fact.revision) === value); if (current && chosen && !current.submission) setEditor({ ...current, values: { ...current.values, sourceSelection: value, sourceFinancialFactId: chosen.financialFactId, sourceRevision: String(chosen.revision) } }); }} />
        {current?.values.sourceFinancialFactId && !source && <Alert type="warning" title="The source revision has changed. Select it again and review the terms." />}
        {source && <Alert type="info" title={'Source figure: ' + source.label + ' · ' + money(source.value, source.currency)}
          description={'Currency: ' + source.currency + ' · Source revision ' + String(source.revision) + '. Enter the outstanding balance separately.'} />}</>}
        {(['name', 'lender'] as const).map((key) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'loan-' + key}>{key === 'name' ? 'Loan name' : 'Lender'}</label>
          <Input id={'loan-' + key} maxLength={120} value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        <label htmlFor="loan-type">Loan type</label><Select id="loan-type" aria-label="Loan type" style={{ width: '100%' }} options={loanTypes}
          disabled={saving || Boolean(current?.submission) || current?.conflict} value={current?.values.loanType} onChange={(value: string) => { change('loanType', value); }} />
        {numberFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'loan-' + key}>{label}</label>
          <Input id={'loan-' + key} type="number" min={key === 'tenureMonths' ? 1 : key === 'sanctionAmount' ? 0.01 : 0}
            max={key === 'interestRatePercent' ? 100 : key === 'tenureMonths' ? 1200 : 999999999999.99}
            step={key === 'tenureMonths' ? 1 : key === 'interestRatePercent' ? 0.0001 : 0.01}
            value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
        {dateFields.map(([key, label]) => <div key={key} style={{ marginBlock: 14 }}><label htmlFor={'loan-' + key}>{label}</label>
          <Input id={'loan-' + key} type="date" value={current?.values[key] ?? ''} onChange={(event) => { change(key, event.target.value); }} /></div>)}
      </fieldset>
      <p>Saving records your review of these terms and the exact source revision. It does not verify the loan with the lender.</p>
      <Button type="primary" loading={saving} disabled={!canCreate || Boolean(currentStatus?.submission) || (!current?.submission && !valid)} onClick={() => { void save(); }}>
        {current?.submission ? 'Retry save confirmation' : current?.original ? 'Save reviewed corrections' : 'Save reviewed loan record'}</Button>
    </Drawer>
    <Drawer title="Review loan status" open={Boolean(currentStatus?.open && canCreate)} onClose={()=>{if(currentStatus)setStatusEditor({...currentStatus,open:false});}}
      size={480} closable={!saving} mask={{closable:!saving}} destroyOnHidden>
      {currentStatus && <><h2>{currentStatus.record.loan.name}</h2><p>{statusLabel(currentStatus.record)} · Record version {currentStatus.record.revision}</p>
        <p>Report the status you have reviewed and its date. Marking a record closed does not verify repayment or clear the recorded balance, EMI or source documents.</p>
        <p>Recorded balance: {money(currentStatus.record.loan.outstanding,currentStatus.record.currency)} as of {currentStatus.record.loan.balanceAsOfDate}</p>
        {statusError && <Alert type="error" title={statusError} />}
        <label htmlFor="loan-lifecycle-status">Loan status</label><Select id="loan-lifecycle-status" aria-label="Loan status" style={{width:'100%',marginBlock:12}}
          disabled={saving || Boolean(currentStatus.submission) || Boolean(currentStatus.conflict)} value={currentStatus.status}
          options={[{value:'ACTIVE',label:currentStatus.record.lifecycleStatus==='CLOSED'?'Active — reopen record':'Active'},{value:'CLOSED',label:'Closed'}]}
          onChange={(status:'ACTIVE'|'CLOSED')=>{setStatusEditor({...currentStatus,status});}} />
        <label htmlFor="loan-lifecycle-date">Status as of</label><Input id="loan-lifecycle-date" type="date" style={{marginBlock:12}} value={currentStatus.asOfDate}
          disabled={saving || Boolean(currentStatus.submission) || Boolean(currentStatus.conflict)} onChange={event=>{setStatusEditor({...currentStatus,asOfDate:event.target.value});}} />
        {currentStatus.status==='ACTIVE' && currentStatus.record.lifecycleStatus==='CLOSED' && <Alert type="info" title="This will reopen the record as customer reported. The earlier closed version stays in history." />}
        <Button type="primary" loading={saving} disabled={!canCreate || Boolean(currentStatus.conflict) || !currentStatus.submission && (!currentStatus.status || !currentStatus.asOfDate)} onClick={()=>{void saveStatus();}}>
          {currentStatus.submission?'Retry status confirmation':'Save reviewed loan status'}</Button></>}
    </Drawer>
    <Drawer title="Loan record details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={560} destroyOnHidden>
      {visible && <><h2>{visible.loan.name}</h2><Tag>Customer reviewed</Tag><dl>
        <dt>Customer-reported loan status</dt><dd>{statusLabel(visible)}{visible.lifecycleAsOfDate?' as of '+visible.lifecycleAsOfDate:''}</dd>
        <dt>Lender</dt><dd>{visible.loan.lender}</dd><dt>Loan type</dt><dd>{terms(visible)}</dd>
        <dt>Currency</dt><dd>{visible.currency}</dd><dt>Sanction amount</dt><dd>{money(visible.loan.sanctionAmount, visible.currency)}</dd>
        <dt>Outstanding balance</dt><dd>{money(visible.loan.outstanding, visible.currency)} as of {visible.loan.balanceAsOfDate}</dd>
        <dt>Annual interest rate</dt><dd>{visible.loan.interestRatePercent}%</dd><dt>Monthly EMI</dt><dd>{money(visible.loan.emi, visible.currency)}</dd>
        <dt>Tenure</dt><dd>{visible.loan.tenureMonths} months</dd><dt>Start date</dt><dd>{visible.loan.startDate}</dd><dt>End date</dt><dd>{visible.loan.endDate}</dd>
        <dt>Record version</dt><dd>{visible.revision}</dd><dt>Source revision</dt><dd>{visible.loan.sourceRevision}</dd><dt>Recorded</dt><dd>{visible.recordedAt}</dd>
      </dl><a href={'/finances?fact=' + encodeURIComponent(visible.loan.sourceFinancialFactId)}>View exact loan source history</a>
      <p>Terms were entered and reviewed by the customer. Ownership and current lender balances have not been independently verified.</p></>}
    </Drawer>
    <Drawer title="Loan record history" open={history?.api === api} onClose={() => { setHistory(null); }} size={560} destroyOnHidden>
      {history?.api === api && <LoanHistory key={history.record.loanRecordId} api={api} record={history.record} />}
    </Drawer>
  </section>;
}

function LoanHistory({ api, record }: { api: AccessApi; record: ProductLoanRecord }) {
  const [page, setPage] = useState<ProductLoanRecordHistory | null>(null);
  const [failure, setFailure] = useState(false);
  const [loading, setLoading] = useState(false);
  const [version, setVersion] = useState<ProductLoanRecord | null>(null);
  const load = useCallback(async (before?: number, signal?: AbortSignal) => {
    setLoading(true); setFailure(false); setPage(null); setVersion(null);
    try {
      const value = await getLoanRecordHistory(api, record.loanRecordId, before, signal);
      if (!Array.isArray(value.items) || value.items.length > 50 || value.items.some((item) => item.loanRecordId !== record.loanRecordId)) throw new Error('Invalid history');
      if (!signal?.aborted) setPage(value);
    } catch { if (!signal?.aborted) setFailure(true); }
    finally { if (!signal?.aborted) setLoading(false); }
  }, [api, record.loanRecordId]);
  useEffect(() => {
    const controller = new AbortController(); const start = window.setTimeout(() => { void load(undefined, controller.signal); }, 0);
    return () => { window.clearTimeout(start); controller.abort(); };
  }, [load]);
  return <section aria-label="Reviewed loan change history"><h2>{record.loan.name}</h2>
    <p>Each saved version retains its reviewed terms and original source. This history does not verify balances with the lender.</p>
    {loading && <p role="status">Loading loan history…</p>}
    {failure && <Alert type="error" title="Loan history is unavailable." action={<Button onClick={() => { void load(); }}>Retry loan history</Button>} />}
    {page?.items.map((item) => <Card key={item.revision} size="small" style={{ marginBlock: 12 }}>
      <strong>Version {item.revision}</strong><p>{item.loan.name} · {item.loan.lender} · {money(item.loan.outstanding,item.currency)} as of {item.loan.balanceAsOfDate}</p>
      <p>{statusLabel(item)}{item.lifecycleAsOfDate?' as of '+item.lifecycleAsOfDate:''}</p>
      <p>Reviewed {item.recordedAt}</p><Button onClick={() => { setVersion(item); }}>View version {item.revision}</Button>
    </Card>)}
    {page?.hasMore && page.nextBeforeRevision !== null && <Button disabled={loading} onClick={() => { void load(page.nextBeforeRevision ?? undefined); }}>Earlier versions</Button>}
    {page && <Button disabled={loading} onClick={() => { void load(); }}>Latest versions</Button>}
    {version && <section aria-label={'Saved loan version ' + String(version.revision)}><h3>Saved version {version.revision}</h3><dl>
      <dt>Customer-reported loan status</dt><dd>{statusLabel(version)}{version.lifecycleAsOfDate?' as of '+version.lifecycleAsOfDate:''}</dd>
      <dt>Loan name</dt><dd>{version.loan.name}</dd><dt>Lender</dt><dd>{version.loan.lender}</dd><dt>Loan type</dt><dd>{terms(version)}</dd>
      <dt>Currency</dt><dd>{version.currency}</dd><dt>Sanction amount</dt><dd>{money(version.loan.sanctionAmount,version.currency)}</dd>
      <dt>Outstanding balance</dt><dd>{money(version.loan.outstanding,version.currency)} as of {version.loan.balanceAsOfDate}</dd>
      <dt>Annual interest rate</dt><dd>{version.loan.interestRatePercent}%</dd><dt>Monthly EMI</dt><dd>{money(version.loan.emi,version.currency)}</dd>
      <dt>Tenure</dt><dd>{version.loan.tenureMonths} months</dd><dt>Start date</dt><dd>{version.loan.startDate}</dd><dt>End date</dt><dd>{version.loan.endDate}</dd>
      <dt>Source revision</dt><dd>{version.loan.sourceRevision}</dd></dl>
      <a href={'/finances?fact=' + encodeURIComponent(version.loan.sourceFinancialFactId)}>View exact loan source history</a></section>}
  </section>;
}
