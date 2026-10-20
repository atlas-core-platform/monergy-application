import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Card, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { exactMoney, getNetWorthResults, validNetWorthPage, validNetWorthResult } from './netWorthApi';
import type { NetWorthResult } from './netWorthApi';
export default function NetWorthSummary({ api, customerId }: { api: AccessApi; customerId: string }) {
  const context = useMemo(() => ({ api, customerId }), [api, customerId]); const active = useRef<typeof context | null>(context);
  useLayoutEffect(() => { active.current = context; return () => { active.current = null; }; }, [context]);
  const [loaded, setLoaded] = useState<{ context: typeof context; value: NetWorthResult | null } | null>(null);
  const [failed, setFailed] = useState<typeof context | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    void getNetWorthResults(api, undefined, controller.signal).then(page => {
      if (!validNetWorthPage(page, item => validNetWorthResult(item, customerId), item => item.calculationId)) throw new Error('Invalid net worth history');
      if (active.current === context && !controller.signal.aborted) setLoaded({ context, value: page.items.at(0) ?? null });
    }).catch(() => { if (active.current === context && !controller.signal.aborted) setFailed(context); });
    return () => { controller.abort(); };
  }, [api, customerId, context]);
  const value = loaded?.context === context ? loaded.value : null; const ready = loaded?.context === context;
  return <Card size="small" style={{ marginBlock: 20 }}><section aria-label="Net worth overview"><h2>Net Worth</h2>
    {failed === context ? <Alert type="info" title="Saved net worth is unavailable under your current access." /> : !ready ? <p role="status">Loading saved net worth…</p> : value ? <>
      <Tag>Latest saved customer review</Tag><p>Valuation date: {value.position.position.asOfDate} · {value.position.position.currency}</p>
      <p>{value.state === 'CALCULATED' && value.netWorth !== null ? exactMoney(value.netWorth, value.position.position.currency) : 'More information is needed to calculate this reviewed inventory.'}</p><p>Calculated {value.recordedAt}. Later financial changes require a new review.</p>
    </> : <p>No reviewed net worth calculation yet. This does not mean zero net worth.</p>}
    <a href="/net-worth">Review net worth and saved sources</a>
  </section></Card>;
}
