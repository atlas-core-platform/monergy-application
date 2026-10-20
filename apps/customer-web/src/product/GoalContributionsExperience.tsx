import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Space, Tag } from 'antd';
import { AccessApiError, type AccessApi } from '../access/accessApi';
import { createGoalContribution, getGoalContributions, reviseGoalContribution, getGoalContributionHistory, withdrawGoalContribution } from './productApi';
import type { ProductFinancialGoal, ProductFinancialGoalDraft, ProductGoalContribution, ProductGoalContributionDraft, ProductGoalContributionList, ProductGoalContributionHistory } from './productApi';
interface Values { amount: string; contributionDate: string }
interface Submission { operationId: string; contribution: ProductGoalContributionDraft; expectedRevision?: number }
const empty: Values = { amount: '', contributionDate: '' };
const moneyValue = (value: number) => Number.isFinite(value) && value > 0 && value <= 999999999999.99 && Number(value.toFixed(2)) === value;
const validDate = (value: string) => /^\d{4}-\d{2}-\d{2}$/u.test(value) && !Number.isNaN(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value;
const today = () => new Date(Date.now() + 14 * 60 * 60 * 1000).toISOString().slice(0, 10);
const sameGoal = (a: ProductFinancialGoalDraft, b: ProductFinancialGoalDraft) => a.name === b.name && a.targetAmount === b.targetAmount && a.currency === b.currency && a.targetDate === b.targetDate;
const validContribution = (value: ProductGoalContributionDraft | null | undefined) => value != null && typeof value.financialGoalId === 'string' && value.financialGoalId.length > 0 && Number.isInteger(value.goalRevision) && value.goalRevision > 0 && moneyValue(value.amount) && validDate(value.contributionDate) && value.contributionDate >= '1900-01-01' && value.contributionDate <= today();
const validSource = (value: ProductFinancialGoalDraft | null | undefined) => value != null && typeof value.name === 'string' && value.name.length > 0 && /^[A-Z]{3}$/u.test(value.currency) && moneyValue(value.targetAmount) && validDate(value.targetDate);
const validReason = (value: unknown): value is string => typeof value === 'string' && value.trim() === value && value.length > 0 && value.length <= 500 && !Array.from(value).some(char => char.charCodeAt(0) < 32 || char.charCodeAt(0) >= 127 && char.charCodeAt(0) <= 159);
const bound = (record: ProductGoalContribution | null | undefined) => record != null && typeof record.goalContributionId === 'string' && record.goalContributionId.length > 0 && typeof record.customerId === 'string' && record.customerId.length > 0 && (record.declarationStatus === 'CUSTOMER_REPORTED' && record.withdrawalReason == null || record.declarationStatus === 'CUSTOMER_WITHDRAWN' && record.revision > 1 && validReason(record.withdrawalReason)) && Number.isInteger(record.revision) && record.revision > 0 && validContribution(record.contribution) && validSource(record.sourceGoal);
const money = (record: ProductGoalContribution) => new Intl.NumberFormat(undefined, { style: 'currency', currency: record.sourceGoal.currency }).format(record.contribution.amount);

export default function GoalContributionsExperience({ api, goal, canRead, canCreate }: { api: AccessApi; goal: ProductFinancialGoal; canRead: boolean; canCreate: boolean }) {
  const context = useMemo(() => ({ api, canRead, canCreate, goalId: goal.financialGoalId, customerId: goal.customerId }), [api, canRead, canCreate, goal.financialGoalId, goal.customerId]);
  const scope = useRef(context);
  useLayoutEffect(() => { scope.current = context; return () => { scope.current = { ...context, canRead: false, canCreate: false }; }; }, [context]);
  const sequence = useRef(0); const pending = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ context: typeof context; list: ProductGoalContributionList } | null>(null);
  const [error, setError] = useState<{ context: typeof context; text: string } | null>(null);
  const [busy, setBusy] = useState<typeof context | null>(null);
  const [editor, setEditor] = useState<{ context: typeof context; open: boolean; values: Values; operationId: string; goalRevision: number; sourceGoal: ProductFinancialGoalDraft; original: ProductGoalContribution | null; submission: Submission | null; conflict: boolean } | null>(null);
  const [saving, setSaving] = useState<typeof context | null>(null);
  const [saveError, setSaveError] = useState<{ context: typeof context; text: string } | null>(null);
  const [saved, setSaved] = useState<{ context: typeof context; record: ProductGoalContribution } | null>(null);
  const [history, setHistory] = useState<{ context: typeof context; record: ProductGoalContribution } | null>(null);
  const [selected, setSelected] = useState<{ context: typeof context; record: ProductGoalContribution } | null>(null);
  const [withdrawal, setWithdrawal] = useState<{ context: typeof context; record: ProductGoalContribution; open: boolean } | null>(null);
  const withdrawing = withdrawal?.context === context ? withdrawal : null;
  const load = useCallback(async (signal?: AbortSignal) => {
    if (!canRead) return;
    const generation = ++sequence.current; pending.current?.abort(); const controller = new AbortController(); pending.current = controller;
    const abort = () => { controller.abort(); }; signal?.addEventListener('abort', abort, { once: true });
    const active = () => scope.current === context && !signal?.aborted && !controller.signal.aborted && generation === sequence.current;
    setBusy(context); setError(null);
    try {
      const list = await getGoalContributions(api, context.goalId, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean' || list.hasMore && list.items.length !== 200 || list.items.some(item => !bound(item) || item.contribution.financialGoalId !== context.goalId || item.customerId !== goal.customerId) || new Set(list.items.map(item => item.goalContributionId)).size !== list.items.length) throw new Error('Invalid contribution list');
      if (active()) setLoaded({ context, list });
    } catch (failure) { if (active()) { setLoaded(null); setError({ context, text: failure instanceof AccessApiError && failure.status === 403 ? 'Your current access does not include goal contributions.' : 'Goal contributions are unavailable. Try again.' }); } }
    finally { signal?.removeEventListener('abort', abort); if (active()) setBusy(null); }
  }, [api, canRead, context, goal.customerId]);
  useEffect(() => { const controller = new AbortController(); const timer = window.setTimeout(() => { void load(controller.signal); }, 0); return () => { window.clearTimeout(timer); controller.abort(); pending.current?.abort(); }; }, [load]);
  const current = editor?.context === context ? editor : null; const isSaving = saving === context;
  const list = loaded?.context === context ? loaded.list : null;
  const changedGoal = current && !current.original && current.goalRevision !== goal.revision;
  const draft: ProductGoalContributionDraft | null = current ? { financialGoalId: context.goalId, goalRevision: current.goalRevision, amount: Number(current.values.amount), contributionDate: current.values.contributionDate } : null;
  const valid = draft && current?.values.amount !== '' && moneyValue(draft.amount) && validDate(draft.contributionDate) && draft.contributionDate >= '1900-01-01' && draft.contributionDate <= today() && !current?.conflict && !changedGoal;
  const open = (record?: ProductGoalContribution) => {
    if (!canCreate || isSaving || withdrawing || record?.declarationStatus === 'CUSTOMER_WITHDRAWN') return; setSaveError(null); setSaved(null);
    if (current?.submission) { setEditor({ ...current, open: true }); return; }
    setSelected(null); setEditor({ context, open: true, values: record ? { amount: String(record.contribution.amount), contributionDate: record.contribution.contributionDate } : empty,
      goalRevision: record?.contribution.goalRevision ?? goal.revision, sourceGoal: record?.sourceGoal ?? goal.goal,
      operationId: crypto.randomUUID().replaceAll('-', ''), original: record ?? null, submission: null, conflict: false });
  };
  const change = (key: keyof Values, value: string) => { if (current && !current.submission && !current.conflict && !isSaving) setEditor({ ...current, values: { ...current.values, [key]: value } }); };
  const save = async () => {
    if (!current || !draft || !canRead || !canCreate || isSaving || !current.submission && !valid) return;
    const submission: Submission = current.submission ?? { operationId: current.operationId, contribution: draft, ...(current.original ? { expectedRevision: current.original.revision } : {}) };
    setEditor({ ...current, submission }); setSaving(context); setSaveError(null);
    try {
      const record = current.original && submission.expectedRevision !== undefined ? await reviseGoalContribution(api, current.original.goalContributionId, { ...submission, expectedRevision: submission.expectedRevision }) : await createGoalContribution(api, submission);
      if (!bound(record) || record.declarationStatus !== 'CUSTOMER_REPORTED' || record.operationId !== submission.operationId || record.customerId !== goal.customerId || record.contribution.financialGoalId !== submission.contribution.financialGoalId || record.contribution.goalRevision !== submission.contribution.goalRevision || record.contribution.amount !== submission.contribution.amount || record.contribution.contributionDate !== submission.contribution.contributionDate || !sameGoal(record.sourceGoal, current.sourceGoal) || record.revision !== (submission.expectedRevision ?? 0) + 1 || current.original && record.goalContributionId !== current.original.goalContributionId) throw new Error('Invalid confirmation');
      if (scope.current !== context) return; setSaved({ context, record }); setEditor({ ...current, open: false, submission: null }); await load();
    } catch (failure) {
      if (scope.current !== context) return; const conflict = failure instanceof AccessApiError && failure.status === 409; const invalid = failure instanceof AccessApiError && failure.status === 400;
      setSaveError({ context, text: conflict ? 'The contribution changed or this request conflicts with a saved change. Close this form, refresh contributions and review before editing again.' : invalid ? 'Check the positive amount and contribution date. The date must be today or earlier. Review and edit before saving again.' : 'The save has not been confirmed. Retry confirmation to check the same request before recording another contribution.' });
      if (conflict || invalid) setEditor({ ...current, submission: null, conflict });
    } finally { setSaving(active => active === context ? null : active); }
  };
  const visible = selected?.context === context ? selected.record : null;
  if (!canRead) return <Alert type="info" title="Goal contributions are not included in your current access." />;
  return <section aria-label="Goal contributions"><h2>{goal.goal.name}</h2><Space wrap><Button disabled={busy === context || isSaving} onClick={() => { void load(); }}>Refresh contributions</Button>
    {canCreate && <Button disabled={isSaving || Boolean(withdrawing)} onClick={() => { open(); }}>{current?.submission ? 'Continue contribution confirmation' : 'Record contribution'}</Button>}{canCreate && withdrawing && !withdrawing.open && <Button onClick={() => { setWithdrawal({ ...withdrawing, open: true }); }}>Continue withdrawal confirmation</Button>}</Space>
    <p>Record an amount you report contributing towards this goal. This does not move money, verify a payment or calculate goal progress or required contributions.</p>
    {saved?.context === context && <Alert type="success" title={saved.record.declarationStatus === 'CUSTOMER_WITHDRAWN' ? 'Contribution report withdrawn' : 'Contribution saved'} />}
    {error?.context === context && <Alert type="error" title={error.text} action={<Button onClick={() => { void load(); }}>Retry contributions</Button>} />}
    {busy === context && <p role="status">Loading contributions…</p>}{list?.items.length === 0 && <p>No contributions reported for this goal yet.</p>}{list?.hasMore && <Alert type="info" title="Showing the 200 most recently added contribution records." />}
    {list?.items.map(record => <Card key={record.goalContributionId} size="small" style={{ marginBlock: 12 }}><Tag>{record.declarationStatus === 'CUSTOMER_WITHDRAWN' ? 'Report withdrawn' : 'Customer reported'}</Tag><p>{money(record)} · {record.contribution.contributionDate} · Goal version {record.contribution.goalRevision}</p>
      <Button onClick={() => { setSelected({ context, record }); }}>View contribution: {record.contribution.contributionDate}</Button>{canCreate && record.declarationStatus === 'CUSTOMER_REPORTED' && <Button disabled={isSaving || Boolean(current?.submission) || Boolean(withdrawing)} onClick={() => { open(record); }}>Edit contribution: {record.contribution.contributionDate}</Button>}{canCreate && record.declarationStatus === 'CUSTOMER_REPORTED' && <Button disabled={isSaving || Boolean(current?.submission) || Boolean(withdrawing)} onClick={() => { setEditor(null); setWithdrawal({ context, record, open: true }); }}>Withdraw contribution report: {record.contribution.contributionDate}</Button>}<Button onClick={() => { setHistory({ context, record }); }}>View contribution history: {record.contribution.contributionDate}</Button></Card>)}
    <Drawer title={current?.original ? 'Edit contribution' : 'Record contribution'} open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }} size={520} closable={!isSaving} mask={{ closable: !isSaving }} destroyOnHidden>
      {current && <p>Goal: {current.sourceGoal.name} · Goal version {current.goalRevision} · Currency: {current.sourceGoal.currency}. The saved goal version and currency remain attached to this report.</p>}
      {changedGoal && !current.submission && <Alert type="warning" title="The goal version changed. Close this form and review the updated goal before recording a contribution." />}
      {saveError?.context === context && <Alert type="error" title={saveError.text} />}
      <fieldset disabled={isSaving || Boolean(current?.submission) || (current?.conflict ?? false) || Boolean(changedGoal)} style={{ border: 0, padding: 0 }}><div style={{ marginBlock: 14 }}><label htmlFor="contribution-amount">Contribution amount</label><Input id="contribution-amount" type="number" min={0.01} max={999999999999.99} step={0.01} value={current?.values.amount ?? ''} onChange={event => { change('amount', event.target.value); }} /></div>
        <div style={{ marginBlock: 14 }}><label htmlFor="contribution-date">Contribution date</label><Input id="contribution-date" type="date" min="1900-01-01" max={today()} value={current?.values.contributionDate ?? ''} onChange={event => { change('contributionDate', event.target.value); }} /></div></fieldset>
      <p>Saving records your report. It does not confirm a bank transfer, allocation of savings or achievement of the goal.</p><Button type="primary" loading={isSaving} disabled={!canCreate || !current?.submission && !valid} onClick={() => { void save(); }}>{current?.submission ? 'Retry contribution confirmation' : current?.original ? 'Save contribution changes' : 'Save contribution'}</Button>
    </Drawer>
    {canCreate && withdrawing && <ContributionWithdrawal api={api} record={withdrawing.record} open={withdrawing.open} onClose={uncertain => { setWithdrawal(uncertain ? { ...withdrawing, open: false } : null); }} onSaved={record => { setWithdrawal(null); setSaved({ context, record }); void load(); }} />}
    <Drawer title="Contribution details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={520} destroyOnHidden>{visible && <ContributionDetails record={visible} />}</Drawer>
    <Drawer title="Contribution history" open={history?.context === context} onClose={() => { setHistory(null); }} size={520} destroyOnHidden>{history?.context === context && <ContributionHistory key={history.record.goalContributionId} api={api} record={history.record} />}</Drawer>
  </section>;
}
function ContributionDetails({ record }: { record: ProductGoalContribution }) {
  return <section aria-label={'Saved contribution version ' + String(record.revision)}><h3>Reported contribution</h3><Tag>{record.declarationStatus === 'CUSTOMER_WITHDRAWN' ? 'Report withdrawn' : 'Customer reported'}</Tag><dl><dt>Contribution amount</dt><dd>{money(record)}</dd><dt>Contribution date</dt><dd>{record.contribution.contributionDate}</dd><dt>Goal</dt><dd>{record.sourceGoal.name}</dd><dt>Goal version</dt><dd>{record.contribution.goalRevision}</dd><dt>Goal target amount</dt><dd>{record.sourceGoal.targetAmount} {record.sourceGoal.currency}</dd><dt>Goal target date</dt><dd>{record.sourceGoal.targetDate}</dd>{record.withdrawalReason && <><dt>Withdrawal reason</dt><dd>{record.withdrawalReason}</dd></>}<dt>Record version</dt><dd>{record.revision}</dd><dt>Recorded</dt><dd>{record.recordedAt}</dd></dl><p>Payment, allocation and achievement have not been verified. Progress and required contributions are not calculated.</p></section>;
}
function ContributionHistory({ api, record }: { api: AccessApi; record: ProductGoalContribution }) {
  const sequence = useRef(0); const pending = useRef<AbortController | null>(null); const [page, setPage] = useState<ProductGoalContributionHistory | null>(null); const [failure, setFailure] = useState(false); const [loading, setLoading] = useState(false); const [selected, setSelected] = useState<ProductGoalContribution | null>(null);
  const load = useCallback(async (before?: number, signal?: AbortSignal) => {
    const generation = ++sequence.current; pending.current?.abort(); const controller = new AbortController(); pending.current = controller; const abort = () => { controller.abort(); }; signal?.addEventListener('abort', abort, { once: true }); const active = () => !signal?.aborted && !controller.signal.aborted && generation === sequence.current;
    setPage(null); setFailure(false); setLoading(true); setSelected(null);
    try {
      const value = await getGoalContributionHistory(api, record.goalContributionId, before, controller.signal); const items = value.items;
      if (!Array.isArray(items) || items.length > 50 || typeof value.hasMore !== 'boolean' || items.some((item, i) => !bound(item) || item.goalContributionId !== record.goalContributionId || item.customerId !== record.customerId || item.contribution.financialGoalId !== record.contribution.financialGoalId || item.contribution.goalRevision !== record.contribution.goalRevision || !sameGoal(item.sourceGoal, record.sourceGoal) || before !== undefined && item.revision >= before || i > 0 && items[i - 1]?.revision !== item.revision + 1) || items.some((item, i) => item.declarationStatus === 'CUSTOMER_WITHDRAWN' && (before !== undefined || i !== 0 || item.revision !== record.revision || items.length < 2 || item.contribution.amount !== items[1]?.contribution.amount || item.contribution.contributionDate !== items[1].contribution.contributionDate)) || value.hasMore && (items.length !== 50 || value.nextBeforeRevision !== items.at(-1)?.revision) || !value.hasMore && (value.nextBeforeRevision !== null || items.length > 0 && items.at(-1)?.revision !== 1) || items.length === 0 && before !== 1) throw new Error('Invalid history');
      if (active()) setPage(value);
    } catch { if (active()) setFailure(true); } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, record]);
  useEffect(() => { const controller = new AbortController(); const timer = window.setTimeout(() => { void load(undefined, controller.signal); }, 0); return () => { window.clearTimeout(timer); controller.abort(); pending.current?.abort(); }; }, [load]);
  return <section aria-label="Goal contribution change history"><p>Each saved version retains the original goal version and currency.</p>{loading && <p role="status">Loading contribution history…</p>}{failure && <Alert type="error" title="Contribution history is unavailable." action={<Button onClick={() => { void load(); }}>Retry contribution history</Button>} />}
    {page?.items.map(item => <Card key={item.revision} size="small" style={{ marginBlock: 12 }}><strong>Version {item.revision}</strong><p>{money(item)} · {item.contribution.contributionDate} · Goal version {item.contribution.goalRevision}</p><Button onClick={() => { setSelected(item); }}>View version {item.revision}</Button></Card>)}{page?.hasMore && page.nextBeforeRevision !== null && <Button disabled={loading} onClick={() => { void load(page.nextBeforeRevision ?? undefined); }}>Earlier versions</Button>}{page && <Button disabled={loading} onClick={() => { void load(); }}>Latest versions</Button>}{selected && <ContributionDetails record={selected} />}
  </section>;
}

