import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Select } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { compareMonthlyCashflow, exactMoney, validMonthlyCashflowResult } from './monthlyCashflowApi';
import type { MonthlyCashflowComparison as Comparison, MonthlyCashflowResult } from './monthlyCashflowApi';
export default function MonthlyCashflowComparison({ api, customerId, items }: { api: AccessApi; customerId: string; items: MonthlyCashflowResult[] }) {
  const scope = useMemo(() => ({ api, customerId }), [api, customerId]);
  interface Choice { scope: typeof scope; value: string; label: string }
  const [earlierChoice, setEarlier] = useState<Choice | null>(null); const [laterChoice, setLater] = useState<Choice | null>(null);
  const earlier = earlierChoice?.scope === scope ? earlierChoice.value : ''; const later = laterChoice?.scope === scope ? laterChoice.value : ''; const [request, setRequest] = useState(0);
  const context = useMemo(() => ({ api, customerId, items, earlier, later, request }), [api, customerId, items, earlier, later, request]);
  const [state, setState] = useState<{ context: typeof context; value: Comparison | null; error: boolean } | null>(null);
  useEffect(() => {
    if (!request || !earlier || !later) return;
    const controller = new AbortController();
    void compareMonthlyCashflow(api, earlier, later, controller.signal).then(value => {
      if (!validMonthlyCashflowResult(value.earlier, customerId) || !validMonthlyCashflowResult(value.later, customerId) || value.earlier.calculationId !== earlier || value.later.calculationId !== later || value.earlier.state !== 'CALCULATED' || value.later.state !== 'CALCULATED' || value.earlier.position.position.asOfDate >= value.later.position.position.asOfDate || value.earlier.position.position.currency !== value.later.position.position.currency || value.earlier.position.position.household !== value.later.position.position.household || [value.incomeChange, value.expenseChange, value.balanceChange].some(x => !/^-?\d{1,15}\.\d{2}$/u.test(x))) throw new Error('Invalid comparison');
      if (!controller.signal.aborted) setState({ context, value, error: false });
    }).catch(() => { if (!controller.signal.aborted) setState({ context, value: null, error: true }); });
    return () => { controller.abort(); };
  }, [api, context, customerId, earlier, later, request]);
  const pageOptions = items.filter(x => x.state === 'CALCULATED').map(x => ({ value: x.calculationId, label: x.position.position.asOfDate + ' · ' + x.position.position.household + ' · ' + x.position.position.currency + ' · saved ' + x.recordedAt }));
  const options = [...pageOptions, ...[earlierChoice, laterChoice].filter((x): x is Choice => x !== null && x.scope === scope && !pageOptions.some(p => p.value === x.value))].filter((x, i, list) => list.findIndex(y => y.value === x.value) === i);
  const value = state?.context === context ? state.value : null;
  return <section aria-label="Compare monthly income and expenses"><h2>Compare completed months</h2><p>Select two saved reviews for the same household scope and currency. Use the saved-history pages below to find older reviews; your two selections are retained as you page. Calendar months may have different lengths. Changes are later totals minus earlier totals; no daily rates or annual estimates are inferred.</p>
    <Select id="cashflow-earlier-review" aria-label="Earlier month review" style={{ width: '100%', marginBlock: 8 }} value={earlier || undefined} options={options} onChange={x => { setEarlier({ scope, value: x, label: options.find(o => o.value === x)?.label ?? x }); setRequest(0); }} />
    <Select id="cashflow-later-review" aria-label="Later month review" style={{ width: '100%', marginBlock: 8 }} value={later || undefined} options={options} onChange={x => { setLater({ scope, value: x, label: options.find(o => o.value === x)?.label ?? x }); setRequest(0); }} />
    <Button disabled={!earlier || !later || earlier === later} onClick={() => { setRequest(x => x + 1); }}>Compare monthly totals</Button>
    {state?.context === context && state.error && <Alert type="info" title="These reviews could not be compared. Check month order, completeness, household, currency and current access." />}
    {value && <dl><dt>Income change</dt><dd>{exactMoney(value.incomeChange, value.later.position.position.currency)}</dd><dt>Expense change</dt><dd>{exactMoney(value.expenseChange, value.later.position.position.currency)}</dd><dt>Monthly balance change</dt><dd>{exactMoney(value.balanceChange, value.later.position.position.currency)}</dd></dl>}
  </section>;
}
