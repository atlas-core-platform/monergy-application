import { Alert, Button, Card, Tag } from 'antd';
import { useEffect, useRef, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { calculateMonthlyNetCashflowRate, exactMoney, getMonthlyNetCashflowRates, monthlyNetCashflowRateIssues, validMonthlyCashflowPage, validMonthlyNetCashflowRate, type MonthlyCashflowResult, type MonthlyNetCashflowRateResult } from './monthlyCashflowApi';

const operation = () => crypto.randomUUID().replaceAll('-', '');
export default function MonthlyNetCashflowRatePanel({ api, customerId, source }: { api: AccessApi; customerId: string; source: MonthlyCashflowResult }) {
  const [saved,setSaved]=useState<MonthlyNetCashflowRateResult|null>(null);const [pending,setPending]=useState<{operationId:string;monthlyCashflowCalculationId:string}|null>(null);
  const [busy,setBusy]=useState(false);const [error,setError]=useState('');const active=useRef(true);
  useEffect(()=>{active.current=true;const controller=new AbortController();void getMonthlyNetCashflowRates(api,controller.signal).then(history=>{
    if(!validMonthlyCashflowPage(history,item=>validMonthlyNetCashflowRate(item,customerId),item=>item.calculationId))throw new Error('Invalid history');
    if(active.current)setSaved(history.items.find(item=>item.sourceMonthlyCashflow.calculationId===source.calculationId)??null);
  }).catch(()=>{if(active.current&&!controller.signal.aborted)setError('Saved net cash-flow rate history could not be confirmed under your current access.');});return()=>{active.current=false;controller.abort();};},[api,customerId,source.calculationId]);
  const submit=async()=>{if(busy||source.state!=='CALCULATED')return;const request=pending??{operationId:operation(),monthlyCashflowCalculationId:source.calculationId};setPending(request);setBusy(true);setError('');try{
    const value=await calculateMonthlyNetCashflowRate(api,request);if(!validMonthlyNetCashflowRate(value,customerId)||value.operationId!==request.operationId||value.sourceMonthlyCashflow.calculationId!==source.calculationId)throw new Error('Invalid confirmation');
    if(active.current){setSaved(value);setPending(null);}
  }catch{if(active.current)setError('The saved net cash-flow rate was not confirmed. Retry the same request before creating another calculation.');}finally{if(active.current)setBusy(false);}};
  const currency=source.position.position.currency;
  return <section aria-label="Monthly net cash-flow rate"><h3>Monthly net cash-flow rate</h3><p>See what share of this exact reviewed monthly income remained after reviewed monthly expenses. Monergy resolves the totals from the saved Monthly Cashflow result; the browser does not submit them.</p>
    <p>A negative rate is retained when reviewed expenses exceed reviewed income. This is historical customer-reviewed cash flow, not verified income or spending, a savings target, forecast, eligibility decision, recommendation or advice.</p>
    {source.state!=='CALCULATED'&&<Alert type="info" title="Complete this monthly cash-flow calculation before calculating the rate."/>}
    <Button type="primary" loading={busy} disabled={source.state!=='CALCULATED'} onClick={()=>{void submit();}}>{pending?'Retry net cash-flow rate confirmation':'Calculate and save net cash-flow rate'}</Button>
    {error&&<Alert type="error" title={error}/>} {saved&&<Card size="small" style={{marginBlock:12}}><Tag>Customer-reviewed inputs</Tag>{saved.state==='CALCULATED'&&saved.monthlyIncome&&saved.monthlyExpenses&&saved.netCashflow&&saved.ratePercent?<><dl><dt>Reviewed monthly income</dt><dd>{exactMoney(saved.monthlyIncome,currency)}</dd><dt>Reviewed monthly expenses</dt><dd>{exactMoney(saved.monthlyExpenses,currency)}</dd><dt>Net monthly cash flow</dt><dd>{exactMoney(saved.netCashflow,currency)}</dd><dt>Net cash-flow rate</dt><dd>{saved.ratePercent}%</dd></dl><p>Rate = (reviewed income − reviewed expenses) ÷ reviewed income × 100.</p></>:<Alert type="info" title="More information is needed" description={<ul>{saved.issues.map(issue=><li key={issue}>{monthlyNetCashflowRateIssues[issue]}</li>)}</ul>}/>}<p>Month end {saved.sourceMonthlyCashflow.position.position.asOfDate} · Saved {saved.recordedAt} · Rule {saved.ruleVersion}</p><details><summary>Calculation details</summary><p>{saved.numericSemantics}</p><p>Rule: {saved.ruleId} · Definition: {saved.definitionHash}</p><p>Calculation: {saved.calculationId}</p></details></Card>}
  </section>;
}
