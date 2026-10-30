import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import FinancialGoalProgressExperience from '../src/product/FinancialGoalProgressExperience';
import type { ProductFinancialGoal, ProductFinancialGoalProgress } from '../src/product/productApi';

const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'customer-session', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const goal: ProductFinancialGoal = { financialGoalId: 'goal-saved', operationId: 'a'.repeat(32), customerId: 'reference-customer', goal: { name: 'Education fund', targetAmount: 100000, currency: 'INR', targetDate: '2030-06-30' }, recordedBy: 'A100', recordedAt: '2026-10-20T00:00:00Z', declarationStatus: 'CUSTOMER_DEFINED', revision: 2 };
const value: ProductFinancialGoalProgress = { customerId: goal.customerId, financialGoalId: goal.financialGoalId, goalRevision: goal.revision, goal: goal.goal, reportedContributionAmount: 25000, remainingAmount: 75000, progressPercent: 25, includedContributionCount: 2, withdrawnContributionCount: 1, latestContributionDate: '2026-10-15', sources: [
  { goalContributionId: 'contribution-a', contributionRevision: 2, goalRevision: 1, amount: 10000, contributionDate: '2026-10-01' },
  { goalContributionId: 'contribution-b', contributionRevision: 1, goalRevision: 2, amount: 15000, contributionDate: '2026-10-15' },
], observedAt: '2026-10-30T10:00:00Z', progressBasis: 'CUSTOMER_REPORTED', achievementStatus: 'NOT_DETERMINED' };

const element = (api: AccessApi, record = goal) => <FoundationProvider appearance="midnight"><FinancialGoalProgressExperience api={api} goal={record} /></FoundationProvider>;
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('shows deterministic reported progress and its exact safe basis', async () => {
  const api = new AccessApi(session); const request = vi.spyOn(api, 'product').mockResolvedValue(value); render(element(api));
  expect(await screen.findByText('25.00%')).toBeVisible(); expect(screen.getByText(/₹25,000/)).toBeVisible(); expect(screen.getByText(/₹75,000/)).toBeVisible(); expect(screen.getByText(/Withdrawn reports excluded/).nextElementSibling).toHaveTextContent('1'); expect(screen.getByText(/does not verify payment or allocation/)).toBeVisible();
  expect(request).toHaveBeenCalledWith('v1/financial-goals/goal-saved/progress', 'GET', undefined, expect.any(AbortSignal));
});

it('shows a truthful zero state without inferring achievement', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'product').mockResolvedValue({ ...value, reportedContributionAmount: 0, remainingAmount: 100000, progressPercent: 0, includedContributionCount: 0, withdrawnContributionCount: 0, latestContributionDate: null, sources: [] }); render(element(api));
  expect(await screen.findByText('0.00%')).toBeVisible(); expect(screen.getByText('No included contributions')).toBeVisible(); expect(screen.getByText(/confirm achievement/)).toBeVisible();
});

it('explains a currency change without displaying partial progress', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'product').mockRejectedValue(new AccessApiError(409, 'GOAL_PROGRESS_CURRENCY_CHANGED')); render(element(api));
  expect(await screen.findByText(/goal currency changed/)).toBeVisible(); expect(screen.queryByText('25.00%')).not.toBeInTheDocument();
});

it('retries an unavailable progress read without changing its target', async () => {
  const api = new AccessApi(session); let attempts = 0; const request = vi.spyOn(api, 'product').mockImplementation(() => ++attempts === 1 ? Promise.reject(new AccessApiError(503, 'unavailable')) : Promise.resolve(value)); render(element(api)); const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Retry goal progress' })); expect(await screen.findByText('25.00%')).toBeVisible(); expect(request).toHaveBeenCalledTimes(2);
});

it.each(['customer', 'goal', 'revision', 'amount', 'remaining', 'percent', 'count', 'duplicate', 'status', 'latest'])('rejects substituted %s progress evidence', async kind => {
  const changed = structuredClone(value); if (kind === 'customer') changed.customerId = 'foreign'; if (kind === 'goal') changed.financialGoalId = 'goal-other'; if (kind === 'revision') changed.goalRevision = 9; if (kind === 'amount') changed.reportedContributionAmount = 1; if (kind === 'remaining') changed.remainingAmount = 1; if (kind === 'percent') changed.progressPercent = 99; if (kind === 'count') changed.includedContributionCount = 9; if (kind === 'duplicate') { const [first, second] = changed.sources; if (first && second) second.goalContributionId = first.goalContributionId; } if (kind === 'status') changed.achievementStatus = 'ACHIEVED'; if (kind === 'latest') changed.latestContributionDate = '2026-10-01';
  const api = new AccessApi(session); vi.spyOn(api, 'product').mockResolvedValue(changed); render(element(api)); expect(await screen.findByText('Goal progress is unavailable. Try again.')).toBeVisible(); expect(screen.queryByText('25.00%')).not.toBeInTheDocument();
});

it('discards a late progress result after the current session changes', async () => {
  const api = new AccessApi(session); let finish: (result: ProductFinancialGoalProgress) => void = () => undefined; const request = vi.spyOn(api, 'product').mockImplementation(() => new Promise(resolve => { finish = resolve; })); const view = render(element(api)); await waitFor(() => { expect(request).toHaveBeenCalled(); });
  const next = new AccessApi({ ...session, actorId: 'A200' }); vi.spyOn(next, 'product').mockResolvedValue({ ...value, reportedContributionAmount: 0, remainingAmount: 100000, progressPercent: 0, includedContributionCount: 0, latestContributionDate: null, sources: [] }); view.rerender(element(next)); expect(await screen.findByText('0.00%')).toBeVisible(); await act(async () => { finish(value); await Promise.resolve(); }); expect(screen.queryByText('25.00%')).not.toBeInTheDocument();
});
