import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Input, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { getLiabilitySnapshot } from './productApi';
import type { ProductLiabilityRecordList, ProductLiabilitySnapshot } from './productApi';

interface View { api: AccessApi; refreshKey: ProductLiabilityRecordList | null; date: string; currency: string }
export default function LiabilitySnapshotExperience({ api, refreshKey }: { api: AccessApi; refreshKey: ProductLiabilityRecordList | null }) {
  const [date, setDate] = useState('');
  const [currency, setCurrency] = useState('');
  const [pending, setPending] = useState<View | null>(null);
  const [result, setResult] = useState<(View & { value: ProductLiabilitySnapshot }) | null>(null);
  const [failure, setFailure] = useState<(View & { text: string }) | null>(null);
  const scope = useRef({ api, refreshKey });
  const sequence = useRef(0);
  const controller = useRef<AbortController | null>(null);
  useLayoutEffect(() => { scope.current = { api, refreshKey }; }, [api, refreshKey]);
  useEffect(() => () => { controller.current?.abort(); }, [api, refreshKey]);
  const matches = (view: View | null) => view?.api === api && view.refreshKey === refreshKey && view.date === date && view.currency === currency;
  const choose = (field: 'date' | 'currency', value: string) => {
    sequence.current++; controller.current?.abort(); setPending(null); setResult(null); setFailure(null);
    if (field === 'date') setDate(value); else setCurrency(value.toUpperCase());
  };
  const load = async () => {
    if (!date || !/^[A-Z]{3}$/.test(currency) || matches(pending)) return;
    const view = { api, refreshKey, date, currency }; const generation = ++sequence.current;
    controller.current?.abort(); const request = new AbortController(); controller.current = request;
    const active = () => !request.signal.aborted && sequence.current === generation && scope.current.api === api && scope.current.refreshKey === refreshKey;
    setPending(view); setResult(null); setFailure(null);
    try {
      const value = await getLiabilitySnapshot(api, date, currency, request.signal);
      if (value.asOfDate !== date || value.currency !== currency || !Array.isArray(value.items) || value.items.length > 200 || typeof value.hasMore !== 'boolean' ||
          (value.hasMore && value.items.length !== 200) || new Set(value.items.map(item => item.liabilityRecordId)).size !== value.items.length ||
          value.items.some(item => item.verificationStatus !== 'CUSTOMER_REVIEWED' || !Number.isInteger(item.revision) || item.revision < 1 || item.currency !== currency || item.liability.balanceAsOfDate !== date))
        throw new Error('Invalid dated liabilities');
      if (active()) setResult({ ...view, value });
    } catch (error) {
      if (active()) setFailure({ ...view, text: error instanceof AccessApiError && error.status === 400
        ? 'Choose a balance date from 1900 through today and a three-letter currency code.'
        : 'Dated liabilities are unavailable. Check your current document access and try again.' });
    } finally { if (active()) setPending(null); }
  };
  const visible = matches(result) ? result?.value : null;
  const repeatedSources = visible && new Set(visible.items.map(item => item.liability.sourceFinancialFactId)).size < visible.items.length;
  return <Card size="small" style={{ marginBlock: 16 }}><section aria-label="Dated liability snapshot">
    <h3>Dated liability snapshot</h3>
    <p>Choose an exact balance date and currency to review the latest saved versions dated that day. Older balances are not carried forward and currencies are not converted. Corrections can move records into or out of this view.</p>
    <label htmlFor="liability-snapshot-date">Snapshot balance date</label>
    <Input id="liability-snapshot-date" type="date" value={date} onChange={event => { choose('date', event.target.value); }} />
    <label htmlFor="liability-snapshot-currency">Snapshot currency</label>
    <Input id="liability-snapshot-currency" placeholder="For example, USD or INR" maxLength={3} value={currency} onChange={event => { choose('currency', event.target.value); }} />
    <Button style={{ marginBlock: 12 }} disabled={!date || !/^[A-Z]{3}$/.test(currency)} loading={matches(pending)} onClick={() => { void load(); }}>View dated liabilities</Button>
    {matches(failure) && <Alert type="error" title={failure?.text} />}
    {visible && <section aria-label="Dated liability results">
      <h3>Latest reviewed liabilities dated {visible.asOfDate} · {visible.currency}</h3>
      <p>These are customer-reviewed records. Missing debts, other dates and currencies, and overlap between loan and liability records remain outside this view. No total debt or complete liability position is established.</p>
      {visible.hasMore && <Alert type="info" title="Showing the first 200 matching records in order added. More matching records exist." />}
      {visible.items.length === 0 && <p>No saved liability records match this exact date and currency. This does not mean zero debt.</p>}
      {repeatedSources && <Alert type="warning" title="More than one record references the same source. Review for overlap before using these balances." />}
      {visible.items.map(record => <Card key={record.liabilityRecordId} size="small" style={{ marginBlock: 12 }}><h4>{record.liability.name}</h4><Tag>Customer reviewed</Tag>
        <dl><dt>Creditor</dt><dd>{record.liability.creditor}</dd><dt>Outstanding balance</dt><dd>{new Intl.NumberFormat('en', { style: 'currency', currency: record.currency }).format(record.liability.outstandingAmount)}</dd>
          <dt>Balance as of</dt><dd>{record.liability.balanceAsOfDate}</dd><dt>Saved version</dt><dd>{record.revision}</dd><dt>Reviewed</dt><dd>{record.recordedAt}</dd><dt>Source revision</dt><dd>{record.liability.sourceRevision}</dd></dl>
        <a href={'/finances?fact=' + encodeURIComponent(record.liability.sourceFinancialFactId)}>View source history for {record.liability.name}</a>
      </Card>)}
    </section>}
  </section></Card>;
}
