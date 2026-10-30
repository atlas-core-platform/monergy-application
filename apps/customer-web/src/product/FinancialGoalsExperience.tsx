import GoalContributionsExperience from './GoalContributionsExperience';
import FinancialGoalProgressExperience from './FinancialGoalProgressExperience';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Input, Space, Tag } from 'antd';
import { AccessApiError, type AccessApi } from '../access/accessApi';
import { createFinancialGoal, getFinancialGoals, reviseFinancialGoal, getFinancialGoalHistory, reportFinancialGoalLifecycle, reportFinancialGoalPriority } from './productApi';
import type { ProductFinancialGoal, ProductFinancialGoalDraft, ProductFinancialGoalList, ProductFinancialGoalHistory, ProductFinancialGoalLifecycleStatus, ProductFinancialGoalPriority } from './productApi';

interface Submission { operationId: string; goal: ProductFinancialGoalDraft; expectedRevision?: number };
interface Values { name: string; targetAmount: string; currency: string; targetDate: string };
const empty: Values = { name: '', targetAmount: '', currency: '', targetDate: '' };
const validGoal = (goal: ProductFinancialGoalDraft | null | undefined) => typeof goal?.name === 'string' && goal.name.trim().length > 0 && goal.name.trim().length <= 120 && !Array.from(goal.name).some(char => char.charCodeAt(0) < 32 || char.charCodeAt(0) >= 127 && char.charCodeAt(0) <= 159) &&
  Number.isFinite(goal.targetAmount) && goal.targetAmount > 0 && goal.targetAmount <= 999999999999.99 && Number(goal.targetAmount.toFixed(2)) === goal.targetAmount &&
  /^[A-Z]{3}$/u.test(goal.currency) && /^(19|20|21)\d{2}-\d{2}-\d{2}$/u.test(goal.targetDate) && !Number.isNaN(Date.parse(goal.targetDate)) && new Date(goal.targetDate).toISOString().slice(0, 10) === goal.targetDate;
