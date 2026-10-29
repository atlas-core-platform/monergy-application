import MonthlyCashflowComparison from './MonthlyCashflowComparison';
import DebtServiceRatioPanel from './DebtServiceRatioPanel';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Checkbox, Drawer, Input, Space, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import { getIncomeRecords, getExpenseRecords } from './productApi';
import { calculateMonthlyCashflow, exactMoney, getMonthlyCashflowPositions, getMonthlyCashflowResult, getMonthlyCashflowResults, monthlyCashflowIssues, saveMonthlyCashflowPosition, validMonthlyCashflowDraft, validMonthlyCashflowPage, validMonthlyCashflowPosition, validMonthlyCashflowResult } from './monthlyCashflowApi';
import type { MonthlyCashflowDraft, MonthlyCashflowLine, MonthlyCashflowPage, MonthlyCashflowPosition, MonthlyCashflowResult } from './monthlyCashflowApi';
const noResults: MonthlyCashflowResult[] = [];
const uuid = () => crypto.randomUUID().replaceAll('-', '');
const blank = (): MonthlyCashflowDraft => ({ asOfDate: '', currency: '', lines: [], incomeComplete: false, expensesComplete: false, replacesPositionId: null, household: 'Self' });
const amount = (value: string) => {
  if (value === '') return null;
  const parts = value.split('.'); const whole = parts[0] ?? ''; const cents = parts[1] ?? '';
  return parts.length <= 2 && /^(?:0|[1-9]\d{0,11})$/u.test(whole) && /^\d{0,2}$/u.test(cents) ? whole + '.' + cents.padEnd(2, '0') : value;
};
const today = () => new Date(Date.now() + 14 * 60 * 60 * 1000).toISOString().slice(0, 10);
const fields = ['INCOME', 'EXPENSE'] as const;
interface Submission { save: { operationId: string; position: MonthlyCashflowDraft }; calculationOperation: string; saved: MonthlyCashflowPosition | null }
export default function MonthlyCashflowPageExperience() {
  const { api, authorization } = useWorkspaceSession(); const product = authorization?.area === 'product' ? authorization.context : null;
  if (!api || !product) return <Alert type="info" title="Your financial workspace is unavailable." />;
  return <MonthlyCashflowExperience api={api} customerId={product.customerId} canRead={product.capabilities.includes('financial-profile.profile.read')} canWrite={product.capabilities.includes('financial-profile.profile.update')} canCalculate={product.capabilities.includes('financial-rules.calculation.execute')} canReadDocuments={product.capabilities.includes('evidence.document.read')} />;
}
export function MonthlyCashflowExperience({ api, customerId, canRead, canWrite, canCalculate, canReadDocuments }: { api: AccessApi; customerId: string; canRead: boolean; canWrite: boolean; canCalculate: boolean; canReadDocuments: boolean }) {
  const context = useMemo(() => ({ api, customerId, canRead, canWrite, canCalculate, canReadDocuments }), [api, customerId, canRead, canWrite, canCalculate, canReadDocuments]);
  const scope = useRef(context); useLayoutEffect(() => { scope.current = context; return () => { scope.current = { ...context, canRead: false }; }; }, [context]);
  const [draft, setDraft] = useState<{ context: typeof context; value: MonthlyCashflowDraft; open: boolean } | null>(null);
  const [submission, setSubmission] = useState<{ context: typeof context; value: Submission } | null>(null);
  const [pending, setPending] = useState<typeof context | null>(null); const [conflict, setConflict] = useState<typeof context | null>(null);
  const [error, setError] = useState<{ context: typeof context; text: string } | null>(null);
  const [positions, setPositions] = useState<{ context: typeof context; page: MonthlyCashflowPage<MonthlyCashflowPosition> } | null>(null);
  const [results, setResults] = useState<{ context: typeof context; page: MonthlyCashflowPage<MonthlyCashflowResult> } | null>(null);
  const [historyError, setHistoryError] = useState<{ context: typeof context; text: string } | null>(null);
  const [selected, setSelected] = useState<{ context: typeof context; result: MonthlyCashflowResult } | null>(null);
  const [review, setReview] = useState<{ context: typeof context; position: MonthlyCashflowPosition } | null>(null);
  const [loading, setLoading] = useState<typeof context | null>(null); const generation = useRef(0); const reading = useRef<AbortController | null>(null);
  const current = draft?.context === context ? draft : null; const frozen = submission?.context === context ? submission.value : null; const busy = pending === context;
  const load = useCallback(async (positionCursor?: string, resultCursor?: string) => {
    if (!canRead) return; reading.current?.abort(); const controller = new AbortController(); reading.current = controller; const version = ++generation.current;
    const active = () => scope.current === context && !controller.signal.aborted && version === generation.current;
    setLoading(context); setHistoryError(null); setPositions(null); setResults(null);
    try {
      const [p, r] = await Promise.all([getMonthlyCashflowPositions(api, positionCursor, controller.signal), canCalculate ? getMonthlyCashflowResults(api, resultCursor, controller.signal) : Promise.resolve(null)]);
      if (!validMonthlyCashflowPage(p, item => validMonthlyCashflowPosition(item, customerId), item => item.positionId) || p.items.some(item => item.positionId === positionCursor) || r && (!validMonthlyCashflowPage(r, item => validMonthlyCashflowResult(item, customerId), item => item.calculationId) || r.items.some(item => item.calculationId === resultCursor))) throw new Error('Invalid saved history');
      if (active()) { setPositions({ context, page: p }); if (r) setResults({ context, page: r }); }
    } catch { if (active()) setHistoryError({ context, text: 'Saved monthly cash flow reviews are unavailable. Check your current access and try again.' }); }
    finally { if (active()) setLoading(null); }
  }, [api, canRead, canCalculate, context, customerId]);
  useEffect(() => { const timer = window.setTimeout(() => { void load(); }, 0); return () => { window.clearTimeout(timer); reading.current?.abort(); }; }, [load]);
  useEffect(() => {
    const id = new URLSearchParams(window.location.search).get('calculation');
    if (!id || !canRead || !canCalculate) return;
    const controller = new AbortController();
    void getMonthlyCashflowResult(api, id, controller.signal).then(result => {
      if (!validMonthlyCashflowResult(result, customerId) || result.calculationId !== id) throw new Error('Invalid calculation');
      if (scope.current === context && !controller.signal.aborted) setSelected({ context, result });
    }).catch(() => { if (scope.current === context && !controller.signal.aborted) setHistoryError({ context, text: 'The linked calculation is unavailable under your current access.' }); });
    return () => { controller.abort(); };
  }, [api, canRead, canCalculate, context, customerId]);
  const edit = (value: MonthlyCashflowDraft) => { if (!canWrite || busy || frozen) return; setDraft({ context, value, open: true }); setError(null); setConflict(null); };
  const change = (value: MonthlyCashflowDraft) => { if (current && !frozen && !busy && conflict !== context) setDraft({ ...current, value }); };
  const lineChange = (index: number, value: MonthlyCashflowLine) => { if (current) change({ ...current.value, lines: current.value.lines.map((line, i) => i === index ? value : line), incomeComplete: false, expensesComplete: false }); };
  const add = (side: string) => { if (current && current.value.lines.length < 200) change({ ...current.value, incomeComplete: false, expensesComplete: false, lines: [...current.value.lines, { lineId: uuid(), side, kind: 'Other', name: '', amount: null, currency: current.value.currency, valueDate: current.value.asOfDate || null, sourceNote: '', record: null, member: 'Self' }] }); };
  const importRecords = async () => {
    if (!current || frozen || busy || !canReadDocuments || !current.value.asOfDate || !/^[A-Z]{3}$/u.test(current.value.currency)) return;
    const original = current; setPending(context); setError(null);
    try {
      const [a, l] = await Promise.all([getIncomeRecords(api), getExpenseRecords(api)]);
      if (a.hasMore || l.hasMore || [...a.items, ...l.items].some(x => x.customerId !== customerId)) throw new Error('Incomplete source list');
      const start = original.value.asOfDate.slice(0, 8) + '01';
      const make = (side: string, id: string, revision: number, name: string, kind: string, value: number, currency: string): MonthlyCashflowLine => ({ lineId: uuid(), side, kind, name, amount: value.toFixed(2), currency, valueDate: original.value.asOfDate, sourceNote: 'Saved whole-month customer-reviewed record', record: { kind: side, recordId: id, revision }, member: 'Self' });
      const imported = [...a.items.filter(x => x.income.periodStart === start && x.income.periodEnd === original.value.asOfDate && x.currency === original.value.currency).map(x => make('INCOME', x.incomeRecordId, x.revision, x.income.name, x.income.incomeType, x.income.amount, x.currency)), ...l.items.filter(x => x.expense.periodStart === start && x.expense.periodEnd === original.value.asOfDate && x.currency === original.value.currency).map(x => make('EXPENSE', x.expenseRecordId, x.revision, x.expense.name, x.expense.category, x.expense.amount, x.currency))];
      const existing = new Set(original.value.lines.map(x => x.record && x.record.kind + '|' + x.record.recordId)); const combined = [...original.value.lines, ...imported.filter(x => x.record === null || !existing.has(x.record.kind + '|' + x.record.recordId))];
      const next = { ...original.value, lines: combined, incomeComplete: false, expensesComplete: false };
      if (!validMonthlyCashflowDraft(next) || imported.some(x => x.currency !== original.value.currency || x.valueDate !== original.value.asOfDate)) throw new Error('Invalid or excessive saved records');
      if (scope.current === context) { setDraft({ ...original, value: next }); setError({ context, text: imported.length === 0 ? 'No saved records match this date and currency. Add and review any other income or expenses; this does not mean zero.' : 'Saved records added. Review other income and expenses, missing records and possible overlap before confirming the inventory.' }); }
    } catch { if (scope.current === context) setError({ context, text: 'Saved records could not be imported completely. No partial import was applied. Review the date, currency, record limit and document access.' }); }
    finally { setPending(value => value === context ? null : value); }
  };
  const save = async (existing?: MonthlyCashflowPosition) => {
    if (!canRead || !canCalculate || busy || !frozen && !existing && (!current || !canWrite || !validMonthlyCashflowDraft(current.value) || conflict === context)) return;
    const request: Submission = frozen ?? { save: { operationId: uuid(), position: existing?.position ?? current?.value ?? blank() }, calculationOperation: uuid(), saved: existing ?? null };
    let confirmed = request.saved;
    setSubmission({ context, value: request }); setPending(context); setError(null);
    try {
      const position = request.saved ?? await saveMonthlyCashflowPosition(api, request.save);
      if (!validMonthlyCashflowPosition(position, customerId) || !request.saved && (position.operationId !== request.save.operationId || JSON.stringify(position.position) !== JSON.stringify(request.save.position))) throw new Error('Invalid review confirmation');
      confirmed = position; if (scope.current !== context) return; setSubmission({ context, value: { ...request, saved: position } });
      const result = await calculateMonthlyCashflow(api, { operationId: request.calculationOperation, positionId: position.positionId });
      if (!validMonthlyCashflowResult(result, customerId) || result.operationId !== request.calculationOperation || JSON.stringify(result.position) !== JSON.stringify(position)) throw new Error('Invalid calculation confirmation');
      if (scope.current !== context) return; setSelected({ context, result }); setSubmission(null); setDraft(null); await load();
    } catch (failure) {
      if (scope.current !== context) return;
      const rejected = failure instanceof AccessApiError && (failure.status === 400 || failure.status === 409);
      if (rejected) { setSubmission(null); if (failure.status === 409 || confirmed) setConflict(context); }
      setError({ context, text: rejected ? 'The review was rejected or a referenced record changed. Refresh, review the saved versions and start a new review.' : confirmed ? 'Calculation has not been confirmed. Retry confirmation using this saved review and the same calculation request.' : 'Saving has not been confirmed. Retry confirmation using the same review and request.' });
    } finally { setPending(value => value === context ? null : value); }
  };
  const openResult = async (item: MonthlyCashflowResult) => {
    setError(null); setLoading(null); const controller = new AbortController(); reading.current?.abort(); reading.current = controller;
    try { const result = await getMonthlyCashflowResult(api, item.calculationId, controller.signal); if (!validMonthlyCashflowResult(result, customerId) || result.calculationId !== item.calculationId || JSON.stringify(result) !== JSON.stringify(item)) throw new Error('Changed result'); if (scope.current === context && !controller.signal.aborted) setSelected({ context, result }); }
    catch { if (scope.current === context && !controller.signal.aborted) setError({ context, text: 'The saved result could not be confirmed under your current access.' }); }
  };
  if (!canRead) return <Alert type="info" title="Monthly income and expenses are not included in your current access." />;
  const positionPage = positions?.context === context ? positions.page : null; const resultPage = results?.context === context ? results.page : null;
  return <main className="product-page"><h1>Income and Expenses</h1><p>Review all income received and expenses paid for one completed calendar month, in one currency and household scope. Monthly balance is income minus expenses.</p>
    <p>Saved record versions keep their source links. Other values are your declarations and are not independently verified. No exchange rates or older values are assumed.</p>
    <Space wrap>{canWrite && canCalculate && <Button type="primary" disabled={busy || Boolean(frozen)} onClick={() => { edit(blank()); }}>Start monthly cash flow review</Button>}{frozen && <Button loading={busy} onClick={() => { if (current) setDraft({ ...current, open: true }); else void save(); }}>Continue monthly cash flow confirmation</Button>}<Button disabled={busy} loading={loading === context} onClick={() => { setSelected(null); setReview(null); void load(); }}>Refresh saved reviews</Button></Space>
    {!canCalculate && <Alert type="info" title="Calculation access is required to calculate and view saved monthly cash flow results." />}
    {error?.context === context && !current?.open && <Alert type="error" title={error.text} />}{historyError?.context === context && <Alert type="error" title={historyError.text} />}
    <h2>Saved results</h2>{resultPage?.items.length === 0 && <p>No saved monthly cash flow calculations yet. This does not mean zero monthly cash flow.</p>}
    {resultPage?.items.map(item => <Card key={item.calculationId} size="small" style={{ marginBlock: 12 }}><h3>{item.position.position.asOfDate} · {item.position.position.currency}</h3><p>{item.state === 'CALCULATED' && item.balance !== null ? exactMoney(item.balance, item.position.position.currency) : 'More information needed'}</p><p>Calculated {item.recordedAt}</p><Button onClick={() => { void openResult(item); }}>View saved result: {item.position.position.asOfDate}</Button></Card>)}
    {resultPage?.hasMore && resultPage.nextBeforeId && <Button onClick={() => { void load(undefined, resultPage.nextBeforeId ?? undefined); }}>Earlier results</Button>}
    {canCalculate && <MonthlyCashflowComparison api={api} customerId={customerId} items={resultPage?.items ?? noResults} />}
    <h2>Saved inventories</h2>{positionPage?.items.length === 0 && <p>No reviewed inventories saved yet.</p>}
    {positionPage?.items.map(item => <Card key={item.positionId} size="small" style={{ marginBlock: 12 }}><p>{item.position.asOfDate} · {item.position.currency} · {item.lines.length} lines · Reviewed {item.recordedAt}</p><Button onClick={() => { setReview({ context, position: item }); }}>View saved inventory: {item.position.asOfDate}</Button>{canCalculate && <Button disabled={busy || Boolean(frozen)} onClick={() => { void save(item); }}>Calculate saved inventory: {item.position.asOfDate}</Button>}{canWrite && canCalculate && <Button disabled={busy || Boolean(frozen)} onClick={() => { edit({ ...structuredClone(item.position), incomeComplete: false, expensesComplete: false, replacesPositionId: item.positionId }); }}>Update this inventory: {item.position.asOfDate}</Button>}</Card>)}
    {positionPage?.hasMore && positionPage.nextBeforeId && <Button onClick={() => { void load(positionPage.nextBeforeId ?? undefined); }}>Earlier inventories</Button>}
    <Drawer title="Review monthly cash flow" open={Boolean(current?.open && canWrite && canCalculate)} onClose={() => { if (current) setDraft({ ...current, open: false }); }} size={760} closable={!busy} mask={{ closable: !busy }} destroyOnHidden>
      {error?.context === context && <Alert type="info" title={error.text} />}
      {current && <><fieldset disabled={busy || Boolean(frozen) || conflict === context} style={{ border: 0, padding: 0 }}>
        <label htmlFor="cashflow-household">Household scope</label><Input id="cashflow-household" maxLength={120} value={current.value.household} onChange={e => { change({ ...current.value, household: e.target.value, incomeComplete: false, expensesComplete: false }); }} />
        <label htmlFor="monthly-cashflow-date">Completed month end</label><Input id="monthly-cashflow-date" type="date" min="1900-01-01" max={today()} value={current.value.asOfDate} onChange={e => { change({ ...current.value, asOfDate: e.target.value, incomeComplete: false, expensesComplete: false }); }} />
        <label htmlFor="monthly-cashflow-currency">Review currency</label><Input id="monthly-cashflow-currency" maxLength={3} placeholder="For example, INR" value={current.value.currency} onChange={e => { change({ ...current.value, currency: e.target.value.toUpperCase(), incomeComplete: false, expensesComplete: false }); }} />
        {canReadDocuments && <Button style={{ marginBlock: 12 }} disabled={!current.value.asOfDate || !/^[A-Z]{3}$/u.test(current.value.currency)} onClick={() => { void importRecords(); }}>Add saved records for this date and currency</Button>}
        <p>Include every payer and spending category for the named household and month. Enter whole-month totals, not rates or annual estimates. Reconcile overlaps before confirming. Household member names are declarations, not access to another customer. Up to 200 lines can be reviewed together.</p>
        {fields.map(side => <section key={side} aria-label={side === 'INCOME' ? 'Review income' : 'Review expenses'}><h2>{side === 'INCOME' ? 'Income' : 'Expenses'}</h2>
          {current.value.lines.filter(line => line.side === side).length === 0 && <p>No {side === 'INCOME' ? 'income' : 'expense'} lines. Confirm the inventory below only after checking that you have none to include.</p>}
          {current.value.lines.map((line, index) => line.side !== side ? null : <Card key={line.lineId} size="small" style={{ marginBlock: 12 }}><Tag>{line.record ? 'Saved reviewed record · version ' + String(line.record.revision) : 'Customer declaration'}</Tag>
            <label htmlFor={'flow-kind-' + line.lineId}>Category</label><Input id={'flow-kind-' + line.lineId} aria-label={'Category for line ' + String(index + 1)} disabled={Boolean(line.record)} maxLength={80} value={line.kind} onChange={e => { lineChange(index, { ...line, kind: e.target.value }); }} />
            <label htmlFor={'flow-member-' + line.lineId}>Household member</label><Input id={'flow-member-' + line.lineId} aria-label={'Member for line ' + String(index + 1)} maxLength={80} value={line.member} onChange={e => { lineChange(index, { ...line, member: e.target.value }); }} />
            <label htmlFor={'net-name-' + line.lineId}>Name</label><Input id={'net-name-' + line.lineId} aria-label={'Name for line ' + String(index + 1)} maxLength={120} disabled={Boolean(line.record)} value={line.name} onChange={e => { lineChange(index, { ...line, name: e.target.value }); }} />
            <label htmlFor={'net-amount-' + line.lineId}>{side === 'INCOME' ? 'Monthly income received' : 'Monthly expense paid'}</label><Input id={'net-amount-' + line.lineId} aria-label={'Amount for line ' + String(index + 1)} inputMode="decimal" disabled={Boolean(line.record)} placeholder="Leave blank if unknown" value={line.amount ?? ''} onChange={e => { lineChange(index, { ...line, amount: e.target.value === '' ? null : e.target.value }); }} onBlur={() => { if (!line.record) lineChange(index, { ...line, amount: amount(line.amount ?? '') }); }} />
            <label htmlFor={'net-currency-' + line.lineId}>Value currency</label><Input id={'net-currency-' + line.lineId} aria-label={'Currency for line ' + String(index + 1)} maxLength={3} disabled={Boolean(line.record)} value={line.currency} onChange={e => { lineChange(index, { ...line, currency: e.target.value.toUpperCase() }); }} />
            <label htmlFor={'net-value-date-' + line.lineId}>Month end</label><Input id={'net-value-date-' + line.lineId} aria-label={'Month end for line ' + String(index + 1)} type="date" disabled={Boolean(line.record)} value={line.valueDate ?? ''} onChange={e => { lineChange(index, { ...line, valueDate: e.target.value || null }); }} />
            <label htmlFor={'net-note-' + line.lineId}>Source or explanation</label><Input.TextArea id={'net-note-' + line.lineId} aria-label={'Source for line ' + String(index + 1)} maxLength={500} value={line.sourceNote} onChange={e => { lineChange(index, { ...line, sourceNote: e.target.value }); }} />
            <Button danger onClick={() => { change({ ...current.value, lines: current.value.lines.filter(x => x.lineId !== line.lineId), incomeComplete: false, expensesComplete: false }); }}>Remove line {index + 1}</Button>{line.record && <p>To change this linked value, correct its saved financial record or remove it and add a clearly identified declaration.</p>}
          </Card>)}<Button disabled={current.value.lines.length >= 200} onClick={() => { add(side); }}>Add {side === 'INCOME' ? 'income' : 'expense'}</Button></section>)}
        <p>Missing information is saved for review and produces no monthly cash flow total. Confirming an empty side explicitly declares that you have none on this date in the review.</p>
        <Checkbox checked={current.value.incomeComplete} onChange={e => { change({ ...current.value, incomeComplete: e.target.checked }); }}>I have reviewed and included all my income, or confirm I have none.</Checkbox>
        <Checkbox checked={current.value.expensesComplete} onChange={e => { change({ ...current.value, expensesComplete: e.target.checked }); }}>I have reviewed and included all my expenses, or confirm I have none.</Checkbox>
      </fieldset><Button type="primary" style={{ marginBlock: 16 }} loading={busy} disabled={conflict === context || !frozen && !validMonthlyCashflowDraft(current.value)} onClick={() => { void save(); }}>{frozen ? 'Retry monthly cash flow confirmation' : 'Save review and calculate monthly cash flow'}</Button></>}
    </Drawer>
    <Drawer title="Saved monthly cash flow result" open={selected?.context === context && canCalculate} onClose={() => { setSelected(null); }} size={760} destroyOnHidden>{selected?.context === context && <MonthlyCashflowDetails api={api} customerId={customerId} result={selected.result} />}</Drawer>
    <Drawer title="Saved reviewed inventory" open={review?.context === context} onClose={() => { setReview(null); }} size={760} destroyOnHidden>{review?.context === context && <PositionDetails position={review.position} />}</Drawer>
  </main>;
}
function MonthlyCashflowDetails({ api,customerId,result }: { api:AccessApi;customerId:string;result:MonthlyCashflowResult }) {
  return <section aria-label="Saved monthly cash flow calculation"><h2>{result.position.position.asOfDate} · {result.position.position.currency}</h2><Tag>Customer-reviewed inputs</Tag>
    {result.state === 'CALCULATED' && result.totalIncome !== null && result.totalExpenses !== null && result.balance !== null ? <dl><dt>Total income</dt><dd>{exactMoney(result.totalIncome, result.position.position.currency)}</dd><dt>Total expenses</dt><dd>{exactMoney(result.totalExpenses, result.position.position.currency)}</dd><dt>Monthly balance</dt><dd>{exactMoney(result.balance, result.position.position.currency)}</dd></dl> : <Alert type="info" title="More information needed before monthly cash flow can be calculated" description={<ul>{result.issues.map(issue => <li key={issue}>{monthlyCashflowIssues[issue]}</li>)}</ul>} />}
    <p>Monthly balance = total income − total expenses. This saved result uses the exact reviewed inventory below. Later changes require a new review and calculation.</p><p>Rule version {result.ruleVersion} · Calculated {result.recordedAt}</p><PositionDetails position={result.position} />
    <details><summary>Calculation details</summary><p>{result.numericSemantics}</p><p>Rule: {result.ruleId} · Definition: {result.definitionHash}</p><p>Calculation: {result.calculationId} · Recorded by: {result.recordedBy}</p></details>
    <DebtServiceRatioPanel api={api} customerId={customerId} source={result} />
  </section>;
}
function PositionDetails({ position }: { position: MonthlyCashflowPosition }) {
  return <section aria-label="Reviewed monthly cash flow inventory"><p>Household scope: {position.position.household}</p><h3>Reviewed inventory · {position.position.asOfDate} · {position.position.currency}</h3><p>Income confirmed complete: {position.position.incomeComplete ? 'Yes' : 'No'} · Expenses confirmed complete: {position.position.expensesComplete ? 'Yes' : 'No'}</p>
    {position.lines.map(({ line, source }) => <Card key={line.lineId} size="small" style={{ marginBlock: 12 }}><h4>{line.name}</h4><Tag>{line.side} · {line.kind} · {line.member}</Tag><Tag>{source ? 'Saved reviewed record' : 'Customer declaration'}</Tag><dl><dt>{line.side === 'INCOME' ? 'Monthly income received' : 'Monthly expense paid'}</dt><dd>{line.amount === null ? 'Value missing' : exactMoney(line.amount, line.currency)}</dd><dt>Month end</dt><dd>{line.valueDate ?? 'Date missing'}</dd><dt>Source or explanation</dt><dd>{line.sourceNote}</dd></dl>
      {source && <><p>Record version {line.record?.revision} · Source revision {source.sourceRevision} · Reviewed {source.reviewedAt}</p><a href={'/finances?fact=' + encodeURIComponent(source.financialFactId)}>View source history for {line.name}</a><details><summary>Exact source references</summary><p>Financial fact: {source.financialFactId}, revision {source.sourceRevision}</p><p>Document version: {source.documentVersionId} · Evidence: {source.evidenceId}</p><p>Provenance: {source.provenanceId}</p></details></>}
    </Card>)}<p>Values and inventory completeness were reviewed by the customer. Independent payer, merchant and financial verification are not established by this calculation.</p>
  </section>;
}
