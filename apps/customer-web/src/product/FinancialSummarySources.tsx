import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Select } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { getNetWorthResults, validNetWorthPage, validNetWorthResult } from './netWorthApi';
import { getMonthlyCashflowResults, validMonthlyCashflowPage, validMonthlyCashflowResult } from './monthlyCashflowApi';
interface Choice { value: string; label: string }
export default function FinancialSummarySources({ api, customerId, disabled, onChange }: { api: AccessApi; customerId: string; disabled: boolean; onChange: (netWorth: string, cashflow: string) => void }) {
  const [net, setNet] = useState(''); const [cash, setCash] = useState('');
  const [options, setOptions] = useState<{ net: Choice[]; cash: Choice[]; nextNet?: string; nextCash?: string }>({ net: [], cash: [] });
  const [busy, setBusy] = useState(true); const [error, setError] = useState(false); const reading = useRef<AbortController | null>(null);
  const load = useCallback(async (kind: 'both' | 'net' | 'cash', cursor?: string) => {
    reading.current?.abort(); const controller = new AbortController(); reading.current = controller; setBusy(true); setError(false);
    try {
      const [n, c] = await Promise.all([kind !== 'cash' ? getNetWorthResults(api, cursor, controller.signal) : null, kind !== 'net' ? getMonthlyCashflowResults(api, cursor, controller.signal) : null]);
      if (n && (!validNetWorthPage(n, x => validNetWorthResult(x, customerId), x => x.calculationId) || n.items.some(x => x.calculationId === cursor)) || c && (!validMonthlyCashflowPage(c, x => validMonthlyCashflowResult(x, customerId), x => x.calculationId) || c.items.some(x => x.calculationId === cursor))) throw new Error('Invalid review history');
      if (!controller.signal.aborted) setOptions(previous => {
        const add = (old: Choice[], values: Choice[]) => cursor ? [...old, ...values.filter(x => !old.some(y => y.value === x.value))] : values;
        return { net: n ? add(previous.net, n.items.map(x => ({ value: x.calculationId, label: x.position.position.asOfDate + ' · ' + x.position.position.currency + ' · saved ' + x.recordedAt + (x.state === 'CALCULATED' ? '' : ' · incomplete') }))) : previous.net,
          cash: c ? add(previous.cash, c.items.map(x => ({ value: x.calculationId, label: x.position.position.asOfDate + ' · ' + x.position.position.household + ' · ' + x.position.position.currency + ' · saved ' + x.recordedAt + (x.state === 'CALCULATED' ? '' : ' · incomplete') }))) : previous.cash,
          nextNet: n ? n.nextBeforeId ?? undefined : previous.nextNet, nextCash: c ? c.nextBeforeId ?? undefined : previous.nextCash };
      });
    } catch { if (!controller.signal.aborted) setError(true); }
    finally { if (!controller.signal.aborted) setBusy(false); }
  }, [api, customerId]);
  useEffect(() => { const timer = window.setTimeout(() => { void load('both'); }, 0); return () => { window.clearTimeout(timer); reading.current?.abort(); }; }, [load]);
  return <section aria-label="Financial summary reviews"><p>Choose net worth and monthly cash flow for the same month-end date and currency. Monthly household and every member must be Self so they match your personal net-worth scope. Both inventories must be complete. Values are customer-reviewed declarations.</p>
    {error && <Alert type="error" title="Saved summary reviews could not be loaded. Refresh Q&A and check access." />}
    <label htmlFor="summary-net">Net-worth result</label><Select showSearch={{ optionFilterProp: 'label' }} id="summary-net" aria-label="Summary net-worth result" options={options.net} value={net || undefined} disabled={disabled || busy || error} onChange={value => { setNet(value); onChange(value, cash); }} />
    {options.nextNet && <Button disabled={disabled || busy} onClick={() => { void load('net', options.nextNet); }}>Load older net-worth results</Button>}
    <label htmlFor="summary-cash">Monthly cash-flow result</label><Select showSearch={{ optionFilterProp: 'label' }} id="summary-cash" aria-label="Summary monthly result" options={options.cash} value={cash || undefined} disabled={disabled || busy || error} onChange={value => { setCash(value); onChange(net, value); }} />
    {options.nextCash && <Button disabled={disabled || busy} onClick={() => { void load('cash', options.nextCash); }}>Load older monthly results</Button>}
    <p><a href="/net-worth">Review net worth</a> · <a href="/monthly-cashflow">Review monthly income and expenses</a></p>
  </section>;
}
