import { Alert, Button, Card, Checkbox, Select, Tag } from 'antd';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { getMonthlyCashflowResults, validMonthlyCashflowPage, validMonthlyCashflowResult, type MonthlyCashflowResult } from './monthlyCashflowApi';
import { calculateLiquidityRatio, exactMoney, getLiquidityRatios, liquidityRatioIssues, validLiquidityRatio, validNetWorthPage, type LiquidityRatioResult, type NetWorthResult } from './netWorthApi';

const operation = () => crypto.randomUUID().replaceAll('-', '');
export default function LiquidityRatioPanel({ api, customerId, source }: { api: AccessApi; customerId: string; source: NetWorthResult }) {
  const eligible = useMemo(() => source.position.position.lines.filter(line => line.side === 'ASSET' && ['CASH', 'BANK_ACCOUNT'].includes(line.kind) && line.amount !== null), [source]);
  const [cashflows, setCashflows] = useState<MonthlyCashflowResult[]>([]); const [cashflowId, setCashflowId] = useState<string>();
  const [selected, setSelected] = useState<string[]>([]); const [complete, setComplete] = useState(false); const [saved, setSaved] = useState<LiquidityRatioResult | null>(null);
  const [pending, setPending] = useState<{ operationId: string; netWorthCalculationId: string; monthlyCashflowCalculationId: string; liquidAssetLineIds: string[]; liquidAssetsComplete: boolean } | null>(null);
  const [busy, setBusy] = useState(false); const [error, setError] = useState(''); const active = useRef(true);
  useEffect(() => {
    active.current = true; const controller = new AbortController();
    void Promise.all([getMonthlyCashflowResults(api, undefined, controller.signal), getLiquidityRatios(api, undefined, controller.signal)]).then(([monthly, history]) => {
      if (!validMonthlyCashflowPage(monthly, item => validMonthlyCashflowResult(item, customerId), item => item.calculationId) || !validNetWorthPage(history, item => validLiquidityRatio(item, customerId), item => item.calculationId)) throw new Error('Invalid liquidity sources');
      if (!active.current) return;
      setCashflows(monthly.items.filter(item => item.state === 'CALCULATED' && item.position.position.currency === source.position.position.currency));
      const latest = history.items.find(item => item.sourceNetWorth.calculationId === source.calculationId); if (latest) setSaved(latest);
    }).catch(() => { if (active.current && !controller.signal.aborted) setError('Liquidity sources and saved history could not be confirmed under your current access.'); });
    return () => { active.current = false; controller.abort(); };
  }, [api, customerId, source.calculationId, source.position.position.currency]);
  const submit = async () => {
    if (busy || source.state !== 'CALCULATED') return;
    let request = pending;
    if (request === null) {
      if (!complete || cashflowId === undefined) return;
      request = { operationId: operation(), netWorthCalculationId: source.calculationId, monthlyCashflowCalculationId: cashflowId, liquidAssetLineIds: selected, liquidAssetsComplete: complete };
    }
    setPending(request); setBusy(true); setError('');
    try {
      const value = await calculateLiquidityRatio(api, request);
      if (!validLiquidityRatio(value, customerId) || value.operationId !== request.operationId || value.sourceNetWorth.calculationId !== source.calculationId || value.sourceMonthlyCashflow.calculationId !== request.monthlyCashflowCalculationId || JSON.stringify(value.liquidAssetLineIds) !== JSON.stringify(request.liquidAssetLineIds)) throw new Error('Invalid confirmation');
      if (active.current) { setSaved(value); setPending(null); }
    } catch { if (active.current) setError('The saved liquidity ratio was not confirmed. Retry the same request before creating another calculation.'); }
    finally { if (active.current) setBusy(false); }
  };
  return <section aria-label="Liquidity ratio"><h3>Liquidity ratio</h3><p>Select every cash and bank-account asset in this exact reviewed inventory, then choose one exact completed month of reviewed expenses. The result is customer-reviewed and is not independent verification, underwriting, eligibility, a recommendation or advice.</p>
    {source.state !== 'CALCULATED' && <Alert type="info" title="Complete this net worth calculation before calculating liquidity." />}
    <label htmlFor="liquidity-expense-month">Reviewed expense month</label><Select id="liquidity-expense-month" aria-label="Reviewed expense month" value={cashflowId} disabled={busy || pending !== null || source.state !== 'CALCULATED'} placeholder="Select a saved completed month" style={{ width: '100%' }} options={cashflows.map(item => ({ value: item.calculationId, label: item.position.position.asOfDate + ' · ' + item.position.position.currency + ' · expenses ' + exactMoney(item.totalExpenses ?? '0.00', item.position.position.currency) }))} onChange={value => { setCashflowId(value); setSaved(null); }} />
    {cashflows.length === 0 && <p>No calculated monthly expense review in {source.position.position.currency} is available.</p>}
    <h4>Cash and bank-account assets</h4>{eligible.map(line => <Checkbox key={line.lineId} checked={selected.includes(line.lineId)} disabled={busy || pending !== null || source.state !== 'CALCULATED'} onChange={event => { setSelected(current => event.target.checked ? [...current, line.lineId] : current.filter(id => id !== line.lineId)); setComplete(false); }}>{line.name} · {exactMoney(line.amount ?? '0.00', line.currency)}</Checkbox>)}
    {eligible.length === 0 && <p>This reviewed inventory contains no cash or bank-account asset lines. Confirm below only after checking that this is correct.</p>}
    <div><Checkbox checked={complete} disabled={busy || pending !== null || source.state !== 'CALCULATED'} onChange={event => { setComplete(event.target.checked); }}>I reviewed this inventory and selected every cash and bank-account asset, or confirm there are none.</Checkbox></div>
    <Button type="primary" loading={busy} disabled={source.state !== 'CALCULATED' || pending === null && (!complete || cashflowId === undefined)} onClick={() => { void submit(); }}>{pending ? 'Retry liquidity confirmation' : 'Calculate and save liquidity ratio'}</Button>
    {error && <Alert type="error" title={error} />}
    {saved && <Card size="small" style={{ marginBlock: 12 }}><Tag>Customer-reviewed inputs</Tag>{saved.state === 'CALCULATED' && saved.liquidAssets && saved.monthlyExpenses && saved.ratioMonths ? <dl><dt>Reviewed liquid assets</dt><dd>{exactMoney(saved.liquidAssets, saved.sourceNetWorth.position.position.currency)}</dd><dt>Reviewed monthly expenses</dt><dd>{exactMoney(saved.monthlyExpenses, saved.sourceMonthlyCashflow.position.position.currency)}</dd><dt>Liquidity ratio</dt><dd>{saved.ratioMonths} months</dd></dl> : <Alert type="info" title="More information is needed" description={<ul>{saved.issues.map(issue => <li key={issue}>{liquidityRatioIssues[issue]}</li>)}</ul>} />}<p>Expense month {saved.sourceMonthlyCashflow.position.position.asOfDate} · Saved {saved.recordedAt} · Rule {saved.ruleVersion}</p><details><summary>Calculation details</summary><p>{saved.numericSemantics}</p><p>Rule: {saved.ruleId} · Definition: {saved.definitionHash}</p><p>Calculation: {saved.calculationId}</p></details></Card>}
  </section>;
}
