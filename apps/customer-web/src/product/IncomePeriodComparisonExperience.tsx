import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Select, Space, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { compareIncomePeriods } from './productApi';
import type { ProductIncomePeriodComparison, ProductIncomeRecord } from './productApi';

const frequencyNames: Record<string, string> = { ONE_OFF: 'One-off', DAILY: 'Daily', WEEKLY: 'Weekly', MONTHLY: 'Monthly', QUARTERLY: 'Quarterly', ANNUAL: 'Yearly', IRREGULAR: 'Irregular' };
const keyOf = (record: ProductIncomeRecord) => record.incomeRecordId + '@' + String(record.revision);
const money = (record: ProductIncomeRecord) => new Intl.NumberFormat('en', { style: 'currency', currency: record.currency }).format(record.income.amount);
interface View { api: AccessApi; earlier: string; later: string }

export default function IncomePeriodComparisonExperience({ api, records, hasMore }: {
  api: AccessApi; records: ProductIncomeRecord[]; hasMore: boolean;
}) {
  const [earlierKey, setEarlierKey] = useState<string>();
  const [laterKey, setLaterKey] = useState<string>();
  const [pending, setPending] = useState<View | null>(null);
  const [result, setResult] = useState<(View & { value: ProductIncomePeriodComparison }) | null>(null);
  const [failure, setFailure] = useState<(View & { text: string }) | null>(null);
  const sequence = useRef(0);
  const controller = useRef<AbortController | null>(null);
  const scope = useRef({ api, records });
  useLayoutEffect(() => { scope.current = { api, records }; }, [api, records]);
  useEffect(() => () => { controller.current?.abort(); }, [api]);
  const earlier = records.find(record => keyOf(record) === earlierKey);
  const later = records.find(record => keyOf(record) === laterKey);
  const matches = (view: View | null) => view?.api === api && Boolean(earlier && later) && view.earlier === earlierKey && view.later === laterKey;
  const busy = matches(pending);
  const choose = (side: 'earlier' | 'later', value?: string) => {
    sequence.current++; controller.current?.abort(); setPending(null); setFailure(null); setResult(null);
    if (side === 'earlier') setEarlierKey(value); else setLaterKey(value);
  };
  const compare = async () => {
    if (!earlier || !later || earlier.incomeRecordId === later.incomeRecordId || busy) return;
    const view = { api, earlier: keyOf(earlier), later: keyOf(later) };
    const generation = ++sequence.current; controller.current?.abort();
    const request = new AbortController(); controller.current = request;
    const active = () => !request.signal.aborted && generation === sequence.current && scope.current.api === api &&
      scope.current.records.some(record => keyOf(record) === view.earlier) && scope.current.records.some(record => keyOf(record) === view.later);
    setPending(view); setResult(null); setFailure(null);
    try {
      const value = await compareIncomePeriods(api, { earlierIncomeRecordId: earlier.incomeRecordId, earlierRevision: earlier.revision,
        laterIncomeRecordId: later.incomeRecordId, laterRevision: later.revision }, request.signal);
      if (value.earlier.incomeRecordId !== earlier.incomeRecordId || value.earlier.revision !== earlier.revision ||
          value.later.incomeRecordId !== later.incomeRecordId || value.later.revision !== later.revision ||
          value.earlier.verificationStatus !== 'CUSTOMER_REVIEWED' || value.later.verificationStatus !== 'CUSTOMER_REVIEWED' || !Number.isInteger(value.periodDays) || value.periodDays < 1)
        throw new Error('Invalid comparison');
      if (active()) setResult({ ...view, value });
    } catch (error) {
      if (active()) setFailure({ ...view, text: error instanceof AccessApiError && error.status === 400
        ? 'Choose records from different sources with the same payer, income type and currency, and equal-length periods that do not overlap. Put the earlier period first.'
        : error instanceof AccessApiError && error.status === 404 ? 'A selected saved version is unavailable. Refresh income records and choose again.'
        : 'Income comparison is unavailable. Check your current document access and try again.' });
    } finally { if (active()) setPending(null); }
  };
  const visible = matches(result) ? result?.value : null;
  const options = records.map(record => ({ value: keyOf(record), label: record.income.name + ' · ' + record.income.periodStart + ' to ' + record.income.periodEnd + ' · ' + money(record) + ' · Version ' + String(record.revision) }));
  return <Card size="small" style={{ marginBlock: 16 }}><section aria-label="Compare income periods">
    <h3>Compare income periods</h3>
    <p>Choose two saved income records for the same payer, type and currency. Periods must have the same number of days, stay separate and use different reviewed sources.</p>
    {records.length < 2 && <p>Add two income records from different reviewed sources to compare periods.</p>}
    {((Boolean(earlierKey) && !earlier) || (Boolean(laterKey) && !later)) && <Alert type="info" title="A selected record version is no longer in this list. Choose its current version to compare again." />}
    {hasMore && <p>The choices include the 200 most recently added records. This view covers only the two selected versions.</p>}
    <label htmlFor="income-earlier">Earlier saved income</label>
    <Select id="income-earlier" aria-label="Earlier saved income" style={{ width: '100%', marginBottom: 12 }} options={options}
      value={earlier ? earlierKey : undefined} allowClear onChange={(value?: string) => { choose('earlier', value); }} />
    <label htmlFor="income-later">Later saved income</label>
    <Select id="income-later" aria-label="Later saved income" style={{ width: '100%', marginBottom: 12 }} options={options}
      value={later ? laterKey : undefined} allowClear onChange={(value?: string) => { choose('later', value); }} />
    <Button onClick={() => { void compare(); }} loading={busy} disabled={!earlier || !later || earlier.incomeRecordId === later.incomeRecordId}>Compare selected income periods</Button>
    {matches(failure) && <Alert type="error" title={failure?.text} />}
    {visible && <section aria-label="Income period comparison">
      <p>{visible.periodDays} days in each reporting period. Review these saved versions and their sources; other income records are outside this view.</p>
      <Space align="start" wrap style={{ width: '100%' }}>
        {([['Earlier period', visible.earlier], ['Later period', visible.later]] as const).map(([title, record]) => <Card key={title} size="small"><h3>{title}</h3><Tag>Customer reviewed</Tag>
          <dl><dt>Income name</dt><dd>{record.income.name}</dd><dt>Payer</dt><dd>{record.income.incomeSource}</dd><dt>Income type</dt><dd>{record.income.incomeType}</dd>
            <dt>Period total</dt><dd>{money(record)}</dd><dt>Reporting period</dt><dd>{record.income.periodStart} to {record.income.periodEnd}</dd>
            <dt>Currency</dt><dd>{record.currency}</dd><dt>Payment frequency (as reported)</dt><dd>{frequencyNames[record.income.frequency] ?? record.income.frequency}</dd><dt>Financial year (as reported)</dt><dd>{record.income.financialYear}</dd>
            <dt>Saved version</dt><dd>{record.revision}</dd><dt>Reviewed</dt><dd>{record.recordedAt}</dd><dt>Source revision</dt><dd>{record.income.sourceRevision}</dd></dl>
          <a href={'/finances?fact=' + encodeURIComponent(record.income.sourceFinancialFactId)}>View {title.toLowerCase()} source history</a>
        </Card>)}
      </Space>
      <p>Periods and amounts are customer reviewed. Frequency and financial-year labels do not annualise these amounts or establish verified income.</p>
    </section>}
  </section></Card>;
}
