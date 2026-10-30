import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Progress, Tag } from 'antd';
import { AccessApiError, type AccessApi } from '../access/accessApi';
import { getFinancialGoalProgress } from './productApi';
import type { ProductFinancialGoal, ProductFinancialGoalProgress } from './productApi';

const amount = (value: number, currency: string) => new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(value);
const validDate = (value: string | null) => value === null || /^(19|20|21)\d{2}-\d{2}-\d{2}$/u.test(value) && !Number.isNaN(Date.parse(value));
const validMoney = (value: number, positive = false) => Number.isFinite(value) && value >= (positive ? 0.01 : 0) && Number(value.toFixed(2)) === value;
const bound = (value: ProductFinancialGoalProgress | null | undefined, goal: ProductFinancialGoal) => {
  if (value?.customerId !== goal.customerId || value.financialGoalId !== goal.financialGoalId || value.goalRevision !== goal.revision ||
    value.goal.name !== goal.goal.name || value.goal.targetAmount !== goal.goal.targetAmount || value.goal.currency !== goal.goal.currency || value.goal.targetDate !== goal.goal.targetDate ||
    value.progressBasis !== 'CUSTOMER_REPORTED' || value.achievementStatus !== 'NOT_DETERMINED' || !Array.isArray(value.sources) || value.sources.length > 200 ||
    value.includedContributionCount !== value.sources.length || !Number.isInteger(value.withdrawnContributionCount) || value.withdrawnContributionCount < 0 ||
    !validMoney(value.reportedContributionAmount) || !validMoney(value.remainingAmount) || !validMoney(value.progressPercent) || !validDate(value.latestContributionDate) || Number.isNaN(Date.parse(value.observedAt))) return false;
  const ids = new Set<string>(); let total = 0; let latest: string | null = null;
  for (const source of value.sources) {
    if (!source.goalContributionId || ids.has(source.goalContributionId) || !Number.isInteger(source.contributionRevision) || source.contributionRevision < 1 ||
      !Number.isInteger(source.goalRevision) || source.goalRevision < 1 || !validMoney(source.amount, true) || !validDate(source.contributionDate)) return false;
    ids.add(source.goalContributionId); total += source.amount; if (latest === null || source.contributionDate > latest) latest = source.contributionDate;
  }
  total = Number(total.toFixed(2));
  const remaining = Number(Math.max(0, goal.goal.targetAmount - total).toFixed(2));
  const percent = Number((total / goal.goal.targetAmount * 100).toFixed(2));
  return value.reportedContributionAmount === total && value.remainingAmount === remaining && value.progressPercent === percent && value.latestContributionDate === latest;
};

export default function FinancialGoalProgressExperience({ api, goal }: { api: AccessApi; goal: ProductFinancialGoal }) {
  const context = useMemo(() => ({ api, goal }), [api, goal]); const scope = useRef(context);
  useLayoutEffect(() => { scope.current = context; }, [context]);
  const sequence = useRef(0); const pending = useRef<AbortController | null>(null);
  const [progress, setProgress] = useState<{ context: typeof context; value: ProductFinancialGoalProgress } | null>(null);
  const [failure, setFailure] = useState<{ context: typeof context; text: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const load = useCallback(async (signal?: AbortSignal) => {
    const generation = ++sequence.current; pending.current?.abort(); const controller = new AbortController(); pending.current = controller;
    const abort = () => { controller.abort(); }; signal?.addEventListener('abort', abort, { once: true }); const active = () => scope.current === context && generation === sequence.current && !signal?.aborted && !controller.signal.aborted;
    setLoading(true); setFailure(null); setProgress(null);
    try {
      const value = await getFinancialGoalProgress(api, goal.financialGoalId, controller.signal);
      if (!bound(value, goal)) throw new Error('Invalid goal progress');
      if (active()) setProgress({ context, value });
    } catch (error) {
      if (active()) setFailure({ context, text: error instanceof AccessApiError && error.status === 403 ? 'Your current access does not include goal progress.' : error instanceof AccessApiError && error.status === 409 ? 'Progress is unavailable because the goal currency changed. Review the contribution currencies before calculating progress.' : 'Goal progress is unavailable. Try again.' });
    } finally { signal?.removeEventListener('abort', abort); if (active()) setLoading(false); }
  }, [api, context, goal]);
  useEffect(() => { const controller = new AbortController(); const timer = window.setTimeout(() => { void load(controller.signal); }, 0); return () => { window.clearTimeout(timer); controller.abort(); pending.current?.abort(); }; }, [load]);
  const value = progress?.context === context ? progress.value : null;
  return <section aria-label={'Goal progress for ' + goal.goal.name}>
    <h2>{goal.goal.name}</h2><Tag>Customer reported</Tag>
    <p>Progress uses the latest saved version of each contribution report for this goal. Withdrawn reports are excluded.</p>
    {loading && <p role="status">Loading goal progress…</p>}
    {failure?.context === context && <Alert type="error" title={failure.text} action={<Button onClick={() => { void load(); }}>Retry goal progress</Button>} />}
    {value && <Card size="small" style={{ marginBlock: 12 }}>
      <Progress percent={Math.min(100, value.progressPercent)} format={() => `${value.progressPercent.toFixed(2)}%`} />
      <dl><dt>Goal amount</dt><dd>{amount(value.goal.targetAmount, value.goal.currency)}</dd>
        <dt>Reported contributions</dt><dd>{amount(value.reportedContributionAmount, value.goal.currency)}</dd>
        <dt>Remaining amount</dt><dd>{amount(value.remainingAmount, value.goal.currency)}</dd>
        <dt>Included contribution reports</dt><dd>{value.includedContributionCount}</dd>
        <dt>Withdrawn reports excluded</dt><dd>{value.withdrawnContributionCount}</dd>
        <dt>Latest included contribution date</dt><dd>{value.latestContributionDate ?? 'No included contributions'}</dd>
        <dt>Goal version</dt><dd>{value.goalRevision}</dd><dt>Observed</dt><dd>{value.observedAt}</dd></dl>
      <p>This is based on customer-reported records. It does not verify payment or allocation, calculate future required contributions, or confirm achievement.</p>
    </Card>}
  </section>;
}
