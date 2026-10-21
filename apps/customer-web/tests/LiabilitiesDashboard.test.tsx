import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import LiabilitiesDashboard from '../src/product/LiabilitiesDashboard';
import type { NetWorthLine, NetWorthResult } from '../src/product/netWorthApi';
const session = { tenantId: 'T001', actorId: 'actor-001', authenticationContextId: 'session-001', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const kinds = ['LOAN', 'CREDIT_CARD', 'OTHER_LIABILITY'];
const lines: NetWorthLine[] = kinds.map((kind, i) => ({ lineId: String(i).repeat(32), side: 'LIABILITY', kind, name: `Liability ${String(i)}`, amount: '10.01', currency: 'INR', valueDate: '2026-10-01', sourceNote: 'Customer reviewed value', record: null }));
const result: NetWorthResult = { calculationId: 'calculation-001', operationId: 'a'.repeat(32), customerId: 'customer-001', position: { positionId: 'position-001', operationId: 'b'.repeat(32), customerId: 'customer-001', position: { asOfDate: '2026-10-01', currency: 'INR', lines, assetsComplete: true, liabilitiesComplete: true, replacesPositionId: null }, lines: lines.map(line => ({ line, source: null })), recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' }, ruleId: 'monergy.net-worth', ruleVersion: '1.0.0', definitionHash: 'a'.repeat(64), numericSemantics: 'Exact decimal values', state: 'CALCULATED', totalAssets: '0.00', totalLiabilities: '30.03', netWorth: '-30.03', issues: [], recordedBy: 'actor-001', recordedAt: '2026-10-20T00:00:00Z' };
const page = (items: NetWorthResult[] = []) => ({ items, hasMore: false, nextBeforeId: null });
const view = (api: AccessApi, customerId = 'customer-001', canReadDocuments = true) => <FoundationProvider appearance="midnight"><LiabilitiesDashboard api={api} customerId={customerId} canReadDocuments={canReadDocuments} /></FoundationProvider>;
const backend = (api: AccessApi, item = result) => vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path === 'v1/net-worth/calculations' ? page([item]) : item));
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
it('uses the authoritative total and shows all three liability kinds with honest review context', async () => {
  const api = new AccessApi(session); const request = backend(api); render(view(api));
  expect(await screen.findByText('INR 30.03')).toBeVisible(); expect(screen.getByText(/Latest saved review by calculation time/)).toBeVisible();
  for (const name of ['Loans', 'Credit cards', 'Other liabilities']) expect(screen.getByRole('option', { name: `${name} (1)` })).toBeInTheDocument();
  expect(screen.getByText(/Later records and corrections do not update/)).toBeVisible();
  expect(request.mock.calls.map(call => call[0])).toEqual(['v1/net-worth/calculations', 'v1/net-worth/calculations/calculation-001']);
  fireEvent.change(screen.getByLabelText('Liability category'), { target: { value: 'LOAN' } });
  expect(screen.getByText('INR 30.03')).toBeVisible(); expect(screen.queryByText(/Liability 1 ·/)).not.toBeInTheDocument();
});
it('never substitutes an older total when the latest review is incomplete', async () => {
  const api = new AccessApi(session); const incomplete: NetWorthResult = { ...result, calculationId: 'incomplete', state: 'INSUFFICIENT_DATA', totalAssets: null, totalLiabilities: null, netWorth: null, issues: ['LIABILITY_INVENTORY_NOT_CONFIRMED'], position: { ...result.position, position: { ...result.position.position, liabilitiesComplete: false } } };
  vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path === 'v1/net-worth/calculations' ? page([incomplete, result]) : incomplete));
  render(view(api)); expect(await screen.findByText(/Not calculated/)).toBeVisible(); expect(screen.queryByText('INR 30.03')).not.toBeInTheDocument(); expect(screen.getByText(/Review and confirm that all liabilities/)).toBeVisible();
});
it('distinguishes an empty history from confirmed zero liabilities', async () => {
  const api = new AccessApi(session); const request = vi.spyOn(api, 'product').mockResolvedValue(page()); const mounted = render(view(api));
  expect(await screen.findByText(/No saved liability review yet/)).toBeVisible(); expect(screen.queryByText('INR 0.00')).not.toBeInTheDocument();
  const zero = { ...result, totalLiabilities: '0.00', netWorth: '0.00', position: { ...result.position, lines: [], position: { ...result.position.position, lines: [] } } };
  request.mockImplementation(path => Promise.resolve(path === 'v1/net-worth/calculations' ? page([zero]) : zero)); mounted.rerender(view(api));
  await userEvent.click(screen.getByRole('button', { name: 'Refresh latest liability review' })); expect(await screen.findByText('INR 0.00')).toBeVisible(); expect(screen.getByText(/explicitly confirmed no liabilities/)).toBeVisible();
});
it.each(['foreign', 'missing', 'mismatch', 'unavailable'])('hides values when fresh owner confirmation is %s', async failure => {
  const api = new AccessApi(session); vi.spyOn(api, 'product').mockImplementation(path => path === 'v1/net-worth/calculations' ? Promise.resolve(page([result])) : failure === 'unavailable' ? Promise.reject(new Error('denied')) : Promise.resolve(failure === 'missing' ? null : { ...result, ...(failure === 'foreign' ? { customerId: 'foreign' } : { totalLiabilities: '1.00' }) }));
  render(view(api)); expect(await screen.findByText(/could not be confirmed/)).toBeVisible(); expect(screen.queryByText('INR 30.03')).not.toBeInTheDocument();
});
it('retains exact linked record, source revision and document version without inventing declaration sources', async () => {
  const api = new AccessApi(session); const linked = structuredClone(result); const line = linked.position.position.lines[2]; if (!line) throw new Error('Missing fixture line'); line.record = { kind: 'LIABILITY', recordId: 'liability-001', revision: 2 }; linked.position.lines[2] = { line, source: { financialFactId: 'fact-001', sourceRevision: 3, provenanceId: 'prov-001', documentVersionId: 'version-001', evidenceId: 'evidence-001', reviewedBy: 'actor-001', reviewedAt: '2026-10-19T00:00:00Z' } }; backend(api, linked); const mounted = render(view(api));
  await screen.findByText('INR 30.03'); expect(screen.getByRole('link', { name: 'View source document for Liability 2', hidden: true })).toHaveAttribute('href', '/evidence?version=version-001'); expect(screen.getByRole('link', { name: 'View source history for Liability 2', hidden: true })).toHaveAttribute('href', '/finances?fact=fact-001');
  expect(screen.queryByRole('link', { name: 'View source history for Liability 0', hidden: true })).not.toBeInTheDocument();
  mounted.rerender(view(api, 'customer-001', false)); await screen.findByText('INR 30.03'); expect(screen.queryByRole('link', { name: 'View source document for Liability 2', hidden: true })).not.toBeInTheDocument();
});
it('does not retain values after a refresh is denied', async () => {
  const api = new AccessApi(session); const request = backend(api); render(view(api)); await screen.findByText('INR 30.03'); request.mockRejectedValue(new Error('revoked')); await userEvent.click(screen.getByRole('button', { name: 'Refresh latest liability review' })); await screen.findByText(/could not be confirmed/); expect(screen.queryByText('INR 30.03')).not.toBeInTheDocument();
});
it('discards late responses from a previous customer even if transport ignores abort', async () => {
  const api = new AccessApi(session); let finish: (value: unknown) => void = () => undefined; const request = vi.spyOn(api, 'product').mockImplementation(() => new Promise(resolve => { finish = resolve; })); const mounted = render(view(api)); await act(async () => { await Promise.resolve(); }); const prior = finish; request.mockResolvedValue(page()); mounted.rerender(view(api, 'customer-002')); await act(async () => { prior(page([result])); await Promise.resolve(); }); expect(await screen.findByText(/No saved liability review yet/)).toBeVisible(); expect(screen.queryByText('INR 30.03')).not.toBeInTheDocument();
});
it('pages saved reviews with the owner cursor and returns to latest without combining totals', async () => {
  const api = new AccessApi(session); const items = Array.from({ length: 50 }, (_, i) => ({ ...result, calculationId: `calculation-${String(i)}` })); const older = { ...result, calculationId: 'older', position: { ...result.position, position: { ...result.position.position, asOfDate: '2026-09-01' } } };
  const request = vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path === 'v1/net-worth/calculations' ? { items, hasMore: true, nextBeforeId: 'calculation-49' } : path.includes('?') ? page([older]) : path.endsWith('/older') ? older : items[0]));
  render(view(api)); await screen.findByText('INR 30.03'); await userEvent.click(screen.getByRole('button', { name: 'Older liability reviews' })); await screen.findByText(/Balance date: 2026-09-01/); expect(request.mock.calls.some(call => call[0] === 'v1/net-worth/calculations?beforeId=calculation-49')).toBe(true); expect(screen.getAllByText('INR 30.03')).toHaveLength(1);
  await userEvent.click(screen.getByRole('button', { name: 'Refresh latest liability review' })); await screen.findByText(/Balance date: 2026-10-01 · Review currency/);
});
it('reconfirms a selected historical review and hides prior values while it loads', async () => {
  const api = new AccessApi(session); const older = { ...result, calculationId: 'older', recordedAt: '2026-10-19T00:00:00Z', totalLiabilities: '25.00', netWorth: '-25.00' }; vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path === 'v1/net-worth/calculations' ? page([result, older]) : path.endsWith('/older') ? older : result)); render(view(api)); await screen.findByText('INR 30.03'); await userEvent.click(within(screen.getByRole('region', { name: 'Saved liability reviews' })).getByRole('button', { name: /saved 2026-10-19/ })); expect(await screen.findByText('INR 25.00')).toBeVisible(); expect(screen.queryByText('INR 30.03')).not.toBeInTheDocument(); expect(screen.getByText('Selected saved review')).toBeVisible();
});

