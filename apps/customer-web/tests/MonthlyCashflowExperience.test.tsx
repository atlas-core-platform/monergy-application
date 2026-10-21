import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { MonthlyCashflowExperience } from '../src/product/MonthlyCashflowExperience';
import { exactMoney } from '../src/product/monthlyCashflowApi';
import type { MonthlyCashflowDraft, MonthlyCashflowLine, MonthlyCashflowPosition, MonthlyCashflowResult } from '../src/product/monthlyCashflowApi';
const session = { tenantId: 'T001', actorId: 'actor-001', authenticationContextId: 'session-001', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const line: MonthlyCashflowLine = { lineId: 'c'.repeat(32), side: 'INCOME', kind: 'BANK_ACCOUNT', name: 'Savings', amount: '1000.10', currency: 'INR', valueDate: '2026-09-30', sourceNote: 'Reviewed bank statement', record: null, member: 'Self' };
const draft: MonthlyCashflowDraft = { asOfDate: '2026-09-30', currency: 'INR', lines: [line], incomeComplete: true, expensesComplete: true, replacesPositionId: null, household: 'Self' };
const position: MonthlyCashflowPosition = { positionId: 'position-saved', operationId: 'a'.repeat(32), customerId: 'reference-customer', position: draft, lines: [{ line, source: null }], recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' };
const result: MonthlyCashflowResult = { calculationId: 'networth-saved', operationId: 'b'.repeat(32), customerId: position.customerId, position, ruleId: 'monergy.monthly-cashflow', ruleVersion: '1.0.0', definitionHash: 'a'.repeat(64), numericSemantics: 'Exact reviewed asset values minus outstanding expenses', state: 'CALCULATED', totalIncome: '1000.10', totalExpenses: '0.00', balance: '1000.10', issues: [], recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' };
const page = <T,>(items: T[] = []) => ({ items, hasMore: false, nextBeforeId: null });
function element(api: AccessApi, props: Partial<{ canRead: boolean; canWrite: boolean; canCalculate: boolean; canReadDocuments: boolean; customerId: string }> = {}) { return <FoundationProvider appearance="midnight"><MonthlyCashflowExperience api={api} customerId={position.customerId} canRead canWrite canCalculate canReadDocuments {...props} /></FoundationProvider>; }
afterEach(() => { cleanup(); vi.restoreAllMocks(); window.history.replaceState({}, '', '/'); });
function backend(api: AccessApi, options: { initial?: boolean; issue?: string } = {}) {
  let saved = options.initial ? position : null; let calculated = options.initial ? result : null;
  return vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (method === 'GET') return Promise.resolve(path === 'v1/monthly-cashflow/positions' ? page(saved ? [saved] : []) : path === 'v1/monthly-cashflow/calculations' ? page(calculated ? [calculated] : []) : calculated);
    if (path === 'v1/monthly-cashflow/positions') {
      const input = body as { operationId: string; position: MonthlyCashflowDraft }; saved = { ...position, operationId: input.operationId, position: input.position, lines: input.position.lines.map(item => ({ line: item, source: null })) }; return Promise.resolve(saved);
    }
    const input = body as { operationId: string; positionId: string }; calculated = { ...result, operationId: input.operationId, position: saved ?? position, ...(options.issue ? { state: 'INSUFFICIENT_DATA', totalIncome: null, totalExpenses: null, balance: null, issues: [options.issue] } : {}) }; return Promise.resolve(calculated);
  });
}
async function form(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: 'Start monthly cash flow review' })); const drawer = await screen.findByRole('dialog', { name: 'Review monthly cash flow' });
  fireEvent.change(within(drawer).getByLabelText('Completed month end'), { target: { value: draft.asOfDate } }); await user.type(within(drawer).getByLabelText('Review currency'), 'INR'); return drawer;
}
async function asset(user: ReturnType<typeof userEvent.setup>, drawer: HTMLElement) {
  await user.click(within(drawer).getByRole('button', { name: 'Add income' })); await user.type(within(drawer).getByLabelText('Name for line 1'), 'Savings'); await user.type(within(drawer).getByLabelText('Amount for line 1'), '1000.10'); await user.type(within(drawer).getByLabelText('Source for line 1'), 'Reviewed bank statement');
}
async function confirm(user: ReturnType<typeof userEvent.setup>, drawer: HTMLElement) {
  await user.click(within(drawer).getByRole('checkbox', { name: 'I have reviewed and included all my income, or confirm I have none.' })); await user.click(within(drawer).getByRole('checkbox', { name: 'I have reviewed and included all my expenses, or confirm I have none.' }));
}
it('completes review,explicit coverage,server calculation,history and exact decimal display', async () => {
  const api = new AccessApi(session); const request = backend(api); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); expect(within(drawer).getAllByRole('checkbox').every(box => !(box as HTMLInputElement).checked)).toBe(true); await confirm(user, drawer);
  expect(request.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(0); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })); const saved = await screen.findByRole('dialog', { name: 'Saved monthly cash flow result' }); expect(saved).toHaveTextContent('INR 1,000.10'); expect(saved).toHaveTextContent('Total expenses'); expect(saved).toHaveTextContent('INR 0.00'); expect(saved).toHaveTextContent('Customer declaration');
  const writes = request.mock.calls.filter(([, method]) => method === 'POST'); expect(writes).toHaveLength(2); expect(writes[0]?.[0]).toBe('v1/monthly-cashflow/positions'); expect(writes[1]?.[0]).toBe('v1/monthly-cashflow/calculations');
  await user.click(within(saved).getByRole('button', { name: 'Close' })); await user.click(await screen.findByRole('button', { name: 'View saved result: 2026-09-30' })); expect(await screen.findByRole('region', { name: 'Saved monthly cash flow calculation' })).toHaveTextContent('Rule version 1.0.0');
});
it.each(['INCOME_INVENTORY_NOT_CONFIRMED', 'EXPENSE_INVENTORY_NOT_CONFIRMED', 'MISSING_VALUE', 'MISSING_VALUE_DATE', 'MIXED_VALUE_DATES', 'MIXED_CURRENCIES', 'OVERLAPPING_SOURCE_RECORDS'])('shows actionable %s without inventing a total', async issue => {
  const api = new AccessApi(session); backend(api, { issue }); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })); const saved = await screen.findByRole('dialog', { name: 'Saved monthly cash flow result' }); expect(saved).toHaveTextContent('More information needed before monthly cash flow can be calculated'); expect(within(saved).queryByText('Total income')).not.toBeInTheDocument(); expect(within(saved).queryByText('INR 0.00')).not.toBeInTheDocument();
});
it('accepts an explicitly reviewed empty inventory and displays negative results without clamping', async () => {
  const api = new AccessApi(session); const negative = { ...result, totalIncome: '10.10', totalExpenses: '90.20', balance: '-80.10' }; vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path === 'v1/monthly-cashflow/positions' ? page([position]) : path === 'v1/monthly-cashflow/calculations' ? page([negative]) : negative)); render(element(api, { canWrite: false })); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'View saved result: 2026-09-30' })); expect(screen.getByRole('dialog', { name: 'Saved monthly cash flow result' })).toHaveTextContent('INR -80.10');
  expect(exactMoney('199999999999998.01', 'INR')).toBe('INR 199,999,999,999,998.01');
});
it('retains identical uncertain inventory request through closing and reopening', async () => {
  const api = new AccessApi(session); let attempts = 0; let saved = position; const request = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (method === 'GET') return Promise.resolve(page());
    if (path.endsWith('/positions')) { const input = body as { operationId: string; position: MonthlyCashflowDraft }; saved = { ...position, operationId: input.operationId, position: input.position, lines: input.position.lines.map(item => ({ line: item, source: null })) }; return ++attempts === 1 ? Promise.reject(new AccessApiError(503, 'unavailable')) : Promise.resolve(saved); }
    return Promise.resolve({ ...result, operationId: (body as { operationId: string }).operationId, position: saved });
  }); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await confirm(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })); await within(drawer).findByText(/Saving has not been confirmed/); expect(within(drawer).getByLabelText('Review currency')).toBeDisabled(); await user.click(within(drawer).getByRole('button', { name: 'Close' })); await user.click(screen.getByRole('button', { name: 'Continue monthly cash flow confirmation' })); await user.click(screen.getByRole('button', { name: 'Retry monthly cash flow confirmation' })); await screen.findByRole('dialog', { name: 'Saved monthly cash flow result' });
  const writes = request.mock.calls.filter(([path, method]) => method === 'POST' && path.endsWith('/positions')); expect(writes).toHaveLength(2); expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
