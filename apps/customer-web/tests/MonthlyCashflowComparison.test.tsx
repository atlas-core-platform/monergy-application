import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import MonthlyCashflowComparison from '../src/product/MonthlyCashflowComparison';
import type { MonthlyCashflowDraft, MonthlyCashflowLine, MonthlyCashflowPosition, MonthlyCashflowResult } from '../src/product/monthlyCashflowApi';
const session = { tenantId: 'T001', actorId: 'actor-001', authenticationContextId: 'session-001', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const line: MonthlyCashflowLine = { lineId: 'c'.repeat(32), side: 'INCOME', kind: 'Salary', name: 'Savings', amount: '1000.10', currency: 'INR', valueDate: '2026-09-30', sourceNote: 'Reviewed bank statement', record: null, member: 'Self' };
const draft: MonthlyCashflowDraft = { asOfDate: '2026-09-30', currency: 'INR', lines: [line], incomeComplete: true, expensesComplete: true, replacesPositionId: null, household: 'Self' };
const position: MonthlyCashflowPosition = { positionId: 'position-saved', operationId: 'a'.repeat(32), customerId: 'reference-customer', position: draft, lines: [{ line, source: null }], recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' };
const result: MonthlyCashflowResult = { calculationId: 'cashflow-saved', operationId: 'b'.repeat(32), customerId: position.customerId, position, ruleId: 'monergy.monthly-cashflow', ruleVersion: '1.0.0', definitionHash: 'a'.repeat(64), numericSemantics: 'Exact monthly income minus expenses', state: 'CALCULATED', totalIncome: '1000.10', totalExpenses: '0.00', balance: '1000.10', issues: [], recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' };



const earlier = { ...result, calculationId: 'earlier', position: { ...position, position: { ...draft, asOfDate: '2026-02-28' } } };
const later = { ...result, calculationId: 'later', position: { ...position, position: { ...draft, asOfDate: '2026-03-31' } } };
function view(api: AccessApi, items: MonthlyCashflowResult[] = [earlier, later], customerId = position.customerId) { return <FoundationProvider appearance="midnight"><MonthlyCashflowComparison api={api} customerId={customerId} items={items} /></FoundationProvider>; }
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
async function choose(label: string, date: string) { fireEvent.mouseDown(screen.getByRole('combobox', { name: label })); const matches = await screen.findAllByText(date + ' · Self · INR · saved ' + result.recordedAt); const option = matches[matches.length - 1]; if (!option) throw new Error('Missing option'); fireEvent.click(option); }
it('retains an earlier choice while paging and displays exact server changes for unequal months', async () => {
 const api = new AccessApi(session); const request = vi.spyOn(api, 'product').mockResolvedValue({ earlier, later, incomeChange: '10.01', expenseChange: '20.02', balanceChange: '-10.01' }); const { rerender } = render(view(api, [earlier])); await choose('Earlier month review', '2026-02-28'); rerender(view(api, [])); rerender(view(api, [later])); await choose('Later month review', '2026-03-31'); await userEvent.click(screen.getByRole('button', { name: 'Compare monthly totals' })); await screen.findByText('INR -10.01'); expect(request.mock.calls[0]?.[2]).toEqual({ earlierCalculationId: 'earlier', laterCalculationId: 'later' });
 rerender(view(api, [later], 'other-customer')); expect(screen.queryByText('INR -10.01')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Compare monthly totals' })).toBeDisabled();
});
it.each(['scope', 'order', 'money', 'denied'])('hides results for rejected %s comparison', async reason => {
 const api = new AccessApi(session); const value = { earlier, later, incomeChange: '10.01', expenseChange: '20.02', balanceChange: '-10.01' }; if (reason === 'scope') value.later = { ...later, customerId: 'foreign' }; if (reason === 'order') value.earlier = { ...earlier, position: { ...earlier.position, position: { ...draft, asOfDate: '2026-04-30' } } }; if (reason === 'money') value.incomeChange = 'NaN'; const request = vi.spyOn(api, 'product'); if (reason === 'denied') request.mockRejectedValue(new Error('denied')); else request.mockResolvedValue(value); render(view(api)); await choose('Earlier month review', '2026-02-28'); await choose('Later month review', '2026-03-31'); await userEvent.click(screen.getByRole('button', { name: 'Compare monthly totals' })); await screen.findByText(/These reviews could not be compared/); expect(screen.queryByText('INR -10.01')).not.toBeInTheDocument();
});
