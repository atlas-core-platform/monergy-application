import { Alert, Button, Checkbox, Collapse, Input, Tag } from 'antd';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';
import { AccessApiError } from '../access/accessApi';
import './product.css';
interface Configuration { available: boolean; revision: string; providerLabel: string; notice: string }
interface Claim { id: string; text: string; kind: string; sourceId: string; revision: number; provenanceId: string; documentVersionId: string }
interface Answer { answerId: string; operationId: string; question: string; state: string; introduction: string; claims: Claim[]; recordedAt: string; model: string; promptVersion: string; configurationRevision: string }
interface View { scope: string; configuration?: Configuration; answers: Answer[]; error: string; busy: boolean }
const uuid = () => crypto.randomUUID().replaceAll('-', '');
function validAnswer(answer: Answer | null | undefined) {
  return answer && /^[a-f0-9]{32}$/.test(answer.answerId) && ['SOURCES', 'INSUFFICIENT_DATA', 'UNSUPPORTED', 'NOT_COMPLETED'].includes(answer.state) &&
    typeof answer.question === 'string' && typeof answer.introduction === 'string' && Array.isArray(answer.claims) && answer.claims.length <= 10 &&
    Number.isFinite(Date.parse(answer.recordedAt)) && answer.claims.every((c: Claim | null) => c?.kind === 'FinancialFact' && typeof c.text === 'string' &&
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
  const currentScope = useRef(scope); const active = useRef<AbortController | null>(null);
  const operation = useRef<{ question: string; revision: string; id: string } | null>(null);
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
  const ask = async () => {
    const configuration = current?.configuration;
    if (!current || !api || !allowed || !configuration?.available || !permission || !question.trim() || current.busy) return;
    const controller = new AbortController(); active.current?.abort(); active.current = controller;
    const text = question.trim();
    if (operation.current?.question !== text || operation.current.revision !== configuration.revision)
      operation.current = { question: text, revision: configuration.revision, id: uuid() };
    setView({ ...current, busy: true, error: '' });
    try {
      const answer = await api.product<Answer>('v1/financial-qa/questions', 'POST', {
        operationId: operation.current.id, question: text, configurationRevision: configuration.revision, allowModelProcessing: true,
      }, controller.signal);
      if (!validAnswer(answer) || answer.question !== text || answer.operationId !== operation.current.id) throw new Error('Invalid response');
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
    <header className="product-heading"><div><p className="workspace-eyebrow">FINANCIAL Q&A</p><h1>Ask about your source records</h1>
      <p>Find and summarize matching financial source records. Answers cite the exact records used. This does not verify figures or provide advice, predictions or new calculations.</p></div><Tag>LOCAL · MODEL CONFIGURATION REQUIRED</Tag></header>
    {!allowed && <Alert type="warning" showIcon title="Q&A access is not enabled" description="Your organization can grant Customer AI analysis alongside search, financial-profile and document read access." />}
    {current?.error && <Alert type="error" showIcon title={current.error} />}
    {allowed && <section className="product-panel" aria-busy={current?.busy ?? true}>
      <Button disabled={current?.busy} onClick={() => { setPermission(false); setQuestion(''); setView({ scope, answers: [], busy: true, error: '' }); setRefresh(v => v + 1); }}>Refresh</Button>
      {current?.configuration && <><h2>{current.configuration.available ? current.configuration.providerLabel : 'Provider not configured'}</h2><p>{current.configuration.notice}</p></>}
      <label htmlFor="financial-question">Your question</label>
      <Input.TextArea id="financial-question" value={current ? question : ''} maxLength={1000} rows={3} disabled={!current?.configuration?.available || current.busy}
        placeholder="Which source records mention salary?" onChange={event => { setQuestion(event.target.value); setPermission(false); operation.current = null; }} />
      <Checkbox checked={!!current && permission} disabled={!current?.configuration?.available || current.busy} onChange={event => { setPermission(event.target.checked); }}>
        For this question, allow Monergy to send my question and matching source descriptions and amounts to the provider above. The question, answer and source trace will be retained in this workspace.
      </Checkbox><p><Button type="primary" loading={current?.busy} disabled={!current?.configuration?.available || !permission || !question.trim()} onClick={() => { void ask(); }}>Ask question</Button></p>
    </section>}
    <section aria-label="Q&A history" aria-live="polite"><h2>Recent answers</h2><p>Up to 20 recent attempts. Answers describe source revisions at the recorded time; they are not live balances.</p>
      {current?.answers.map(answer => <article className="product-panel" key={answer.answerId}><h3>{answer.question}</h3><p>{answer.introduction}</p><time dateTime={answer.recordedAt}>{new Date(answer.recordedAt).toLocaleString()}</time>
        {answer.claims.map(claim => <div key={claim.id}><p>{claim.text}</p><a href={'/finances?fact=' + encodeURIComponent(claim.sourceId)}>View source & history (revision {claim.revision})</a>{' · '}<a href={'/evidence?version=' + encodeURIComponent(claim.documentVersionId)}>Open source document version</a></div>)}
        <Collapse items={[{ key: 'trace', label: 'Answer details', children: <dl><dt>Answer ID</dt><dd>{answer.answerId}</dd><dt>Model</dt><dd>{answer.model || 'No completed model response'}</dd><dt>Prompt version</dt><dd>{answer.promptVersion}</dd></dl> }]} />
      </article>)}
    </section>
  </main>;
}
