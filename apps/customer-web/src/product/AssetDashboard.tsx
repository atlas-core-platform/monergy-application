import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Space, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { exactMoney, getNetWorthResult, getNetWorthResults, netWorthIssues, validNetWorthPage, validNetWorthResult } from './netWorthApi';
import type { NetWorthPage, NetWorthResult } from './netWorthApi';

const kinds: Record<string, string> = { CASH: 'Cash', BANK_ACCOUNT: 'Bank accounts', INVESTMENT: 'Investments', PROPERTY: 'Property', VEHICLE: 'Vehicles', OTHER_ASSET: 'Other assets' };
export default function AssetDashboard({ api, customerId, canReadDocuments }: { api: AccessApi; customerId: string; canReadDocuments: boolean }) {
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
      if (item && (!validNetWorthResult(confirmed, customerId) || JSON.stringify(confirmed) !== JSON.stringify(item))) throw new Error('Changed saved review');
      if (live()) setLoaded({ context, page: page ?? { items: selected ? [selected] : [], hasMore: false, nextBeforeId: null }, selected: confirmed, older: selected !== undefined || cursor !== undefined });
    } catch { if (live()) setFailed(context); }
    finally { if (live()) setPending(null); }
  }, [api, customerId, context]);
  useEffect(() => { let cancelled = false; void Promise.resolve().then(() => { if (!cancelled) return run(); }); return () => { cancelled = true; request.current?.abort(); }; }, [run]);
  const value = current?.selected;
  const assets = value?.position.lines.filter(entry => entry.line.side === 'ASSET') ?? [];
  return <Card size="small" style={{ marginBlock: 20 }}><section aria-label="Assets dashboard">
    <h2>Assets</h2>
    <p>Your saved, customer-reviewed asset inventory. Values are declarations, not independently verified ownership or valuations.</p>
    <Space wrap><Button loading={pending === context} onClick={() => { void run(); }}>Refresh latest asset review</Button><a href="/net-worth">Review or update your inventory</a></Space>
    {pending === context && <p role="status">Loading asset review…</p>}
    {failed === context && <Alert type="error" title="Asset reviews could not be confirmed under your current access. Refresh to try again." />}
    {current && !value && <p>No saved asset review yet. This does not mean zero assets. Review your inventory to include cash, bank accounts, investments, property, vehicles and other assets.</p>}
    {value && <>
      <Tag>{current.older ? 'Selected saved review' : 'Latest saved review by calculation time'}</Tag>
      <p>Valuation date: {value.position.position.asOfDate} · Review currency: {value.position.position.currency}</p>
      <p>Saved {value.recordedAt}. Later records and corrections do not update this review. Refresh finds newly saved reviews; start a new inventory review to change values. Dates and currencies are never combined.</p>
      <h3>Total assets in this review</h3>
      <p>{value.state === 'CALCULATED' && value.totalAssets !== null ? exactMoney(value.totalAssets, value.position.position.currency) : 'Not calculated — this saved review needs more information.'}</p>
      <p>Asset coverage: {value.position.position.assetsComplete ? 'Confirmed complete by you for this review' : 'Not confirmed complete'}.</p>
      {value.issues.length > 0 && <Alert type="warning" title="Review gaps before using a total" description={<ul>{value.issues.map(issue => <li key={issue}>{netWorthIssues[issue]}</li>)}</ul>} />}
      {assets.length === 0 && <p>{value.state === 'CALCULATED' && value.position.position.assetsComplete ? 'You explicitly confirmed no assets in this saved review.' : 'No asset entries are recorded in this review; this alone does not establish zero assets.'}</p>}
      <label htmlFor="asset-dashboard-kind">Asset category</label>{' '}
      <select id="asset-dashboard-kind" value={filter} onChange={event => { setFilter(event.target.value); }}>
        <option value="ALL">All assets ({assets.length})</option>
        {Object.entries(kinds).map(([key, name]) => <option key={key} value={key}>{name} ({assets.filter(entry => entry.line.kind === key).length})</option>)}
      </select>
      <p>The total above covers the whole reviewed inventory; the category filter only changes the entries shown below.</p>
      {filter !== 'ALL' && assets.every(entry => entry.line.kind !== filter) && <p>No {kinds[filter]?.toLowerCase()} entries in this saved review.</p>}
      {assets.filter(entry => filter === 'ALL' || entry.line.kind === filter).map(({ line, source }) => <details key={line.lineId} style={{ marginBlock: 12 }}>
        <summary>{line.name} · {kinds[line.kind]} · {line.amount === null ? 'Value not recorded' : exactMoney(line.amount, line.currency)}</summary>
        <p>Value date: {line.valueDate ?? 'Not recorded'} · {source ? 'Linked customer-reviewed record' : 'Customer declaration without a linked source record'}</p>
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
    {current && current.page.items.length > 0 && <section aria-label="Saved asset reviews"><h3>Saved asset reviews</h3><p>Choose one review at a time. Each retains its own date, currency and exact source versions.</p>
      <Space wrap>{current.page.items.map(item => <Button key={item.calculationId} onClick={() => { void run(undefined, item); }}>View asset review: {item.position.position.asOfDate} · {item.position.position.currency} · saved {item.recordedAt}</Button>)}</Space>
      {current.page.hasMore && <Button onClick={() => { void run(current.page.nextBeforeId ?? undefined); }}>Older asset reviews</Button>}
    </section>}
  </section></Card>;
}
