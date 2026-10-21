import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { NetWorthExperience } from '../src/product/NetWorthExperience';
import { exactMoney } from '../src/product/netWorthApi';
import type { NetWorthDraft, NetWorthLine, NetWorthPosition, NetWorthResult } from '../src/product/netWorthApi';
const session = { tenantId: 'T001', actorId: 'actor-001', authenticationContextId: 'session-001', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const line: NetWorthLine = { lineId: 'c'.repeat(32), side: 'ASSET', kind: 'BANK_ACCOUNT', name: 'Savings', amount: '1000.10', currency: 'INR', valueDate: '2026-10-01', sourceNote: 'Reviewed bank statement', record: null };
const draft: NetWorthDraft = { asOfDate: '2026-10-01', currency: 'INR', lines: [line], assetsComplete: true, liabilitiesComplete: true, replacesPositionId: null };
const position: NetWorthPosition = { positionId: 'position-saved', operationId: 'a'.repeat(32), customerId: 'reference-customer', position: draft, lines: [{ line, source: null }], recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' };
const result: NetWorthResult = { calculationId: 'networth-saved', operationId: 'b'.repeat(32), customerId: position.customerId, position, ruleId: 'monergy.net-worth', ruleVersion: '1.0.0', definitionHash: 'a'.repeat(64), numericSemantics: 'Exact reviewed asset values minus outstanding liabilities', state: 'CALCULATED', totalAssets: '1000.10', totalLiabilities: '0.00', netWorth: '1000.10', issues: [], recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' };
const page = <T,>(items: T[] = []) => ({ items, hasMore: false, nextBeforeId: null });
function element(api: AccessApi, props: Partial<{ canRead: boolean; canWrite: boolean; canCalculate: boolean; canReadDocuments: boolean; customerId: string }> = {}) { return <FoundationProvider appearance="midnight"><NetWorthExperience api={api} customerId={position.customerId} canRead canWrite canCalculate canReadDocuments {...props} /></FoundationProvider>; }
afterEach(() => { cleanup(); vi.restoreAllMocks(); window.history.replaceState({}, '', '/'); });
function backend(api: AccessApi, options: { initial?: boolean; issue?: string } = {}) {
  let saved = options.initial ? position : null; let calculated = options.initial ? result : null;
  return vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (method === 'GET') return Promise.resolve(path === 'v1/net-worth/positions' ? page(saved ? [saved] : []) : path === 'v1/net-worth/calculations' ? page(calculated ? [calculated] : []) : calculated);
    if (path === 'v1/net-worth/positions') {
      const input = body as { operationId: string; position: NetWorthDraft }; saved = { ...position, operationId: input.operationId, position: input.position, lines: input.position.lines.map(item => ({ line: item, source: null })) }; return Promise.resolve(saved);
    }
    const input = body as { operationId: string; positionId: string }; calculated = { ...result, operationId: input.operationId, position: saved ?? position, ...(options.issue ? { state: 'INSUFFICIENT_DATA', totalAssets: null, totalLiabilities: null, netWorth: null, issues: [options.issue] } : {}) }; return Promise.resolve(calculated);
  });
}
async function form(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: 'Start net worth review' })); const drawer = screen.getByRole('dialog', { name: 'Review net worth' });
  fireEvent.change(within(drawer).getByLabelText('Valuation date'), { target: { value: draft.asOfDate } }); await user.type(within(drawer).getByLabelText('Review currency'), 'INR'); return drawer;
}
async function asset(user: ReturnType<typeof userEvent.setup>, drawer: HTMLElement) {
  await user.click(within(drawer).getByRole('button', { name: 'Add asset' })); await user.type(within(drawer).getByLabelText('Name for line 1'), 'Savings'); await user.type(within(drawer).getByLabelText('Amount for line 1'), '1000.10'); await user.type(within(drawer).getByLabelText('Source for line 1'), 'Reviewed bank statement');
}
async function confirm(user: ReturnType<typeof userEvent.setup>, drawer: HTMLElement) {
  await user.click(within(drawer).getByRole('checkbox', { name: 'I have reviewed and included all my assets, or confirm I have none.' })); await user.click(within(drawer).getByRole('checkbox', { name: 'I have reviewed and included all my liabilities, or confirm I have none.' }));
}
it('completes review,explicit coverage,server calculation,history and exact decimal display', async () => {
  const api = new AccessApi(session); const request = backend(api); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); expect(within(drawer).getAllByRole('checkbox').every(box => !(box as HTMLInputElement).checked)).toBe(true); await confirm(user, drawer);
  expect(request.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(0); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })); const saved = await screen.findByRole('dialog', { name: 'Saved net worth result' }); expect(saved).toHaveTextContent('INR 1,000.10'); expect(saved).toHaveTextContent('Total liabilities'); expect(saved).toHaveTextContent('INR 0.00'); expect(saved).toHaveTextContent('Customer declaration');
  const writes = request.mock.calls.filter(([, method]) => method === 'POST'); expect(writes).toHaveLength(2); expect(writes[0]?.[0]).toBe('v1/net-worth/positions'); expect(writes[1]?.[0]).toBe('v1/net-worth/calculations');
  await user.click(within(saved).getByRole('button', { name: 'Close' })); await user.click(await screen.findByRole('button', { name: 'View saved result: 2026-10-01' })); expect(await screen.findByRole('region', { name: 'Saved net worth calculation' })).toHaveTextContent('Rule version 1.0.0');
});
it.each(['ASSET_INVENTORY_NOT_CONFIRMED', 'LIABILITY_INVENTORY_NOT_CONFIRMED', 'MISSING_VALUE', 'MISSING_VALUE_DATE', 'MIXED_VALUE_DATES', 'MIXED_CURRENCIES', 'OVERLAPPING_SOURCE_RECORDS'])('shows actionable %s without inventing a total', async issue => {
  const api = new AccessApi(session); backend(api, { issue }); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })); const saved = await screen.findByRole('dialog', { name: 'Saved net worth result' }); expect(saved).toHaveTextContent('More information needed before net worth can be calculated'); expect(within(saved).queryByText('Total assets')).not.toBeInTheDocument(); expect(within(saved).queryByText('INR 0.00')).not.toBeInTheDocument();
});
it('accepts an explicitly reviewed empty inventory and displays negative results without clamping', async () => {
  const api = new AccessApi(session); const negative = { ...result, totalAssets: '10.10', totalLiabilities: '90.20', netWorth: '-80.10' }; vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path === 'v1/net-worth/positions' ? page([position]) : path === 'v1/net-worth/calculations' ? page([negative]) : negative)); render(element(api, { canWrite: false })); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'View saved result: 2026-10-01' })); expect(screen.getByRole('dialog', { name: 'Saved net worth result' })).toHaveTextContent('INR -80.10');
  expect(exactMoney('199999999999998.01', 'INR')).toBe('INR 199,999,999,999,998.01');
});
it('retains identical uncertain inventory request through closing and reopening', async () => {
  const api = new AccessApi(session); let attempts = 0; let saved = position; const request = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (method === 'GET') return Promise.resolve(page());
    if (path.endsWith('/positions')) { const input = body as { operationId: string; position: NetWorthDraft }; saved = { ...position, operationId: input.operationId, position: input.position, lines: input.position.lines.map(item => ({ line: item, source: null })) }; return ++attempts === 1 ? Promise.reject(new AccessApiError(503, 'unavailable')) : Promise.resolve(saved); }
    return Promise.resolve({ ...result, operationId: (body as { operationId: string }).operationId, position: saved });
  }); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await confirm(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })); await within(drawer).findByText(/Saving has not been confirmed/); expect(within(drawer).getByLabelText('Review currency')).toBeDisabled(); await user.click(within(drawer).getByRole('button', { name: 'Close' })); await user.click(screen.getByRole('button', { name: 'Continue net worth confirmation' })); await user.click(screen.getByRole('button', { name: 'Retry net worth confirmation' })); await screen.findByRole('dialog', { name: 'Saved net worth result' });
  const writes = request.mock.calls.filter(([path, method]) => method === 'POST' && path.endsWith('/positions')); expect(writes).toHaveLength(2); expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