const statuses: ProductFinancialGoalLifecycleStatus[] = ['IN_PROGRESS', 'PAUSED', 'REPORTED_ACHIEVED', 'STOPPED'];
const statusLabel = (status: ProductFinancialGoalLifecycleStatus) => ({ IN_PROGRESS: 'In progress', PAUSED: 'Paused', REPORTED_ACHIEVED: 'Reported achieved', STOPPED: 'Stopped' })[status];
const priorities: ProductFinancialGoalPriority[] = ['NOT_SET', 'LOW', 'MEDIUM', 'HIGH'];
const priorityLabel = (priority: ProductFinancialGoalPriority) => ({ NOT_SET: 'Priority not set', LOW: 'Low priority', MEDIUM: 'Medium priority', HIGH: 'High priority' })[priority];
const validDate = (value: string | null) => value !== null && /^(19|20|21)\d{2}-\d{2}-\d{2}$/u.test(value) && !Number.isNaN(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value;
const bound = (record: ProductFinancialGoal | null | undefined) => record != null && typeof record.financialGoalId === 'string' && record.financialGoalId.length > 0 &&
  typeof record.customerId === 'string' && record.customerId.length > 0 && record.declarationStatus === 'CUSTOMER_DEFINED' && Reflect.get(record, 'lifecycleBasis') === 'CUSTOMER_REPORTED' &&
  statuses.includes(record.lifecycleStatus) && (record.lifecycleStatus === 'IN_PROGRESS' && record.lifecycleAsOfDate === null || validDate(record.lifecycleAsOfDate)) &&
  Reflect.get(record, 'priorityBasis') === 'CUSTOMER_REPORTED' && priorities.includes(record.priorityLevel) && (record.priorityAsOfDate === null ? record.priorityLevel === 'NOT_SET' : validDate(record.priorityAsOfDate)) &&
  Number.isInteger(record.revision) && record.revision > 0 && validGoal(record.goal);
const sameGoal = (a: ProductFinancialGoalDraft, b: ProductFinancialGoalDraft) => a.name === b.name.trim() && a.targetAmount === b.targetAmount && a.currency === b.currency && a.targetDate === b.targetDate;
const money = (goal: ProductFinancialGoalDraft) => new Intl.NumberFormat(undefined, { style: 'currency', currency: goal.currency }).format(goal.targetAmount);

export default function FinancialGoalsExperience({ api, canRead, canCreate }: { api: AccessApi; canRead: boolean; canCreate: boolean }) {
  const context = useMemo(() => ({ api, canRead, canCreate }), [api, canRead, canCreate]);
  const scope = useRef(context);
  useLayoutEffect(() => { scope.current = context; return () => { scope.current = { ...context, canRead: false, canCreate: false }; }; }, [context]);
  const sequence = useRef(0); const pending = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<{ context: typeof context; list: ProductFinancialGoalList } | null>(null);
  const [error, setError] = useState<{ context: typeof context; text: string } | null>(null);
  const [busy, setBusy] = useState<typeof context | null>(null);
  const [editor, setEditor] = useState<{ context: typeof context; open: boolean; values: Values; operationId: string; original: ProductFinancialGoal | null; submission: Submission | null; conflict: boolean } | null>(null);
  const [saving, setSaving] = useState<typeof context | null>(null);
  const [saveError, setSaveError] = useState<{ context: typeof context; text: string } | null>(null);
  const [saved, setSaved] = useState<{ context: typeof context; record: ProductFinancialGoal } | null>(null);
  const [history, setHistory] = useState<{ context: typeof context; record: ProductFinancialGoal } | null>(null);
  const [contributions,setContributions]=useState<{context:typeof context;record:ProductFinancialGoal}|null>(null);
  const [progress,setProgress]=useState<{context:typeof context;record:ProductFinancialGoal}|null>(null);
  const [lifecycle,setLifecycle]=useState<{context:typeof context;record:ProductFinancialGoal;status:ProductFinancialGoalLifecycleStatus;asOfDate:string;operationId:string;submission:{operationId:string;expectedRevision:number;status:ProductFinancialGoalLifecycleStatus;asOfDate:string}|null;conflict:boolean}|null>(null);
  const [lifecycleBusy,setLifecycleBusy]=useState<typeof context|null>(null);
  const [lifecycleError,setLifecycleError]=useState<{context:typeof context;text:string}|null>(null);
  const [priority,setPriority]=useState<{context:typeof context;record:ProductFinancialGoal;priority:ProductFinancialGoalPriority;asOfDate:string;operationId:string;submission:{operationId:string;expectedRevision:number;priority:ProductFinancialGoalPriority;asOfDate:string}|null;conflict:boolean}|null>(null);
  const [priorityBusy,setPriorityBusy]=useState<typeof context|null>(null);
  const [priorityError,setPriorityError]=useState<{context:typeof context;text:string}|null>(null);
  const [selected, setSelected] = useState<{ context: typeof context; record: ProductFinancialGoal } | null>(null);
  const load = useCallback(async (signal?: AbortSignal) => {
    if (!canRead) return;
    const generation = ++sequence.current; pending.current?.abort(); const controller = new AbortController(); pending.current = controller;
    const abort = () => { controller.abort(); }; signal?.addEventListener('abort', abort, { once: true });
    const active = () => scope.current === context && !signal?.aborted && !controller.signal.aborted && generation === sequence.current;
    setBusy(context); setError(null);
    try {
      const list = await getFinancialGoals(api, controller.signal);
      if (!Array.isArray(list.items) || list.items.length > 200 || typeof list.hasMore !== 'boolean' || list.hasMore && list.items.length !== 200 || list.items.some(item => !bound(item)) || new Set(list.items.map(item => item.financialGoalId)).size !== list.items.length) throw new Error('Invalid goal list');
      if (active()) setLoaded({ context, list });
    } catch (failure) { if (active()) { setLoaded(null); setError({ context, text: failure instanceof AccessApiError && failure.status === 403 ? 'Your current access does not include financial goals.' : 'Financial goals are unavailable. Try again.' }); } }
    finally { signal?.removeEventListener('abort', abort); if (active()) setBusy(null); }
  }, [api, canRead, context]);
  useEffect(() => { const controller = new AbortController(); const timer = window.setTimeout(() => { void load(controller.signal); }, 0); return () => { window.clearTimeout(timer); controller.abort(); pending.current?.abort(); }; }, [load]);
  const current = editor?.context === context ? editor : null; const isSaving = saving === context;
  const list = loaded?.context === context ? loaded.list : null;
  const draft = current ? { name: current.values.name, targetAmount: Number(current.values.targetAmount), currency: current.values.currency, targetDate: current.values.targetDate } : null;
  const valid = draft && current?.values.targetAmount !== '' && validGoal(draft) && !current?.conflict;
  const open = (record?: ProductFinancialGoal) => {
    if (!canCreate || isSaving) return;
    setSaveError(null); setSaved(null);
    if (current?.submission) { setEditor({ ...current, open: true }); return; }
    setSelected(null);
    setEditor({ context, open: true, values: record ? { ...record.goal, targetAmount: String(record.goal.targetAmount) } : empty,
      operationId: crypto.randomUUID().replaceAll('-', ''), original: record ?? null, submission: null, conflict: false });
  };
  const change = (key: keyof Values, value: string) => { if (current && !current.submission && !current.conflict && !isSaving) setEditor({ ...current, values: { ...current.values, [key]: value } }); };
  const save = async () => {
    if (!current || !draft || !canRead || !canCreate || isSaving || !current.submission && !valid) return;
    const submission: Submission = current.submission ?? { operationId: current.operationId, goal: draft, ...(current.original ? { expectedRevision: current.original.revision } : {}) };
    setEditor({ ...current, submission }); setSaving(context); setSaveError(null);
    try {
      const record = current.original && submission.expectedRevision !== undefined ? await reviseFinancialGoal(api, current.original.financialGoalId, { ...submission, expectedRevision: submission.expectedRevision }) : await createFinancialGoal(api, submission);
      if (!bound(record) || record.operationId !== submission.operationId || !sameGoal(record.goal, submission.goal) || record.revision !== (submission.expectedRevision ?? 0) + 1 || current.original && (record.financialGoalId !== current.original.financialGoalId || record.customerId !== current.original.customerId)) throw new Error('Invalid confirmation');
      if (scope.current !== context) return;
      setSaved({ context, record }); setEditor({ ...current, open: false, submission: null }); await load();
    } catch (failure) {
      if (scope.current !== context) return;
      const conflict = failure instanceof AccessApiError && failure.status === 409;
      const invalid = failure instanceof AccessApiError && failure.status === 400;
      setSaveError({ context, text: conflict ? 'The goal changed or this request conflicts with a saved change. Close this form, refresh goals and review before editing again.' : invalid ? 'Check the goal name, positive amount, three-letter currency and target date. Review and edit the form before saving again.' : 'The save has not been confirmed. Retry confirmation to check the same request before creating another goal.' });
      if (conflict || invalid) setEditor({ ...current, submission: null, conflict });
    } finally { setSaving(active => active === context ? null : active); }
  };
  const visible = selected?.context === context ? selected.record : null;
  const lifecycleCurrent=lifecycle?.context===context?lifecycle:null;const isLifecycleBusy=lifecycleBusy===context;
  const openLifecycle=(record:ProductFinancialGoal)=>{if(!canCreate||isLifecycleBusy)return;setLifecycleError(null);setLifecycle({context,record,status:record.lifecycleStatus==='IN_PROGRESS'?'PAUSED':'IN_PROGRESS',asOfDate:new Date().toISOString().slice(0,10),operationId:crypto.randomUUID().replaceAll('-',''),submission:null,conflict:false});};
  const saveLifecycle=async()=>{if(!lifecycleCurrent||!canCreate||isLifecycleBusy||lifecycleCurrent.conflict||!validDate(lifecycleCurrent.asOfDate)||lifecycleCurrent.status===lifecycleCurrent.record.lifecycleStatus)return;
    const submission=lifecycleCurrent.submission??{operationId:lifecycleCurrent.operationId,expectedRevision:lifecycleCurrent.record.revision,status:lifecycleCurrent.status,asOfDate:lifecycleCurrent.asOfDate};
    setLifecycle({...lifecycleCurrent,submission});setLifecycleBusy(context);setLifecycleError(null);
    try{const result=await reportFinancialGoalLifecycle(api,lifecycleCurrent.record.financialGoalId,submission);if(!bound(result)||result.financialGoalId!==lifecycleCurrent.record.financialGoalId||result.customerId!==lifecycleCurrent.record.customerId||result.operationId!==submission.operationId||result.revision!==submission.expectedRevision+1||result.lifecycleStatus!==submission.status||result.lifecycleAsOfDate!==submission.asOfDate||Reflect.get(result,'lifecycleBasis')!=='CUSTOMER_REPORTED'||!sameGoal(result.goal,lifecycleCurrent.record.goal))throw new Error('Invalid confirmation');if(scope.current!==context)return;setSaved({context,record:result});setLifecycle(null);await load();}
    catch(failure){if(scope.current!==context)return;const conflict=failure instanceof AccessApiError&&failure.status===409;const invalid=failure instanceof AccessApiError&&failure.status===400;setLifecycleError({context,text:conflict?'The goal changed. Refresh goals and review its latest status before trying again.':invalid?'Choose a different status and a valid effective date.':'The status change has not been confirmed. Retry the same request before making another change.'});if(conflict||invalid)setLifecycle({...lifecycleCurrent,submission:null,conflict});}
    finally{setLifecycleBusy(active=>active===context?null:active);}};
  const priorityCurrent=priority?.context===context?priority:null;const isPriorityBusy=priorityBusy===context;
  const openPriority=(record:ProductFinancialGoal)=>{if(!canCreate||isPriorityBusy||!['IN_PROGRESS','PAUSED'].includes(record.lifecycleStatus))return;setPriorityError(null);setPriority({context,record,priority:record.priorityLevel==='NOT_SET'?'MEDIUM':record.priorityLevel==='HIGH'?'MEDIUM':'HIGH',asOfDate:new Date().toISOString().slice(0,10),operationId:crypto.randomUUID().replaceAll('-',''),submission:null,conflict:false});};
  const savePriority=async()=>{if(!priorityCurrent||!canCreate||isPriorityBusy||priorityCurrent.conflict||!validDate(priorityCurrent.asOfDate)||priorityCurrent.priority===priorityCurrent.record.priorityLevel)return;
    const submission=priorityCurrent.submission??{operationId:priorityCurrent.operationId,expectedRevision:priorityCurrent.record.revision,priority:priorityCurrent.priority,asOfDate:priorityCurrent.asOfDate};
    setPriority({...priorityCurrent,submission});setPriorityBusy(context);setPriorityError(null);
    try{const result=await reportFinancialGoalPriority(api,priorityCurrent.record.financialGoalId,submission);if(!bound(result)||result.financialGoalId!==priorityCurrent.record.financialGoalId||result.customerId!==priorityCurrent.record.customerId||result.operationId!==submission.operationId||result.revision!==submission.expectedRevision+1||result.priorityLevel!==submission.priority||result.priorityAsOfDate!==submission.asOfDate||result.priorityBasis!=='CUSTOMER_REPORTED'||result.lifecycleStatus!==priorityCurrent.record.lifecycleStatus||result.lifecycleAsOfDate!==priorityCurrent.record.lifecycleAsOfDate||!sameGoal(result.goal,priorityCurrent.record.goal))throw new Error('Invalid confirmation');if(scope.current!==context)return;setSaved({context,record:result});setPriority(null);await load();}
    catch(failure){if(scope.current!==context)return;const conflict=failure instanceof AccessApiError&&failure.status===409;const invalid=failure instanceof AccessApiError&&failure.status===400;setPriorityError({context,text:conflict?'The goal changed. Refresh goals and review its latest priority before trying again.':invalid?'Choose a different priority and a valid effective date.':'The priority change has not been confirmed. Retry the same request before making another change.'});if(conflict||invalid)setPriority({...priorityCurrent,submission:null,conflict});}
    finally{setPriorityBusy(active=>active===context?null:active);}};
  if (!canRead) return <Alert type="info" title="Financial goals are not included in your current access." />;
  return <section aria-label="Financial goals" style={{ marginBlock: 24 }}>
    <Space wrap><h2>Financial goals</h2><Button disabled={busy === context || isSaving} onClick={() => { void load(); }}>Refresh goals</Button>
      {canCreate && <Button disabled={isSaving} onClick={() => { open(); }}>{current?.submission ? 'Continue save confirmation' : 'Add goal'}</Button>}</Space>
    <p>Set your own goal amount and target date. Progress can use your saved contribution reports; future required contributions are not calculated.</p>
    {saved?.context === context && <Alert type="success" title={'Goal saved: ' + saved.record.goal.name} />}
    {error?.context === context && <Alert type="error" title={error.text} action={<Button onClick={() => { void load(); }}>Retry goals</Button>} />}
    {busy === context && <p role="status">Loading goals…</p>}{list?.items.length === 0 && <p>No financial goals added yet. Add a goal you want to work towards.</p>}
    {list?.hasMore && <Alert type="info" title="Showing the 200 most recently added goals." />}
    {list?.items.map(record => <Card key={record.financialGoalId} size="small" style={{ marginBlock: 12 }}><Space wrap><strong>{record.goal.name}</strong><Tag>Customer defined</Tag><Tag>{statusLabel(record.lifecycleStatus)}</Tag><Tag>{priorityLabel(record.priorityLevel)}</Tag></Space>
      <p>Goal amount: {money(record.goal)} · Target date: {record.goal.targetDate}</p>
      <Button onClick={() => { setSelected({ context, record }); }}>View goal: {record.goal.name}</Button>
      {canCreate && <Button disabled={isSaving || Boolean(current?.submission)} onClick={() => { open(record); }}>Edit goal: {record.goal.name}</Button>}
      {canCreate && <Button disabled={isLifecycleBusy} onClick={()=>{openLifecycle(record);}}>Update status: {record.goal.name}</Button>}
      {canCreate && ['IN_PROGRESS','PAUSED'].includes(record.lifecycleStatus) && <Button disabled={isPriorityBusy} onClick={()=>{openPriority(record);}}>Set priority: {record.goal.name}</Button>}
      <Button onClick={() => {setProgress({context,record});}}>View progress: {record.goal.name}</Button>
      <Button onClick={() => {setContributions({context,record});}}>View contributions: {record.goal.name}</Button>
      <Button onClick={() => { setHistory({ context, record }); }}>View goal history: {record.goal.name}</Button></Card>)}
    <Drawer title={current?.original ? 'Edit goal' : 'Add goal'} open={Boolean(current?.open && canCreate)} onClose={() => { if (current) setEditor({ ...current, open: false }); }} size={520} closable={!isSaving} mask={{ closable: !isSaving }} destroyOnHidden>
      {current?.original && <p>Editing version {current.original.revision}. Earlier versions remain available in history.</p>}
      {saveError?.context === context && <Alert type="error" title={saveError.text} />}
      <fieldset disabled={isSaving || Boolean(current?.submission) || current?.conflict} style={{ border: 0, padding: 0 }}>
        <div style={{ marginBlock: 14 }}><label htmlFor="goal-name">Goal name</label><Input id="goal-name" maxLength={120} value={current?.values.name ?? ''} onChange={event => { change('name', event.target.value); }} /></div>
        <div style={{ marginBlock: 14 }}><label htmlFor="goal-amount">Goal amount</label><Input id="goal-amount" type="number" min={0.01} max={999999999999.99} step={0.01} value={current?.values.targetAmount ?? ''} onChange={event => { change('targetAmount', event.target.value); }} /></div>
        <div style={{ marginBlock: 14 }}><label htmlFor="goal-currency">Currency</label><Input id="goal-currency" maxLength={3} placeholder="e.g. INR" value={current?.values.currency ?? ''} onChange={event => { change('currency', event.target.value.toUpperCase()); }} /></div>
        <div style={{ marginBlock: 14 }}><label htmlFor="goal-date">Target date</label><Input id="goal-date" type="date" min="1900-01-01" max="2199-12-31" value={current?.values.targetDate ?? ''} onChange={event => { change('targetDate', event.target.value); }} /></div>
      </fieldset>
      <p>Saving records the objective you defined. It does not allocate savings or confirm that the goal has been achieved.</p>
      <Button type="primary" loading={isSaving} disabled={!canCreate || !current?.submission && !valid} onClick={() => { void save(); }}>{current?.submission ? 'Retry save confirmation' : current?.original ? 'Save goal changes' : 'Save goal'}</Button>
    </Drawer>
    <Drawer title="Goal details" open={Boolean(visible)} onClose={() => { setSelected(null); }} size={520} destroyOnHidden>
      {visible && <GoalDetails record={visible} />}
    </Drawer>
    <Drawer title="Update goal status" open={Boolean(lifecycleCurrent)} onClose={()=>{if(!isLifecycleBusy)setLifecycle(null);}} size={520} closable={!isLifecycleBusy} mask={{closable:!isLifecycleBusy}} destroyOnHidden>
      {lifecycleCurrent&&<><p>Current status: <strong>{statusLabel(lifecycleCurrent.record.lifecycleStatus)}</strong>. This records what you report; Monergy does not independently confirm achievement.</p>
        {lifecycleError?.context===context&&<Alert type="error" title={lifecycleError.text}/>}<fieldset disabled={isLifecycleBusy||Boolean(lifecycleCurrent.submission)||lifecycleCurrent.conflict} style={{border:0,padding:0}}>
          <div style={{marginBlock:14}}><label htmlFor="goal-lifecycle-status">New status</label><select id="goal-lifecycle-status" value={lifecycleCurrent.status} onChange={event=>{setLifecycle({...lifecycleCurrent,status:event.target.value as ProductFinancialGoalLifecycleStatus});}}>{statuses.filter(status=>status!==lifecycleCurrent.record.lifecycleStatus).map(status=><option key={status} value={status}>{statusLabel(status)}</option>)}</select></div>
          <div style={{marginBlock:14}}><label htmlFor="goal-lifecycle-date">Effective date</label><Input id="goal-lifecycle-date" type="date" min={lifecycleCurrent.record.lifecycleAsOfDate??'1900-01-01'} max={new Date().toISOString().slice(0,10)} value={lifecycleCurrent.asOfDate} onChange={event=>{setLifecycle({...lifecycleCurrent,asOfDate:event.target.value});}}/></div>
        </fieldset><Button type="primary" loading={isLifecycleBusy} disabled={!validDate(lifecycleCurrent.asOfDate)||lifecycleCurrent.status===lifecycleCurrent.record.lifecycleStatus||lifecycleCurrent.conflict} onClick={()=>{void saveLifecycle();}}>{lifecycleCurrent.submission?'Retry status confirmation':'Save status'}</Button></>}
    </Drawer>
    <Drawer title="Set goal priority" open={Boolean(priorityCurrent)} onClose={()=>{if(!isPriorityBusy)setPriority(null);}} size={520} closable={!isPriorityBusy} mask={{closable:!isPriorityBusy}} destroyOnHidden>
      {priorityCurrent&&<><p>Current priority: <strong>{priorityLabel(priorityCurrent.record.priorityLevel)}</strong>. Priority is what you report; it is not financial advice, a score, a required contribution, or proof of achievement.</p>
        {priorityError?.context===context&&<Alert type="error" title={priorityError.text}/>}<fieldset disabled={isPriorityBusy||Boolean(priorityCurrent.submission)||priorityCurrent.conflict} style={{border:0,padding:0}}>
          <div style={{marginBlock:14}}><label htmlFor="goal-priority">Priority</label><select id="goal-priority" value={priorityCurrent.priority} onChange={event=>{setPriority({...priorityCurrent,priority:event.target.value as ProductFinancialGoalPriority});}}>{priorities.filter(value=>value!==priorityCurrent.record.priorityLevel).map(value=><option key={value} value={value}>{priorityLabel(value)}</option>)}</select></div>
          <div style={{marginBlock:14}}><label htmlFor="goal-priority-date">Effective date</label><Input id="goal-priority-date" type="date" min={priorityCurrent.record.priorityAsOfDate??'1900-01-01'} max={new Date().toISOString().slice(0,10)} value={priorityCurrent.asOfDate} onChange={event=>{setPriority({...priorityCurrent,asOfDate:event.target.value});}}/></div>
        </fieldset><Button type="primary" loading={isPriorityBusy} disabled={!validDate(priorityCurrent.asOfDate)||priorityCurrent.priority===priorityCurrent.record.priorityLevel||priorityCurrent.conflict} onClick={()=>{void savePriority();}}>{priorityCurrent.submission?'Retry priority confirmation':'Save priority'}</Button></>}
    </Drawer>
    <Drawer title="Goal contributions" open={contributions?.context === context} onClose={() => {setContributions(null);}} size={620} destroyOnHidden>
      {contributions?.context === context && <GoalContributionsExperience key={contributions.record.financialGoalId} api={api} goal={list?.items.find(item=>item.financialGoalId===contributions.record.financialGoalId) ?? contributions.record} canRead={canRead} canCreate={canCreate} />}
    </Drawer>
    <Drawer title="Goal progress" open={progress?.context === context} onClose={() => {setProgress(null);}} size={620} destroyOnHidden>
      {progress?.context === context && <FinancialGoalProgressExperience key={`${progress.record.financialGoalId}:${String(progress.record.revision)}`} api={api} goal={list?.items.find(item=>item.financialGoalId===progress.record.financialGoalId) ?? progress.record} />}
    </Drawer>
    <Drawer title="Goal history" open={history?.context === context} onClose={() => { setHistory(null); }} size={520} destroyOnHidden>
      {history?.context === context && <GoalHistory key={history.record.financialGoalId} api={api} record={history.record} />}
    </Drawer>
  </section>;
}
function GoalDetails({ record }: { record: ProductFinancialGoal }) {
  return <section aria-label={'Saved goal version ' + String(record.revision)}><h3>{record.goal.name}</h3><Tag>Customer defined</Tag><Tag>{statusLabel(record.lifecycleStatus)}</Tag><Tag>{priorityLabel(record.priorityLevel)}</Tag><dl>
    <dt>Goal amount</dt><dd>{money(record.goal)}</dd><dt>Currency</dt><dd>{record.goal.currency}</dd><dt>Target date</dt><dd>{record.goal.targetDate}</dd>
    <dt>Reported status</dt><dd>{statusLabel(record.lifecycleStatus)}</dd><dt>Status effective date</dt><dd>{record.lifecycleAsOfDate??'Not recorded'}</dd><dt>Status basis</dt><dd>Customer reported</dd>
    <dt>Reported priority</dt><dd>{priorityLabel(record.priorityLevel)}</dd><dt>Priority effective date</dt><dd>{record.priorityAsOfDate??'Not recorded'}</dd><dt>Priority basis</dt><dd>Customer reported</dd>
    <dt>Record version</dt><dd>{record.revision}</dd><dt>Recorded</dt><dd>{record.recordedAt}</dd></dl><p>Progress is available from saved customer-reported contributions. Future required contributions and independent achievement are not calculated.</p></section>;
}
function GoalHistory({ api, record }: { api: AccessApi; record: ProductFinancialGoal }) {
  const sequence = useRef(0); const pending = useRef<AbortController | null>(null);
  const [page, setPage] = useState<ProductFinancialGoalHistory | null>(null); const [failure, setFailure] = useState(false); const [loading, setLoading] = useState(false); const [selected, setSelected] = useState<ProductFinancialGoal | null>(null);
  const load = useCallback(async (before?: number, signal?: AbortSignal) => {
    const generation = ++sequence.current; pending.current?.abort(); const controller = new AbortController(); pending.current = controller;
    const abort = () => { controller.abort(); }; signal?.addEventListener('abort', abort, { once: true });
    const active = () => !signal?.aborted && !controller.signal.aborted && generation === sequence.current;
    setPage(null); setFailure(false); setLoading(true); setSelected(null);
    try {
      const value = await getFinancialGoalHistory(api, record.financialGoalId, before, controller.signal); const items = value.items;
      if (!Array.isArray(items) || items.length > 50 || typeof value.hasMore !== 'boolean' || items.some((item, i) => !bound(item) || item.financialGoalId !== record.financialGoalId || item.customerId !== record.customerId || before !== undefined && item.revision >= before || i > 0 && items[i - 1]?.revision !== item.revision + 1) ||
        value.hasMore && (items.length !== 50 || value.nextBeforeRevision !== items.at(-1)?.revision) || !value.hasMore && (value.nextBeforeRevision !== null || items.length > 0 && items.at(-1)?.revision !== 1) || items.length === 0 && before !== 1) throw new Error('Invalid history');
      if (active()) setPage(value);
    } catch { if (active()) setFailure(true); } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, record]);
  useEffect(() => { const controller = new AbortController(); const timer = window.setTimeout(() => { void load(undefined, controller.signal); }, 0); return () => { window.clearTimeout(timer); controller.abort(); pending.current?.abort(); }; }, [load]);
  return <section aria-label="Financial goal change history"><h3>{record.goal.name}</h3><p>Each saved version retains your objective, amount, currency, target date, reported status and reported priority.</p>
    {loading && <p role="status">Loading goal history…</p>}{failure && <Alert type="error" title="Goal history is unavailable." action={<Button onClick={() => { void load(); }}>Retry goal history</Button>} />}
    {page?.items.map(item => <Card key={item.revision} size="small" style={{ marginBlock: 12 }}><strong>Version {item.revision}</strong><p>{item.goal.name} · {money(item.goal)} · {item.goal.targetDate} · {statusLabel(item.lifecycleStatus)} · {priorityLabel(item.priorityLevel)}</p><Button onClick={() => { setSelected(item); }}>View version {item.revision}</Button></Card>)}
    {page?.hasMore && page.nextBeforeRevision !== null && <Button disabled={loading} onClick={() => { void load(page.nextBeforeRevision ?? undefined); }}>Earlier versions</Button>}
    {page && <Button disabled={loading} onClick={() => { void load(); }}>Latest versions</Button>}{selected && <GoalDetails record={selected} />}
  </section>;
}
