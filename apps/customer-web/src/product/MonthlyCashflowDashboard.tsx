import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Card, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { exactMoney, getMonthlyCashflowResult, getMonthlyCashflowResults, monthlyCashflowIssues, validMonthlyCashflowPage, validMonthlyCashflowResult } from './monthlyCashflowApi';
import type { MonthlyCashflowResult } from './monthlyCashflowApi';
export default function MonthlyCashflowDashboard({ api, customerId }: { api: AccessApi; customerId: string }) {
  const [refresh, setRefresh] = useState(0); const context = useMemo(() => ({ api, customerId, refresh }), [api, customerId, refresh]);
  const [state, setState] = useState<{ context: typeof context; value: MonthlyCashflowResult | null; error: boolean } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      const page = await getMonthlyCashflowResults(api, undefined, controller.signal);
      if (!validMonthlyCashflowPage(page, x => validMonthlyCashflowResult(x, customerId), x => x.calculationId)) throw new Error('Invalid history');
      const latest = page.items[0]; const value = latest ? await getMonthlyCashflowResult(api, latest.calculationId, controller.signal) : null;
      if (value && (!validMonthlyCashflowResult(value, customerId) || JSON.stringify(value) !== JSON.stringify(latest))) throw new Error('Changed review');
      if (!controller.signal.aborted) setState({ context, value, error: false });
    })().catch(() => { if (!controller.signal.aborted) setState({ context, value: null, error: true }); });
    return () => { controller.abort(); };
  }, [api, context, customerId]);
  const current = state?.context === context ? state : null; const value = current?.value;
  return <Card style={{ marginBlock: 20 }}><section aria-label="Monthly income and expenses overview"><h2>Income and Expenses</h2>
    {!current ? <p role="status">Loading monthly review…</p> : current.error ? <Alert type="info" title="Monthly totals are unavailable under your current access." /> : !value ? <p>No monthly review saved. This does not mean zero income or expenses.</p> : <><Tag>Latest saved customer review</Tag><p>Month ending {value.position.position.asOfDate} · {value.position.position.household} · {value.position.position.currency}</p>
      {value.state === 'CALCULATED' && value.totalIncome !== null && value.totalExpenses !== null && value.balance !== null ? <dl><dt>Income</dt><dd>{exactMoney(value.totalIncome, value.position.position.currency)}</dd><dt>Expenses</dt><dd>{exactMoney(value.totalExpenses, value.position.position.currency)}</dd><dt>Monthly balance</dt><dd>{exactMoney(value.balance, value.position.position.currency)}</dd></dl> : <Alert type="info" title="Monthly coverage needs review" description={<ul>{value.issues.map(x => <li key={x}>{monthlyCashflowIssues[x]}</li>)}</ul>} />}
      <a href={'/monthly-cashflow?calculation=' + encodeURIComponent(value.calculationId)}>View exact monthly review and sources</a></>}
    <p><a href="/monthly-cashflow">Review income, expenses and month comparisons</a></p><Button onClick={() => { setRefresh(x => x + 1); }}>Refresh monthly totals</Button>
  </section></Card>;
}