it('retries only the identical calculation after inventory confirmation', async () => {
  const api = new AccessApi(session); let saved = position; let attempts = 0; const request = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (method === 'GET') return Promise.resolve(page()); if (path.endsWith('/positions')) { const input = body as { operationId: string; position: NetWorthDraft }; saved = { ...position, operationId: input.operationId, position: input.position, lines: input.position.lines.map(item => ({ line: item, source: null })) }; return Promise.resolve(saved); }
    return ++attempts === 1 ? Promise.reject(new AccessApiError(503, 'unavailable')) : Promise.resolve({ ...result, operationId: (body as { operationId: string }).operationId, position: saved });
  }); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })); await within(drawer).findByText(/Calculation has not been confirmed/); await user.click(within(drawer).getByRole('button', { name: 'Retry net worth confirmation' })); await screen.findByRole('dialog', { name: 'Saved net worth result' });
  expect(request.mock.calls.filter(([path, method]) => method === 'POST' && path.endsWith('/positions'))).toHaveLength(1); const calculations = request.mock.calls.filter(([path, method]) => method === 'POST' && path.endsWith('/calculations')); expect(calculations).toHaveLength(2); expect(calculations[0]?.[2]).toEqual(calculations[1]?.[2]);
});
it.each([400, 409])('requires renewed review after a confirmed %s rejection', async status => {
  const api = new AccessApi(session); vi.spyOn(api, 'product').mockImplementation((_path, method) => method === 'GET' ? Promise.resolve(page()) : Promise.reject(new AccessApiError(status, 'rejected'))); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })); await within(drawer).findByText(/The review was rejected/); expect(within(drawer).getByLabelText('Review currency'))[status === 409 ? 'toBeDisabled' : 'toBeEnabled']();
});
it.each(['customer', 'operation', 'position', 'rule', 'hash', 'partial'])('rejects a substituted calculation %s confirmation', async field => {
  const api = new AccessApi(session); let saved = position; vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (method === 'GET') return Promise.resolve(page()); if (path.endsWith('/positions')) { const input = body as { operationId: string; position: NetWorthDraft }; saved = { ...position, operationId: input.operationId, position: input.position, lines: input.position.lines.map(item => ({ line: item, source: null })) }; return Promise.resolve(saved); }
    const changed = { ...result, operationId: (body as { operationId: string }).operationId, position: saved }; if (field === 'customer') changed.customerId = 'foreign'; if (field === 'operation') changed.operationId = 'f'.repeat(32); if (field === 'position') changed.position = position; if (field === 'rule') changed.ruleVersion = '2.0.0'; if (field === 'hash') changed.definitionHash = 'bad'; if (field === 'partial') changed.state = 'INSUFFICIENT_DATA'; return Promise.resolve(changed);
  }); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })); await within(drawer).findByText(/Calculation has not been confirmed/); expect(screen.queryByRole('dialog', { name: 'Saved net worth result' })).not.toBeInTheDocument();
});
it('requires name,source,valid date/currency and monetary cents while retaining missing values', async () => {
  const api = new AccessApi(session); backend(api); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await user.click(within(drawer).getByRole('button', { name: 'Add asset' })); expect(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })).toBeDisabled(); await user.type(within(drawer).getByLabelText('Name for line 1'), 'Savings'); await user.type(within(drawer).getByLabelText('Source for line 1'), 'Awaiting balance'); expect(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })).toBeEnabled(); fireEvent.change(within(drawer).getByLabelText('Amount for line 1'), { target: { value: '-1' } }); expect(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })).toBeDisabled();
});
it('resets completeness when the date,currency or inventory changes', async () => {
  const api = new AccessApi(session); backend(api); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await confirm(user, drawer); fireEvent.change(within(drawer).getByLabelText('Valuation date'), { target: { value: '2026-09-30' } }); expect(within(drawer).getAllByRole('checkbox').every(box => !(box as HTMLInputElement).checked)).toBe(true); await confirm(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Add liability' })); expect(within(drawer).getAllByRole('checkbox').every(box => !(box as HTMLInputElement).checked)).toBe(true);
});
it('starts an updated inventory with explicit review again and preserves the previous result', async () => {
  const api = new AccessApi(session); backend(api, { initial: true }); render(element(api)); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'Update this inventory: 2026-10-01' })); const drawer = screen.getByRole('dialog', { name: 'Review net worth' }); expect(within(drawer).getByLabelText('Amount for line 1')).toHaveValue('1000.10'); expect(within(drawer).getAllByRole('checkbox').every(box => !(box as HTMLInputElement).checked)).toBe(true);
});
it('read-only access can inspect history and calculate a saved inventory without exposing edits', async () => {
  const api = new AccessApi(session); backend(api, { initial: true }); render(element(api, { canWrite: false })); await screen.findByRole('button', { name: 'View saved inventory: 2026-10-01' }); expect(screen.queryByRole('button', { name: 'Start net worth review' })).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Update this inventory: 2026-10-01' })).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Calculate saved inventory: 2026-10-01' })).toBeVisible();
});
it('makes no request without current read access and hides data after capability removal', async () => {
  const api = new AccessApi(session); const request = backend(api, { initial: true }); const view = render(element(api, { canRead: false })); expect(request).not.toHaveBeenCalled(); view.rerender(element(api)); await screen.findByRole('button', { name: 'View saved inventory: 2026-10-01' }); view.rerender(element(api, { canRead: false })); expect(screen.queryByRole('button', { name: 'View saved inventory: 2026-10-01' })).not.toBeInTheDocument();
});
it('discards a late saved review after the customer changes', async () => {
  const api = new AccessApi(session); let finish: (value: unknown) => void = () => undefined; vi.spyOn(api, 'product').mockImplementation((_path, method) => method === 'GET' ? Promise.resolve(page()) : new Promise(resolve => { finish = resolve; })); const view = render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await asset(user, drawer); await user.click(within(drawer).getByRole('button', { name: 'Save review and calculate net worth' })); view.rerender(element(api, { customerId: 'another-customer' })); await act(async () => { finish(position); await Promise.resolve(); }); expect(screen.queryByRole('dialog')).not.toBeInTheDocument(); expect(screen.queryByText('INR 1,000.10')).not.toBeInTheDocument();
});
it.each(['customer', 'duplicate', 'cursor', 'partial'])('rejects malformed saved %s history without showing an empty balance', async kind => {
  const api = new AccessApi(session); const changed = structuredClone(result); if (kind === 'customer') changed.customerId = 'foreign'; if (kind === 'partial') changed.state = 'INSUFFICIENT_DATA'; vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.endsWith('/positions') ? page() : { items: kind === 'duplicate' ? [changed, changed] : [changed], hasMore: false, nextBeforeId: kind === 'cursor' ? 'other' : null })); render(element(api)); await screen.findByText(/Saved net worth reviews are unavailable/); expect(screen.queryByText('No saved net worth calculations yet. This does not mean zero net worth.')).not.toBeInTheDocument();
});
it('retains source drill-down and exact reviewed record versions in saved results', async () => {
  const api = new AccessApi(session); const linkedLine = { ...line, kind: 'INVESTMENT', record: { kind: 'INVESTMENT', recordId: 'investment-saved', revision: 2 } }; const linkedPosition = { ...position, position: { ...draft, lines: [linkedLine] }, lines: [{ line: linkedLine, source: { financialFactId: 'fact-original', sourceRevision: 1, provenanceId: 'provenance-original', documentVersionId: 'document-v1', evidenceId: 'evidence-v1', reviewedBy: 'actor-001', reviewedAt: '2026-10-01T00:00:00Z' } }] }; const linked = { ...result, position: linkedPosition }; vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.endsWith('/positions') ? page([linkedPosition]) : path.endsWith('/calculations') ? page([linked]) : linked)); render(element(api)); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'View saved result: 2026-10-01' })); expect(await screen.findByRole('link', { name: 'View source history for Savings' })).toHaveAttribute('href', '/finances?fact=fact-original'); expect(screen.getByText(/Record version 2 · Source revision 1/)).toBeVisible();
});
it('pages saved inventory and result histories using explicit cursors', async () => {
  const api = new AccessApi(session); const items = Array.from({ length: 50 }, (_, i) => ({ ...result, calculationId: 'networth-' + String(i) })); const request = vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.includes('/positions') ? page() : path.includes('?beforeId=networth-49') ? page([result]) : { items, hasMore: true, nextBeforeId: 'networth-49' })); render(element(api)); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'Earlier results' })); await waitFor(() => { expect(request.mock.calls.some(([path]) => path.endsWith('?beforeId=networth-49'))).toBe(true); });
});