it('retries only the identical calculation after inventory confirmation', async () => {
  const api = new AccessApi(session); let saved = position; let attempts = 0; const request = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (method === 'GET') return Promise.resolve(page()); if (path.endsWith('/positions')) { const input = body as { operationId: string; position: MonthlyCashflowDraft }; saved = { ...position, operationId: input.operationId, position: input.position, lines: input.position.lines.map(item => ({ line: item, source: null })) }; return Promise.resolve(saved); }
    return ++attempts === 1 ? Promise.reject(new AccessApiError(503, 'unavailable')) : Promise.resolve({ ...result, operationId: (body as { operationId: string }).operationId, position: saved });
  }); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await confirm(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })); await within(drawer).findByText(/Calculation has not been confirmed/); await user.click(within(drawer).getByRole('button', { name: 'Retry monthly cash flow confirmation' })); await screen.findByRole('dialog', { name: 'Saved monthly cash flow result' });
  expect(request.mock.calls.filter(([path, method]) => method === 'POST' && path.endsWith('/positions'))).toHaveLength(1); const calculations = request.mock.calls.filter(([path, method]) => method === 'POST' && path.endsWith('/calculations')); expect(calculations).toHaveLength(2); expect(calculations[0]?.[2]).toEqual(calculations[1]?.[2]);
});
it.each([400, 409])('requires renewed review after a confirmed %s rejection', async status => {
  const api = new AccessApi(session); vi.spyOn(api, 'product').mockImplementation((_path, method) => method === 'GET' ? Promise.resolve(page()) : Promise.reject(new AccessApiError(status, 'rejected'))); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })); await within(drawer).findByText(/The review was rejected/); expect(within(drawer).getByLabelText('Review currency'))[status === 409 ? 'toBeDisabled' : 'toBeEnabled']();
});
it.each(['customer', 'operation', 'position', 'rule', 'hash', 'partial'])('rejects a substituted calculation %s confirmation', async field => {
  const api = new AccessApi(session); let saved = position; vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (method === 'GET') return Promise.resolve(page()); if (path.endsWith('/positions')) { const input = body as { operationId: string; position: MonthlyCashflowDraft }; saved = { ...position, operationId: input.operationId, position: input.position, lines: input.position.lines.map(item => ({ line: item, source: null })) }; return Promise.resolve(saved); }
    const changed = { ...result, operationId: (body as { operationId: string }).operationId, position: saved }; if (field === 'customer') changed.customerId = 'foreign'; if (field === 'operation') changed.operationId = 'f'.repeat(32); if (field === 'position') changed.position = position; if (field === 'rule') changed.ruleVersion = '2.0.0'; if (field === 'hash') changed.definitionHash = 'bad'; if (field === 'partial') changed.state = 'INSUFFICIENT_DATA'; return Promise.resolve(changed);
  }); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })); await within(drawer).findByText(/Calculation has not been confirmed/); expect(screen.queryByRole('dialog', { name: 'Saved monthly cash flow result' })).not.toBeInTheDocument();
});
it('requires name,source,valid date/currency and monetary cents while retaining missing values', async () => {
  const api = new AccessApi(session); backend(api); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await user.click(within(drawer).getByRole('button', { name: 'Add income' })); expect(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })).toBeDisabled(); await user.type(within(drawer).getByLabelText('Name for line 1'), 'Savings'); await user.type(within(drawer).getByLabelText('Source for line 1'), 'Awaiting balance'); expect(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })).toBeEnabled(); fireEvent.change(within(drawer).getByLabelText('Amount for line 1'), { target: { value: '-1' } }); expect(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })).toBeDisabled();
});
it('resets completeness when the date,currency or inventory changes', async () => {
  const api = new AccessApi(session); backend(api); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await confirm(user, drawer); fireEvent.change(within(drawer).getByLabelText('Completed month end'), { target: { value: '2026-08-31' } }); expect(within(drawer).getAllByRole('checkbox').every(box => !(box as HTMLInputElement).checked)).toBe(true); await confirm(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Add expense' })); expect(within(drawer).getAllByRole('checkbox').every(box => !(box as HTMLInputElement).checked)).toBe(true);
});
it('starts an updated inventory with explicit review again and preserves the previous result', async () => {
  const api = new AccessApi(session); backend(api, { initial: true }); render(element(api)); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'Update this inventory: 2026-09-30' })); const drawer = await screen.findByRole('dialog', { name: 'Review monthly cash flow' }); expect(within(drawer).getByLabelText('Amount for line 1')).toHaveValue('1000.10'); expect(within(drawer).getAllByRole('checkbox').every(box => !(box as HTMLInputElement).checked)).toBe(true);
});
it('read-only access can inspect history and calculate a saved inventory without exposing edits', async () => {
  const api = new AccessApi(session); backend(api, { initial: true }); render(element(api, { canWrite: false })); await screen.findByRole('button', { name: 'View saved inventory: 2026-09-30' }); expect(screen.queryByRole('button', { name: 'Start monthly cash flow review' })).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Update this inventory: 2026-09-30' })).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Calculate saved inventory: 2026-09-30' })).toBeVisible();
});
it('makes no request without current read access and hides data after capability removal', async () => {
  const api = new AccessApi(session); const request = backend(api, { initial: true }); const view = render(element(api, { canRead: false })); expect(request).not.toHaveBeenCalled(); view.rerender(element(api)); await screen.findByRole('button', { name: 'View saved inventory: 2026-09-30' }); view.rerender(element(api, { canRead: false })); expect(screen.queryByRole('button', { name: 'View saved inventory: 2026-09-30' })).not.toBeInTheDocument();
});
it('discards a late saved review after the customer changes', async () => {
  const api = new AccessApi(session); let finish: (value: unknown) => void = () => undefined; vi.spyOn(api, 'product').mockImplementation((_path, method) => method === 'GET' ? Promise.resolve(page()) : new Promise(resolve => { finish = resolve; })); const view = render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate monthly cash flow' })); view.rerender(element(api, { customerId: 'another-customer' })); await act(async () => { finish(position); await Promise.resolve(); }); expect(screen.queryByRole('dialog')).not.toBeInTheDocument(); expect(screen.queryByText('INR 1,000.10')).not.toBeInTheDocument();
});
it.each(['customer', 'duplicate', 'cursor', 'partial'])('rejects malformed saved %s history without showing an empty balance', async kind => {
  const api = new AccessApi(session); const changed = structuredClone(result); if (kind === 'customer') changed.customerId = 'foreign'; if (kind === 'partial') changed.state = 'INSUFFICIENT_DATA'; vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.endsWith('/positions') ? page() : { items: kind === 'duplicate' ? [changed, changed] : [changed], hasMore: false, nextBeforeId: kind === 'cursor' ? 'other' : null })); render(element(api)); await screen.findByText(/Saved monthly cash flow reviews are unavailable/); expect(screen.queryByText('No saved monthly cash flow calculations yet. This does not mean zero monthly cash flow.')).not.toBeInTheDocument();
});
it('retains source drill-down and exact reviewed record versions in saved results', async () => {
  const api = new AccessApi(session); const linkedLine = { ...line, kind: 'INVESTMENT', record: { kind: 'INCOME', recordId: 'investment-saved', revision: 2 } }; const linkedPosition = { ...position, position: { ...draft, lines: [linkedLine] }, lines: [{ line: linkedLine, source: { financialFactId: 'fact-original', sourceRevision: 1, provenanceId: 'provenance-original', documentVersionId: 'document-v1', evidenceId: 'evidence-v1', reviewedBy: 'actor-001', reviewedAt: '2026-09-30T00:00:00Z' } }] }; const linked = { ...result, position: linkedPosition }; vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.endsWith('/positions') ? page([linkedPosition]) : path.endsWith('/calculations') ? page([linked]) : linked)); render(element(api)); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'View saved result: 2026-09-30' })); expect(await screen.findByRole('link', { name: 'View source history for Savings' })).toHaveAttribute('href', '/finances?fact=fact-original'); expect(screen.getByText(/Record version 2 · Source revision 1/)).toBeVisible();
});
it('pages saved inventory and result histories using explicit cursors', async () => {
  const api = new AccessApi(session); const items = Array.from({ length: 50 }, (_, i) => ({ ...result, calculationId: 'networth-' + String(i) })); const request = vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.includes('/positions') ? page() : path.includes('?beforeId=networth-49') ? page([result]) : { items, hasMore: true, nextBeforeId: 'networth-49' })); render(element(api)); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'Earlier results' })); await waitFor(() => { expect(request.mock.calls.some(([path]) => path.endsWith('?beforeId=networth-49'))).toBe(true); });
});

