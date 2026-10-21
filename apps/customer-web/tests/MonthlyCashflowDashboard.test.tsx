import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import MonthlyCashflowDashboard from '../src/product/MonthlyCashflowDashboard';
import type { MonthlyCashflowDraft, MonthlyCashflowLine, MonthlyCashflowPosition, MonthlyCashflowResult } from '../src/product/monthlyCashflowApi';
const session = { tenantId: 'T001', actorId: 'actor-001', authenticationContextId: 'session-001', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const line: MonthlyCashflowLine = { lineId: 'c'.repeat(32), side: 'INCOME', kind: 'Salary', name: 'Savings', amount: '1000.10', currency: 'INR', valueDate: '2026-09-30', sourceNote: 'Reviewed bank statement', record: null, member: 'Self' };
const draft: MonthlyCashflowDraft = { asOfDate: '2026-09-30', currency: 'INR', lines: [line], incomeComplete: true, expensesComplete: true, replacesPositionId: null, household: 'Self' };
const position: MonthlyCashflowPosition = { positionId: 'position-saved', operationId: 'a'.repeat(32), customerId: 'reference-customer', position: draft, lines: [{ line, source: null }], recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' };
const result: MonthlyCashflowResult = { calculationId: 'cashflow-saved', operationId: 'b'.repeat(32), customerId: position.customerId, position, ruleId: 'monergy.monthly-cashflow', ruleVersion: '1.0.0', definitionHash: 'a'.repeat(64), numericSemantics: 'Exact monthly income minus expenses', state: 'CALCULATED', totalIncome: '1000.10', totalExpenses: '0.00', balance: '1000.10', issues: [], recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' };
const page = <T,>(items: T[] = []) => ({ items, hasMore: false, nextBeforeId: null });

function view(api: AccessApi, customerId = position.customerId) { return <FoundationProvider appearance="midnight"><MonthlyCashflowDashboard api={api} customerId={customerId} /></FoundationProvider>; }
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
it('rereads the exact latest review and links its immutable source result', async () => {
 const api = new AccessApi(session); const request = vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.endsWith('/calculations') ? page([result]) : result)); render(view(api));
 const link = await screen.findByRole('link', { name: 'View exact monthly review and sources' }); expect(link).toHaveAttribute('href', '/monthly-cashflow?calculation=cashflow-saved');
 const overview = screen.getByRole('region', { name: 'Monthly income and expenses overview' }); expect(overview).toHaveTextContent('INR 1,000.10'); expect(overview).toHaveTextContent('INR 0.00'); expect(request).toHaveBeenCalledTimes(2);
});
it('keeps no review distinct from a zero total', async () => {
 const api = new AccessApi(session); vi.spyOn(api, 'product').mockResolvedValue(page()); render(view(api)); await screen.findByText(/This does not mean zero/); expect(screen.queryByText('INR 0.00')).not.toBeInTheDocument();
});
it('does not substitute an older complete review for incomplete latest coverage', async () => {
 const latest = { ...result, calculationId: 'incomplete', state: 'INSUFFICIENT_DATA', totalIncome: null, totalExpenses: null, balance: null, issues: ['EXPENSE_INVENTORY_NOT_CONFIRMED'] }; const api = new AccessApi(session); vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.endsWith('/calculations') ? page([latest, result]) : latest)); render(view(api)); await screen.findByText('Monthly coverage needs review'); expect(screen.queryByText('INR 1,000.10')).not.toBeInTheDocument();
});
it.each(['changed', 'denied'])('hides totals when exact result is %s after history read', async scenario => {
 const api = new AccessApi(session); vi.spyOn(api, 'product').mockImplementation(path => path.endsWith('/calculations') ? Promise.resolve(page([result])) : scenario === 'denied' ? Promise.reject(new Error('denied')) : Promise.resolve({ ...result, totalIncome: '9.00' })); render(view(api)); await screen.findByText(/Monthly totals are unavailable/); expect(screen.queryByText('INR 1,000.10')).not.toBeInTheDocument();
});
it('discards a pending response after the customer context changes', async () => {
 const api = new AccessApi(session); let complete!: (value: unknown) => void; vi.spyOn(api, 'product').mockImplementationOnce(() => new Promise(resolve => { complete = resolve; })).mockResolvedValue(page()); const { rerender } = render(view(api)); rerender(view(api, 'other-customer')); await screen.findByText(/This does not mean zero/); await act(async () => { complete(page([result])); await Promise.resolve(); }); expect(within(screen.getByRole('region', { name: 'Monthly income and expenses overview' })).queryByText('INR 1,000.10')).not.toBeInTheDocument();
});
