import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Space, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { exactMoney, getNetWorthResult, getNetWorthResults, netWorthIssues, validNetWorthPage, validNetWorthResult } from './netWorthApi';
import type { NetWorthPage, NetWorthResult } from './netWorthApi';

const kinds: Record<string, string> = { LOAN: 'Loans', CREDIT_CARD: 'Credit cards', OTHER_LIABILITY: 'Other liabilities' };
export default function LiabilitiesDashboard({ api, customerId, canReadDocuments }: { api: AccessApi; customerId: string; canReadDocuments: boolean }) {
  const context = useMemo(() => ({ api, customerId, canReadDocuments }), [api, customerId, canReadDocuments]);
  const active = useRef<typeof context | null>(context);
  const request = useRef<AbortController | null>(null);
  useLayoutEffect(() => { active.current = context; return () => { active.current = null; request.current?.abort(); }; }, [context]);
  const [loaded, setLoaded] = useState<{ context: typeof context; page: NetWorthPage<NetWorthResult>; selected: NetWorthResult | null; older: boolean } | null>(null);
  const [pending, setPending] = useState<typeof context | null>(null);
  const [failed, setFailed] = useState<typeof context | null>(null);
  const [filter, setFilter] = useState('ALL');
  const current = loaded?.context === context ? loaded : null;
  const run = useCallback(async (cursor?: string, selected?: NetWorthResult) => {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    const live = () => active.current === context && request.current === controller && !controller.signal.aborted;
    setPending(context); setFailed(null); setLoaded(null); setFilter('ALL');
    try {
      const page = selected ? null : await getNetWorthResults(api, cursor, controller.signal);
      if (page && !validNetWorthPage(page, item => validNetWorthResult(item, customerId), item => item.calculationId)) throw new Error('Invalid saved reviews');
      const item = selected ?? page?.items[0];
      const confirmed = item ? await getNetWorthResult(api, item.calculationId, controller.signal) : null;
      if (item && (!validNetWorthResult(confirmed, customerId) || confirmed.state === 'CALCULATED' && (!confirmed.position.position.assetsComplete || !confirmed.position.position.liabilitiesComplete) || JSON.stringify(confirmed) !== JSON.stringify(item))) throw new Error('Changed saved review');
      if (live()) setLoaded({ context, page: page ?? { items: selected ? [selected] : [], hasMore: false, nextBeforeId: null }, selected: confirmed, older: selected !== undefined || cursor !== undefined });
    } catch { if (live()) setFailed(context); }
    finally { if (live()) setPending(null); }
  }, [api, customerId, context]);
  useEffect(() => { let cancelled = false; void Promise.resolve().then(() => { if (!cancelled) return run(); }); return () => { cancelled = true; request.current?.abort(); }; }, [run]);
  const value = current?.selected;
  const liabilities = value?.position.lines.filter(entry => entry.line.side === 'LIABILITY') ?? [];
  return <Card size="small" style={{ marginBlock: 20 }}><section aria-label="Liabilities dashboard">
    <h2>Liabilities</h2>
    <p>Your saved, customer-reviewed liability inventory. Balances are customer declarations, not independently verified creditor balances or repayment status.</p>
    <Space wrap><Button loading={pending === context} onClick={() => { void run(); }}>Refresh latest liability review</Button><a href="/net-worth">Review or update your inventory</a></Space>
    {pending === context && <p role="status">Loading liability review…</p>}
    {failed === context && <Alert type="error" title="Liability reviews could not be confirmed under your current access. Refresh to try again." />}
    {current && !value && <p>No saved liability review yet. This does not mean zero liabilities. Review your inventory to include loans, credit cards and other liabilities.</p>}
    {value && <>
      <Tag>{current.older ? 'Selected saved review' : 'Latest saved review by calculation time'}</Tag>
      <p>Balance date: {value.position.position.asOfDate} · Review currency: {value.position.position.currency}</p>
      <p>Saved {value.recordedAt}. Later records and corrections do not update this review. Refresh finds newly saved reviews; start a new inventory review to change values. Dates and currencies are never combined.</p>
      <h3>Total liabilities in this review</h3>
      <p>Review loans and credit-card balances together to avoid counting the same debt twice. A reported closed loan is not proof that a balance was repaid; reconcile duplicate sources before saving a new review.</p>
      <p>{value.state === 'CALCULATED' && value.totalLiabilities !== null ? exactMoney(value.totalLiabilities, value.position.position.currency) : 'Not calculated — this saved review needs more information.'}</p>
      <p>Liability coverage: {value.position.position.liabilitiesComplete ? 'Confirmed complete by you for this review' : 'Not confirmed complete'}.</p>
      {value.issues.length > 0 && <Alert type="warning" title="Review gaps before using a total" description={<ul>{value.issues.map(issue => <li key={issue}>{netWorthIssues[issue]}</li>)}</ul>} />}
      {liabilities.length === 0 && <p>{value.state === 'CALCULATED' && value.position.position.liabilitiesComplete ? 'You explicitly confirmed no liabilities in this saved review.' : 'No liability entries are recorded in this review; this alone does not establish zero liabilities.'}</p>}
      <label htmlFor="liability-dashboard-kind">Liability category</label>{' '}
      <select id="liability-dashboard-kind" value={filter} onChange={event => { setFilter(event.target.value); }}>
        <option value="ALL">All liabilities ({liabilities.length})</option>
        {Object.entries(kinds).map(([key, name]) => <option key={key} value={key}>{name} ({liabilities.filter(entry => entry.line.kind === key).length})</option>)}
      </select>
      <p>The total above covers the whole reviewed inventory; the category filter only changes the entries shown below.</p>
      {filter !== 'ALL' && liabilities.every(entry => entry.line.kind !== filter) && <p>No {kinds[filter]?.toLowerCase()} entries in this saved review.</p>}
      {liabilities.filter(entry => filter === 'ALL' || entry.line.kind === filter).map(({ line, source }) => <details key={line.lineId} style={{ marginBlock: 12 }}>
        <summary>{line.name} · {kinds[line.kind]} · {line.amount === null ? 'Balance not recorded' : exactMoney(line.amount, line.currency)}</summary>
        <p>Balance date: {line.valueDate ?? 'Not recorded'} · {source ? 'Linked customer-reviewed record' : 'Customer declaration without a linked source record'}</p>
        <p>{line.sourceNote}</p>
        {line.record && <p>Saved record: {line.record.recordId} · version {line.record.revision}</p>}
        {source && <><p>Financial source revision {source.sourceRevision} · Reviewed {source.reviewedAt}</p>
          <p>Document version: {source.documentVersionId} · Evidence: {source.evidenceId} · Provenance: {source.provenanceId}</p>
          <a href={'/finances?fact=' + encodeURIComponent(source.financialFactId)}>View source history for {line.name}</a>
          {canReadDocuments && <p><a href={'/evidence?version=' + encodeURIComponent(source.documentVersionId)}>View source document for {line.name}</a></p>}
        </>}
      </details>)}
      <details><summary>Calculation reference</summary><p>Saved calculation: {value.calculationId} · Rule: {value.ruleId} {value.ruleVersion}</p><p>{value.numericSemantics}</p></details>
    </>}
    {current && current.page.items.length > 0 && <section aria-label="Saved liability reviews"><h3>Saved liability reviews</h3><p>Choose one review at a time. Each retains its own date, currency and exact source versions.</p>
      <Space wrap>{current.page.items.map(item => <Button key={item.calculationId} onClick={() => { void run(undefined, item); }}>View liability review: {item.position.position.asOfDate} · {item.position.position.currency} · saved {item.recordedAt}</Button>)}</Space>
      {current.page.hasMore && <Button onClick={() => { void run(current.page.nextBeforeId ?? undefined); }}>Older liability reviews</Button>}
    </section>}
  </section></Card>;
}