it('opens an exact cited calculation without executing a new calculation', async () => {
  window.history.replaceState({}, '', '/monthly-cashflow?calculation=networth-saved');
  const api = new AccessApi(session); const request = backend(api, { initial: true }); render(element(api));
  expect(await screen.findByRole('dialog', { name: 'Saved monthly cash flow result' })).toHaveTextContent('INR 1,000.10');
  expect(request.mock.calls.some(([path, method]) => path === 'v1/monthly-cashflow/calculations/networth-saved' && method === 'GET')).toBe(true);
  expect(request.mock.calls.some(([, method]) => method === 'POST')).toBe(false);
});
it('does not fetch a cited calculation without calculation access', async () => {
  window.history.replaceState({}, '', '/monthly-cashflow?calculation=networth-saved');
  const api = new AccessApi(session); const request = backend(api, { initial: true }); render(element(api, { canCalculate: false }));
  await screen.findByRole('button', { name: 'View saved inventory: 2026-09-30' });
  expect(request.mock.calls.some(([path]) => path.includes('/calculations'))).toBe(false);
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
});

it.each([false, true])('imports only exact whole-month records and rejects truncated lists (%s)', async truncated => {
  const api = new AccessApi(session); const handler = backend(api); const original = handler.getMockImplementation(); if (!original) throw new Error('Missing mock');
  handler.mockImplementation((path, method, body, signal) => path === 'v1/income-records' ? Promise.resolve({ items: [{ incomeRecordId: 'income-month', customerId: position.customerId, revision: 2, currency: 'INR', income: { name: 'Salary', incomeType: 'Salary', amount: 500.25, periodStart: '2026-09-01', periodEnd: '2026-09-30' } }, { incomeRecordId: 'income-year', customerId: position.customerId, revision: 1, currency: 'INR', income: { name: 'Annual salary', incomeType: 'Salary', amount: 6000, periodStart: '2026-01-01', periodEnd: '2026-09-30' } }], hasMore: truncated }) : path === 'v1/expense-records' ? Promise.resolve({ items: [], hasMore: false }) : original(path, method, body, signal));
  render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await user.click(within(drawer).getByRole('button', { name: 'Add saved records for this date and currency' }));
  if (truncated) { expect(await within(drawer).findByText(/could not be imported completely/)).toBeVisible(); expect(within(drawer).queryByLabelText('Amount for line 1')).not.toBeInTheDocument(); }
  else { const amount = await within(drawer).findByLabelText('Amount for line 1'); expect(amount).toHaveValue('500.25'); expect(amount).toBeDisabled(); expect(within(drawer).queryByDisplayValue('6000.00')).not.toBeInTheDocument(); expect(within(drawer).getAllByRole('checkbox').every(x => !(x as HTMLInputElement).checked)).toBe(true); }
});