const assetSnapshot = { asOfDate: draft.asOfDate, currency: 'INR', investments: { items: [{ investmentRecordId: 'investment-linked', customerId: position.customerId, revision: 2, currency: 'INR', investment: { name: 'Reviewed fund', currentValue: 200.01, valueAsOfDate: draft.asOfDate } }], hasMore: false }, properties: { items: [], hasMore: false }, vehicles: { items: [], hasMore: false } };
it('imports exact saved source versions without confirming completeness or permitting value substitution', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.startsWith('v1/asset-records/snapshot') ? assetSnapshot : path.startsWith('v1/liability-records/snapshot') ? { asOfDate: draft.asOfDate, currency: 'INR', items: [], hasMore: false } : page())); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await user.click(within(drawer).getByRole('button', { name: 'Add saved records for this date and currency' })); await within(drawer).findByText(/Saved records added/); expect(within(drawer).getByLabelText('Name for line 1')).toHaveValue('Reviewed fund'); expect(within(drawer).getByLabelText('Amount for line 1')).toHaveValue('200.01'); expect(within(drawer).getByLabelText('Amount for line 1')).toBeDisabled(); expect(within(drawer).getAllByRole('checkbox').every(box => !(box as HTMLInputElement).checked)).toBe(true); expect(drawer).toHaveTextContent('Saved reviewed record · version 2');
});
it.each(['truncated', 'customer', 'date'])('rejects %s source imports without applying partial inventory rows', async field => {
  const api = new AccessApi(session); const changed = structuredClone(assetSnapshot); if (field === 'truncated') changed.investments.hasMore = true; if (field === 'customer') { const item = changed.investments.items.at(0); if (item) item.customerId = 'foreign'; } if (field === 'date') changed.asOfDate = '2026-09-30'; vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.startsWith('v1/asset-records/snapshot') ? changed : path.startsWith('v1/liability-records/snapshot') ? { asOfDate: draft.asOfDate, currency: 'INR', items: [], hasMore: false } : page())); render(element(api)); const user = userEvent.setup(); const drawer = await form(user); await user.click(within(drawer).getByRole('button', { name: 'Add saved records for this date and currency' })); await within(drawer).findByText(/No partial import was applied/); expect(within(drawer).queryByLabelText('Name for line 1')).not.toBeInTheDocument();
});

it('opens an exact cited calculation without executing a new calculation', async () => {
  window.history.replaceState({}, '', '/net-worth?calculation=networth-saved');
  const api = new AccessApi(session); const request = backend(api, { initial: true }); render(element(api));
  expect(await screen.findByRole('dialog', { name: 'Saved net worth result' })).toHaveTextContent('INR 1,000.10');
  expect(request.mock.calls.some(([path, method]) => path === 'v1/net-worth/calculations/networth-saved' && method === 'GET')).toBe(true);
  expect(request.mock.calls.some(([, method]) => method === 'POST')).toBe(false);
});
it('does not fetch a cited calculation without calculation access', async () => {
  window.history.replaceState({}, '', '/net-worth?calculation=networth-saved');
  const api = new AccessApi(session); const request = backend(api, { initial: true }); render(element(api, { canCalculate: false }));
  await screen.findByRole('button', { name: 'View saved inventory: 2026-10-01' });
  expect(request.mock.calls.some(([path]) => path.includes('/calculations'))).toBe(false);
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
});