it.each(['OVERLAPPING_SOURCE_RECORDS','MIXED_VALUE_DATES','MIXED_CURRENCIES','MISSING_VALUE'])('shows owner gap %s without a debt total',async issue=>{
 const api=new AccessApi(session);backend(api,{...result,state:'INSUFFICIENT_DATA',totalAssets:null,totalLiabilities:null,netWorth:null,issues:[issue]});render(view(api));expect(await screen.findByText(/Not calculated/)).toBeVisible();expect(screen.queryByText('INR 30.03')).not.toBeInTheDocument();expect(screen.getByText('Review gaps before using a total')).toBeVisible();
});
it('rejects a claimed calculated total when liability completeness is not confirmed',async()=>{
 const api=new AccessApi(session);backend(api,{...result,position:{...result.position,position:{...result.position.position,liabilitiesComplete:false}}});render(view(api));expect(await screen.findByText(/could not be confirmed/)).toBeVisible();expect(screen.queryByText('INR 30.03')).not.toBeInTheDocument();
});
it('does not turn asset entries into liabilities',async()=>{
 const api=new AccessApi(session);const mixed=structuredClone(result);const asset:NetWorthLine={lineId:'f'.repeat(32),side:'ASSET',kind:'CASH',name:'Emergency cash',amount:'20.00',currency:'INR',valueDate:'2026-10-01',sourceNote:'Customer declaration',record:null};mixed.position.position.lines.push(asset);mixed.position.lines.push({line:asset,source:null});mixed.totalAssets='20.00';mixed.netWorth='-10.03';backend(api,mixed);render(view(api));expect(await screen.findByText('INR 30.03')).toBeVisible();expect(screen.queryByText(/Emergency cash/)).not.toBeInTheDocument();expect(screen.getByRole('option',{name:'All liabilities (3)'})).toBeInTheDocument();
});
