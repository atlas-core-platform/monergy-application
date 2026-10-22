import FinancialSummarySources from './FinancialSummarySources';
import { Alert, Button, Checkbox, Collapse, Input, Select, Tag } from 'antd';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';
import { AccessApiError } from '../access/accessApi';
import { listEvidence } from './productApi';
import { getNetWorthPositions, validNetWorthPage, validNetWorthPosition } from './netWorthApi';
import './product.css';
interface Configuration { available: boolean; revision: string; providerLabel: string; notice: string }
interface Claim { id: string; text: string; kind: string; sourceId: string; revision: number; provenanceId: string; documentVersionId: string; sourceLocation?: string | null; calculationId?: string | null; ruleVersion?: string | null }
interface Answer { answerId: string; operationId: string; question: string; state: string; introduction: string; claims: Claim[]; recordedAt: string; model: string; promptVersion: string; configurationRevision: string }
interface View { scope: string; configuration?: Configuration; answers: Answer[]; error: string; busy: boolean }
const uuid = () => crypto.randomUUID().replaceAll('-', '');
function validAnswer(answer: Answer | null | undefined) {
  return answer && /^[a-f0-9]{32}$/.test(answer.answerId) && ['SOURCES', 'INSUFFICIENT_DATA', 'UNSUPPORTED', 'NOT_COMPLETED'].includes(answer.state) &&
    typeof answer.question === 'string' && typeof answer.introduction === 'string' && Array.isArray(answer.claims) && answer.claims.length <= 10 &&
    Number.isFinite(Date.parse(answer.recordedAt)) && answer.claims.every((c: Claim | null) => c != null && ['FinancialFact', 'DocumentExcerpt', 'Calculation', 'MonthlyCalculation'].includes(c.kind) && typeof c.text === 'string' &&
      typeof c.sourceId === 'string' && typeof c.documentVersionId === 'string' && Number.isInteger(c.revision) && c.revision > 0);
}
export default function FinancialQaExperience() {
  const { session, authorization } = useWorkspaceSession();
  return <FinancialQaPanel key={JSON.stringify([session, authorization])} />;
}
function FinancialQaPanel() {
  const { api, session, authorization } = useWorkspaceSession();
  const customer = authorization?.area === 'product' ? authorization.context : null;
  const scope = JSON.stringify([session, customer?.customerId, customer?.capabilities]);
  const allowed = ['ai-intelligence.analysis.execute', 'search.query.execute', 'financial-profile.profile.read', 'evidence.document.read']
    .every(capability => customer?.capabilities.includes(capability));
  const [view, setView] = useState<View | null>(null);
  const [question, setQuestion] = useState(''); const [permission, setPermission] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [summary, setSummary] = useState({ net: '', cash: '' });
  const [basis, setBasis] = useState(() => new URLSearchParams(window.location.search).get('mode') === 'summary' && customer?.capabilities.includes('financial-rules.calculation.execute') ? 'summary' : 'financial'); const [source, setSource] = useState<string | undefined>();
  const [choices, setChoices] = useState<{ value: string; label: string }[]>([]); const [next, setNext] = useState<string | undefined>();
  const currentScope = useRef(scope); const active = useRef<AbortController | null>(null);
  const operation = useRef<{ question: string; revision: string; source: string; id: string } | null>(null);
  useLayoutEffect(() => { currentScope.current = scope; }, [scope]);
  const current = view?.scope === scope ? view : null;
  useEffect(() => {
    const controller = new AbortController(); active.current?.abort(); active.current = controller;
    operation.current = null;
    if (!api || !allowed) return () => { controller.abort(); };
    void (async () => {
      try {
        const configuration = await api.product<Configuration>('v1/financial-qa/configuration', 'GET', undefined, controller.signal);
        if (typeof configuration.available !== 'boolean' || typeof configuration.notice !== 'string' ||
            configuration.available && !/^[a-f0-9]{64}$/.test(configuration.revision)) throw new Error('Invalid response');
        const answers = await api.product<Answer[]>('v1/financial-qa/history', 'GET', undefined, controller.signal);
        if (!Array.isArray(answers) || answers.length > 20 || !answers.every(validAnswer)) throw new Error('Invalid response');
        if (!controller.signal.aborted && currentScope.current === scope) setView({ scope, configuration, answers, busy: false, error: '' });
      } catch {
        if (!controller.signal.aborted && currentScope.current === scope) setView({ scope, answers: [], busy: false, error: 'Q&A could not be loaded. Check your access and try Refresh.' });
      }
    })();
    return () => { controller.abort(); };
  }, [api, allowed, scope, refresh]);
  const chooseBasis = async (value: string, cursor?: string) => {
    if (!api || !allowed || !current || current.busy) return;
    setBasis(value); setSummary({ net: '', cash: '' }); setSource(undefined); setPermission(false); operation.current = null; setNext(undefined);
    if (!cursor) setChoices([]);
    if (value === 'financial' || value === 'summary') return;
    const controller = new AbortController(); active.current?.abort(); active.current = controller;
    setView({ ...current, busy: true, error: '' });
    try {
      let options: { value: string; label: string }[]; let nextCursor: string | undefined;
      if (value === 'document') {
        const page = await listEvidence(api, controller.signal);
        if (!Array.isArray(page.items) || page.items.some(item => typeof item.documentVersionId !== 'string' || typeof item.originalFileName !== 'string')) throw new Error('Invalid sources');
        options = page.items.map(item => ({ value: item.documentVersionId, label: item.originalFileName + ' · version ' + String(item.version) }));
      } else {
        const page = await getNetWorthPositions(api, cursor, controller.signal);
        if (!validNetWorthPage(page, item => validNetWorthPosition(item, customer?.customerId ?? ''), item => item.positionId)) throw new Error('Invalid positions');
        options = page.items.map(item => ({ value: item.positionId, label: item.position.asOfDate + ' · ' + item.position.currency + ' · ' + item.recordedAt }));
        nextCursor = page.nextBeforeId ?? undefined;
      }
      if (!controller.signal.aborted && currentScope.current === scope) {
        setChoices(previous => cursor ? [...previous, ...options.filter(option => !previous.some(old => old.value === option.value))] : options); setNext(nextCursor); setView({ ...current, busy: false });
      }
    } catch { if (!controller.signal.aborted && currentScope.current === scope) setView({ ...current, busy: false, error: 'The selected sources could not be loaded. Check your access and retry.' }); }
  };
  const requestQuestion = basis === 'summary' ? 'Summarize my selected reviewed net worth and monthly income and expenses.' : question.trim();
  const missingSource = basis === 'summary' ? !summary.net || !summary.cash : basis !== 'financial' && !source;
  const ask = async () => {
    const configuration = current?.configuration;
    if (!current || !api || !allowed || !configuration?.available || !permission || !requestQuestion || current.busy || missingSource) return;
    const controller = new AbortController(); active.current?.abort(); active.current = controller;
    const text = requestQuestion; const sourceKey = basis + ':' + (basis === 'summary' ? summary.net + ':' + summary.cash : source ?? '');
    if (operation.current?.question !== text || operation.current.revision !== configuration.revision || operation.current.source !== sourceKey)
      operation.current = { question: text, revision: configuration.revision, source: sourceKey, id: uuid() };
    setView({ ...current, busy: true, error: '' });
    try {
      const answer = await api.product<Answer>('v1/financial-qa/questions', 'POST', {
        operationId: operation.current.id, question: text, configurationRevision: configuration.revision, allowModelProcessing: true,
        ...(basis === 'summary' ? { summaryNetWorthCalculationId: summary.net, summaryCashflowCalculationId: summary.cash } : basis === 'document' ? { documentVersionId: source } : basis === 'net-worth' ? { netWorthPositionId: source } : {}),
      }, controller.signal);
      if (!validAnswer(answer) || answer.question !== text || answer.operationId !== operation.current.id || basis === 'summary' && answer.state === 'SOURCES' && (answer.claims.length !== 6 || new Set(answer.claims.map(c => c.id)).size !== 6 || answer.claims.filter(c => c.kind === 'Calculation' && c.calculationId === summary.net).length !== 3 || answer.claims.filter(c => c.kind === 'MonthlyCalculation' && c.calculationId === summary.cash).length !== 3)) throw new Error('Invalid response');
      if (!controller.signal.aborted && currentScope.current === scope) {
        setView({ ...current, busy: false, answers: [answer, ...current.answers.filter(a => a.answerId !== answer.answerId)].slice(0, 20) });
        setPermission(false); operation.current = null;
      }
    } catch (error) {
      if (!controller.signal.aborted && currentScope.current === scope) {
        setView({ ...current, busy: false, answers: [], error: error instanceof AccessApiError && error.status === 409
          ? 'The provider settings or request changed. Refresh and review permission again.'
          : 'No answer could be confirmed. Refresh to check the saved attempt before submitting again.' });
        setPermission(false);
      }
    }
  };
  return <main className="product-page">
    <header className="product-heading"><div><p className="workspace-eyebrow">FINANCIAL Q&A</p><h1>Financial Q&A and summary</h1>
      <p>Ask about financial records, passages from a selected document, or a Financial Rules calculation from a reviewed Net worth position. Create a financial summary from selected month-end reviews. Answers cite their exact sources. No external verification, advice or forecasts.</p></div><Tag>LOCAL · MODEL CONFIGURATION REQUIRED</Tag></header>
    {!allowed && <Alert type="warning" showIcon title="Q&A access is not enabled" description="Your organization can grant Customer AI analysis alongside search, financial-profile and document read access." />}
    {current?.error && <Alert type="error" showIcon title={current.error} />}
    {allowed && <section className="product-panel" aria-busy={current?.busy ?? true}>
      <Button disabled={current?.busy} onClick={() => { setPermission(false); setQuestion(''); setBasis('financial'); setSource(undefined); setChoices([]); setNext(undefined); setView({ scope, answers: [], busy: true, error: '' }); setRefresh(v => v + 1); }}>Refresh</Button>
      {current?.configuration && <><h2>{current.configuration.available ? current.configuration.providerLabel : 'Provider not configured'}</h2><p>{current.configuration.notice}</p></>}
      <label htmlFor="qa-basis">Answer source</label>
      <Select id="qa-basis" aria-label="Answer source" value={basis} disabled={(current?.busy ?? true) || !current?.configuration?.available} onChange={value => { void chooseBasis(value); }} options={[
        { value: 'summary', label: 'Financial summary of reviewed inventories', disabled: !customer?.capabilities.includes('financial-rules.calculation.execute') },
        { value: 'financial', label: 'Financial source records' }, { value: 'document', label: 'Selected document' },
        { value: 'net-worth', label: 'Calculate reviewed net worth', disabled: !customer?.capabilities.includes('financial-rules.calculation.execute') },
      ]} />
      {basis === 'summary' && api && current?.configuration?.available && <FinancialSummarySources key={scope} api={api} customerId={customer?.customerId ?? ''} disabled={current.busy} onChange={(net, cash) => { setSummary({ net, cash }); setPermission(false); operation.current = null; }} />}
      {basis !== 'financial' && basis !== 'summary' && <><label htmlFor="qa-source">{basis === 'document' ? 'Document version' : 'Reviewed position'}</label><Select id="qa-source" aria-label="Selected source" value={source} options={choices} disabled={current?.busy} placeholder="Choose a source" onChange={value => { setSource(value); setPermission(false); operation.current = null; }} />
        {next && <Button disabled={current?.busy} onClick={() => { void chooseBasis(basis, next); }}>Load older positions</Button>}
        <p>{basis === 'document' ? 'Text/CSV and supported spreadsheet templates; PNG/JPEG and up to 20 unlocked PDF pages use recorded OCR. Process scans first. Missing, oversized or uncertain text returns insufficient information. Quotations and OCR are not verified financial facts.' : 'Submitting evaluates the selected inventory using Financial Rules. Complete assets and liabilities, one date and one currency are required. The model does not calculate.'}</p></>}
      {basis !== 'summary' && <><label htmlFor="financial-question">Your question</label>
      <Input.TextArea id="financial-question" value={current ? question : ''} maxLength={1000} rows={3} disabled={!current?.configuration?.available || current.busy}
        placeholder="Which source records mention salary?" onChange={event => { setQuestion(event.target.value); setPermission(false); operation.current = null; }} /></>}
      <Checkbox checked={!!current && permission} disabled={!current?.configuration?.available || current.busy} onChange={event => { setPermission(event.target.checked); }}>
        For this question, allow Monergy to send my question and the selected document passages, source amounts or calculation results to the provider above. If I selected a reviewed position, also run its Financial Rules calculation. The question, answer and source trace will be retained in this workspace.
      </Checkbox><p><Button type="primary" loading={current?.busy} disabled={!current?.configuration?.available || !permission || !requestQuestion || missingSource} onClick={() => { void ask(); }}>{basis === 'summary' ? 'Create financial summary' : 'Ask question'}</Button></p>
    </section>}
    <section aria-label="Q&A history" aria-live="polite"><h2>Recent answers</h2><p>Up to 20 recent attempts. Answers describe source revisions at the recorded time; they are not live balances.</p>
      {current?.answers.map(answer => <article className="product-panel" key={answer.answerId}><h3>{answer.question}</h3><p>{answer.introduction}</p><time dateTime={answer.recordedAt}>{new Date(answer.recordedAt).toLocaleString()}</time>
        {answer.claims.map(claim => <div key={claim.id}><p>{claim.text}</p>{claim.kind === 'FinancialFact' && <><a href={'/finances?fact=' + encodeURIComponent(claim.sourceId)}>View source & history (revision {claim.revision})</a>{' · '}</>}{claim.kind !== 'Calculation' && claim.kind !== 'MonthlyCalculation' && <a href={'/evidence?version=' + encodeURIComponent(claim.documentVersionId)}>Open source document version</a>}{claim.kind === 'Calculation' && <a href={'/net-worth?calculation=' + encodeURIComponent(claim.calculationId ?? '')}>Open exact calculation</a>}{claim.kind === 'MonthlyCalculation' && <a href={'/monthly-cashflow?calculation=' + encodeURIComponent(claim.calculationId ?? '')}>Open exact monthly calculation</a>}{claim.sourceLocation && <p>Source: {claim.sourceLocation}{claim.ruleVersion ? ' · Rule ' + claim.ruleVersion : ''}</p>}</div>)}
        <Collapse items={[{ key: 'trace', label: 'Answer details', children: <dl><dt>Answer ID</dt><dd>{answer.answerId}</dd><dt>Model</dt><dd>{answer.model || 'No completed model response'}</dd><dt>Prompt version</dt><dd>{answer.promptVersion}</dd></dl> }]} />
      </article>)}
    </section>
  </main>;
}