function ContributionWithdrawal({ api, record, open, onClose, onSaved }: { api: AccessApi; record: ProductGoalContribution; open: boolean; onClose: (uncertain: boolean) => void; onSaved: (record: ProductGoalContribution) => void }) {
  const active = useRef(true);
  useLayoutEffect(() => { active.current = true; return () => { active.current = false; }; }, []);
  const [reason, setReason] = useState('');
  const [submission, setSubmission] = useState<{ operationId: string; expectedRevision: number; reason: string } | null>(null);
  const [saving, setSaving] = useState(false); const [conflict, setConflict] = useState(false); const [error, setError] = useState('');
  const save = async () => {
    if (saving || conflict || !submission && !validReason(reason)) return;
    const input = submission ?? { operationId: crypto.randomUUID().replaceAll('-', ''), expectedRevision: record.revision, reason };
    setSubmission(input); setSaving(true); setError('');
    try {
      const receipt = await withdrawGoalContribution(api, record.goalContributionId, input);
      if (!bound(receipt) || receipt.declarationStatus !== 'CUSTOMER_WITHDRAWN' || receipt.withdrawalReason !== input.reason || receipt.operationId !== input.operationId || receipt.goalContributionId !== record.goalContributionId || receipt.customerId !== record.customerId || receipt.revision !== input.expectedRevision + 1 || receipt.contribution.financialGoalId !== record.contribution.financialGoalId || receipt.contribution.goalRevision !== record.contribution.goalRevision || receipt.contribution.amount !== record.contribution.amount || receipt.contribution.contributionDate !== record.contribution.contributionDate || !sameGoal(receipt.sourceGoal, record.sourceGoal)) throw new Error('Invalid withdrawal confirmation');
      if (active.current) onSaved(receipt);
    } catch (failure) {
      if (!active.current) return;
      const rejected = failure instanceof AccessApiError && (failure.status === 400 || failure.status === 409);
      if (rejected) { setSubmission(null); setConflict(failure.status === 409); }
      setError(rejected ? failure.status === 409 ? 'The report changed. Close, refresh contributions and review again.' : 'Review the withdrawal reason before trying again.' : 'Withdrawal has not been confirmed. Retry the same request before making another change.');
    } finally { if (active.current) setSaving(false); }
  };
  return <Drawer title="Withdraw contribution report" open={open} onClose={() => { onClose(Boolean(submission)); }} size={520} closable={!saving} mask={{ closable: !saving }} destroyOnHidden>
    <p>{money(record)} · {record.contribution.contributionDate} · {record.sourceGoal.name} · Goal version {record.contribution.goalRevision}</p>
    <p>Withdraw this customer report while retaining its saved versions. This does not reverse a payment or move money.</p>
    <label htmlFor="contribution-withdrawal-reason">Withdrawal reason</label><Input.TextArea id="contribution-withdrawal-reason" value={reason} maxLength={500} disabled={saving || Boolean(submission) || conflict} onChange={event => { setReason(event.target.value); }} />
    {error && <Alert type="error" title={error} />}<Button danger type="primary" loading={saving} disabled={conflict || !submission && !validReason(reason)} onClick={() => { void save(); }}>{submission ? 'Retry withdrawal confirmation' : 'Confirm withdrawal'}</Button>
  </Drawer>;
}
