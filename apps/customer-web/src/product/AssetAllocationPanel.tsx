import { Alert, Button, Card, Tag } from 'antd';
import { useEffect, useRef, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { assetAllocationIssues, calculateAssetAllocation, exactMoney, getAssetAllocations, validAssetAllocation, validNetWorthPage, type AssetAllocationResult, type NetWorthResult } from './netWorthApi';

const operation = () => crypto.randomUUID().replaceAll('-', '');
const labels: Record<string, string> = { BANK_ACCOUNT: 'Bank accounts', CASH: 'Cash', INVESTMENT: 'Investments', OTHER_ASSET: 'Other assets', PROPERTY: 'Property', VEHICLE: 'Vehicles' };
export default function AssetAllocationPanel({ api, customerId, source }: { api: AccessApi; customerId: string; source: NetWorthResult }) {
  const [saved, setSaved] = useState<AssetAllocationResult | null>(null); const [pending, setPending] = useState<{ operationId: string; netWorthCalculationId: string } | null>(null);
  const [busy, setBusy] = useState(false); const [error, setError] = useState(''); const active = useRef(true);
  useEffect(() => {
    active.current = true; const controller = new AbortController();
    void getAssetAllocations(api, undefined, controller.signal).then(history => {
      if (!validNetWorthPage(history, item => validAssetAllocation(item, customerId), item => item.calculationId)) throw new Error('Invalid asset-allocation history');
      if (active.current) setSaved(history.items.find(item => item.sourceNetWorth.calculationId === source.calculationId) ?? null);
    }).catch(() => { if (active.current && !controller.signal.aborted) setError('Saved asset-allocation history could not be confirmed under your current access.'); });
    return () => { active.current = false; controller.abort(); };
  }, [api, customerId, source.calculationId]);
  const submit = async () => {
    if (busy || source.state !== 'CALCULATED') return; const request = pending ?? { operationId: operation(), netWorthCalculationId: source.calculationId };
    setPending(request); setBusy(true); setError('');
    try {
      const value = await calculateAssetAllocation(api, request);
      if (!validAssetAllocation(value, customerId) || value.operationId !== request.operationId || value.sourceNetWorth.calculationId !== source.calculationId) throw new Error('Invalid confirmation');
      if (active.current) { setSaved(value); setPending(null); }
    } catch { if (active.current) setError('The saved asset allocation was not confirmed. Retry the same request before creating another calculation.'); }
    finally { if (active.current) setBusy(false); }
  };
  return <section aria-label="Asset allocation"><h3>Asset allocation</h3><p>See how this exact reviewed asset inventory is distributed across its saved asset types. Monergy resolves categories and values from the saved Net Worth result; the browser does not submit them.</p>
    <p>This is historical composition of customer-reviewed values, not verified ownership or valuation, a target allocation, diversification assessment, eligibility, recommendation or advice.</p>
    {source.state !== 'CALCULATED' && <Alert type="info" title="Complete this net worth calculation before calculating asset allocation." />}
    <Button type="primary" loading={busy} disabled={source.state !== 'CALCULATED'} onClick={() => { void submit(); }}>{pending ? 'Retry asset allocation confirmation' : 'Calculate and save asset allocation'}</Button>
    {error && <Alert type="error" title={error} />}
    {saved && <Card size="small" style={{ marginBlock: 12 }}><Tag>Customer-reviewed inputs</Tag>{saved.state === 'CALCULATED' && saved.totalAssets ? <><dl><dt>Total reviewed assets</dt><dd>{exactMoney(saved.totalAssets, saved.sourceNetWorth.position.position.currency)}</dd></dl>{saved.categories.map(item => <Card key={item.category} size="small" style={{ marginBlock: 8 }}><strong>{labels[item.category]}</strong><p>{exactMoney(item.amount, saved.sourceNetWorth.position.position.currency)} · {item.percentage}%</p></Card>)}<p>Percentages are independently rounded and can differ from 100.00% by a rounding cent.</p></> : <Alert type="info" title="More information is needed" description={<ul>{saved.issues.map(issue => <li key={issue}>{assetAllocationIssues[issue]}</li>)}</ul>} />}<p>Valuation date {saved.sourceNetWorth.position.position.asOfDate} · Saved {saved.recordedAt} · Rule {saved.ruleVersion}</p><details><summary>Calculation details</summary><p>{saved.numericSemantics}</p><p>Rule: {saved.ruleId} · Definition: {saved.definitionHash}</p><p>Calculation: {saved.calculationId}</p></details></Card>}
  </section>;
}
