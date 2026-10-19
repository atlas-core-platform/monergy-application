import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Select, Space, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { compareExpensePeriods } from './productApi';
import type { ProductExpensePeriodComparison, ProductExpenseRecord } from './productApi';

const keyOf = (record: ProductExpenseRecord) => record.expenseRecordId + '@' + String(record.revision);
const money = (record: ProductExpenseRecord) => new Intl.NumberFormat('en', { style: 'currency', currency: record.currency }).format(record.expense.amount);
interface View { api: AccessApi; earlier: string; later: string }

export default function ExpensePeriodComparisonExperience({ api, records, hasMore }: {
  api: AccessApi; records: ProductExpenseRecord[]; hasMore: boolean;
}) {
  const [earlierKey, setEarlierKey] = useState<string>();
  const [laterKey, setLaterKey] = useState<string>();
  const [pending, setPending] = useState<View | null>(null);
  const [result, setResult] = useState<(View & { value: ProductExpensePeriodComparison }) | null>(null);
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
    if (!earlier || !later || earlier.expenseRecordId === later.expenseRecordId || busy) return;
    const view = { api, earlier: keyOf(earlier), later: keyOf(later) };
    const generation = ++sequence.current; controller.current?.abort();
    const request = new AbortController(); controller.current = request;
    const active = () => !request.signal.aborted && generation === sequence.current && scope.current.api === api &&
      scope.current.records.some(record => keyOf(record) === view.earlier) && scope.current.records.some(record => keyOf(record) === view.later);
    setPending(view); setResult(null); setFailure(null);
    try {
      const value = await compareExpensePeriods(api, { earlierExpenseRecordId: earlier.expenseRecordId, earlierRevision: earlier.revision,
        laterExpenseRecordId: later.expenseRecordId, laterRevision: later.revision }, request.signal);
      if (value.earlier.expenseRecordId !== earlier.expenseRecordId || value.earlier.revision !== earlier.revision ||
          value.later.expenseRecordId !== later.expenseRecordId || value.later.revision !== later.revision ||
          value.earlier.verificationStatus !== 'CUSTOMER_REVIEWED' || value.later.verificationStatus !== 'CUSTOMER_REVIEWED' || !Number.isInteger(value.periodDays) || value.periodDays < 1)
        throw new Error('Invalid comparison');
      if (active()) setResult({ ...view, value });
    } catch (error) {
      if (active()) setFailure({ ...view, text: error instanceof AccessApiError && error.status === 400
        ? 'Choose records from different sources with the same category and currency, and equal-length periods that do not overlap. Put the earlier period first.'
        : error instanceof AccessApiError && error.status === 404 ? 'A selected saved version is unavailable. Refresh expense records and choose again.'
        : 'Expense comparison is unavailable. Check your current document access and try again.' });
    } finally { if (active()) setPending(null); }
  };
  const visible = matches(result) ? result?.value : null;
  const options = records.map(record => ({ value: keyOf(record), label: record.expense.name + ' · ' + record.expense.periodStart + ' to ' + record.expense.periodEnd + ' · ' + money(record) + ' · Version ' + String(record.revision) }));
  return <Card size="small" style={{ marginBlock: 16 }}><section aria-label="Compare expense periods">
    <h3>Compare expense periods</h3>
    <p>Choose two saved expense records for the same category and currency. Periods must have the same number of days, stay separate and use different reviewed sources.</p>
    {records.length < 2 && <p>Add two expense records from different reviewed sources to compare periods.</p>}
    {((Boolean(earlierKey) && !earlier) || (Boolean(laterKey) && !later)) && <Alert type="info" title="A selected record version is no longer in this list. Choose its current version to compare again." />}
    {hasMore && <p>The choices include the 200 most recently added records. This view covers only the two selected versions.</p>}
    <label htmlFor="expense-earlier">Earlier saved expense</label>
    <Select id="expense-earlier" aria-label="Earlier saved expense" style={{ width: '100%', marginBottom: 12 }} options={options}
      value={earlier ? earlierKey : undefined} allowClear onChange={(value?: string) => { choose('earlier', value); }} />
    <label htmlFor="expense-later">Later saved expense</label>
    <Select id="expense-later" aria-label="Later saved expense" style={{ width: '100%', marginBottom: 12 }} options={options}
      value={later ? laterKey : undefined} allowClear onChange={(value?: string) => { choose('later', value); }} />
    <Button onClick={() => { void compare(); }} loading={busy} disabled={!earlier || !later || earlier.expenseRecordId === later.expenseRecordId}>Compare selected expense periods</Button>
    {matches(failure) && <Alert type="error" title={failure?.text} />}
    {visible && <section aria-label="Expense period comparison">
      <p>{visible.periodDays} days in each reporting period. Review these saved versions and their sources; other expense records are outside this view.</p>
      <Space align="start" wrap style={{ width: '100%' }}>
        {([['Earlier period', visible.earlier], ['Later period', visible.later]] as const).map(([title, record]) => <Card key={title} size="small"><h3>{title}</h3><Tag>Customer reviewed</Tag>
          <dl><dt>Expense name</dt><dd>{record.expense.name}</dd><dt>Category</dt><dd>{record.expense.category}</dd>
            <dt>Period total</dt><dd>{money(record)}</dd><dt>Reporting period</dt><dd>{record.expense.periodStart} to {record.expense.periodEnd}</dd>
            <dt>Currency</dt><dd>{record.currency}</dd>
            <dt>Saved version</dt><dd>{record.revision}</dd><dt>Reviewed</dt><dd>{record.recordedAt}</dd><dt>Source revision</dt><dd>{record.expense.sourceRevision}</dd></dl>
          <a href={'/finances?fact=' + encodeURIComponent(record.expense.sourceFinancialFactId)}>View {title.toLowerCase()} source history</a>
        </Card>)}
      </Space>
      <p>Periods, categories and amounts are customer reviewed. This view does not establish recurring spending, category completeness or verified expenses.</p>
    </section>}
  </section></Card>;
}
