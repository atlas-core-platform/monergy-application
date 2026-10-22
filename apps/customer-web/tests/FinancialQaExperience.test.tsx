import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import FinancialQaExperience from '../src/product/FinancialQaExperience';

const session = {
  tenantId: 'T001',
  actorId: 'A100',
  authenticationContextId: 'customer-session',
  subjectVersion: 2,
  expiresAt: '2027-01-01T00:00:00Z',
};

function mount(api: AccessApi, capabilities = ['ai-intelligence.analysis.execute', 'search.query.execute', 'financial-profile.profile.read', 'evidence.document.read']) {
  render(
    <FoundationProvider appearance="midnight">
      <Context
        value={{
          status: 'authorized',
          session,
          api,
          authorization: {
            area: 'product',
            context: {
              tenantId: 'T001',
              actorId: 'A100',
              customerId: 'reference-customer',
              customerName: 'Reference Customer',
              customerDetail: 'Primary UAT customer',
              capabilities,
              environment: 'Test environment',
              dataDurability: 'Temporary data',
              productionAccepted: false,
            },
          },
          message: '',
          recovery: null,
          connect: () => Promise.resolve(),
          retry: () => Promise.resolve(),
          signOut: () => Promise.resolve(),
          cancel: () => undefined,
        }}
      >
        <FinancialQaExperience />
      </Context>
    </FoundationProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  window.history.replaceState({}, '', '/');
});

const configuration = { available: true, revision: 'a'.repeat(64), providerLabel: 'Configured provider', notice: 'Operator-published notice' };
const answer = { answerId: 'b'.repeat(32), operationId: 'c'.repeat(32), question: 'Show salary', state: 'SOURCES', introduction: 'Matching records, not verified balances.', claims: [{ id: 'source-0', text: 'Salary source 100 USD', kind: 'FinancialFact', sourceId: 'fact-1', revision: 2, provenanceId: 'provenance-1', documentVersionId: 'version-2' }], recordedAt: '2026-10-21T00:00:00Z', model: 'model-v1', promptVersion: 'qa/1', configurationRevision: configuration.revision };
it('requires per-question permission and shows exact source citations', async () => {
  const api = new AccessApi(session); const user = userEvent.setup();
  vi.spyOn(crypto, 'randomUUID').mockReturnValue('cccccccc-cccc-cccc-cccc-cccccccccccc');
  const request = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (path.endsWith('/configuration')) return Promise.resolve(configuration);
    if (path.endsWith('/history')) return Promise.resolve([]);
    expect(method).toBe('POST'); expect(body).toEqual({ operationId: answer.operationId, question: 'Show salary', configurationRevision: configuration.revision, allowModelProcessing: true });
    return Promise.resolve(answer);
  });
  mount(api); await screen.findByText(configuration.notice); await user.type(screen.getByLabelText('Your question'), 'Show salary');
  expect(screen.getByRole('button', { name: 'Ask question' })).toBeDisabled();
  await user.click(screen.getByRole('checkbox')); await user.click(screen.getByRole('button', { name: 'Ask question' }));
  expect(await screen.findByText('Salary source 100 USD')).toBeVisible();
  expect(screen.getByRole('link', { name: 'Open source document version' })).toHaveAttribute('href', '/evidence?version=version-2');
  expect(screen.getByRole('link', { name: /View source & history/ })).toHaveAttribute('href', '/finances?fact=fact-1');
  expect(screen.getByRole('checkbox')).not.toBeChecked(); expect(request.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(1);
});
it('makes no owner requests without all required permissions', () => {
  const api = new AccessApi(session); const request = vi.spyOn(api, 'product'); mount(api, ['search.query.execute']);
  expect(screen.getByText('Q&A access is not enabled')).toBeVisible(); expect(request).not.toHaveBeenCalled();
});
it('keeps provider setup explicit without enabling a submit', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.endsWith('/configuration') ? { ...configuration, available: false } : []));
  mount(api); expect(await screen.findByText('Provider not configured')).toBeVisible(); expect(screen.getByRole('button', { name: 'Ask question' })).toBeDisabled();
});
it('renders insufficient-data history without fabricated facts', async () => {
  const api = new AccessApi(session); vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.endsWith('/configuration') ? configuration : [{ ...answer, state: 'INSUFFICIENT_DATA', introduction: 'Insufficient source information.', claims: [] }]));
  mount(api); expect(await screen.findByText('Insufficient source information.')).toBeVisible(); expect(screen.queryByText('Salary source 100 USD')).not.toBeInTheDocument();
});

it('requires an explicit document version and submits it with renewed permission', async () => {
  const api = new AccessApi(session); const user = userEvent.setup();
  vi.spyOn(crypto, 'randomUUID').mockReturnValue('cccccccc-cccc-cccc-cccc-cccccccccccc');
  const request = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (path.endsWith('/configuration')) return Promise.resolve(configuration);
    if (path.endsWith('/history')) return Promise.resolve([]);
    if (method === 'GET') return Promise.resolve({ items: [{ documentVersionId: 'version-2', originalFileName: 'statement.txt', version: 2 }] });
    expect(body).toEqual({ operationId: answer.operationId, question: 'Show salary', configurationRevision: configuration.revision, allowModelProcessing: true, documentVersionId: 'version-2' });
    return Promise.resolve({ ...answer, claims: [{ ...answer.claims[0], kind: 'DocumentExcerpt', sourceLocation: 'lines 1-2' }] });
  });
  mount(api); await screen.findByText(configuration.notice);
  await user.type(screen.getByLabelText('Your question'), 'Show salary'); await user.click(screen.getByRole('checkbox'));
  await user.click(screen.getByRole('combobox', { name: 'Answer source' })); await user.click(screen.getByText('Selected document'));
  await waitFor(() => { expect(screen.getByRole('combobox', { name: 'Selected source' })).toBeEnabled(); });
  expect(screen.getByRole('checkbox')).not.toBeChecked();
  expect(screen.getByRole('button', { name: 'Ask question' })).toBeDisabled();
  await user.click(screen.getByRole('combobox', { name: 'Selected source' })); await user.click(await screen.findByText('statement.txt · version 2'));
  await user.click(screen.getByRole('checkbox')); await user.click(screen.getByRole('button', { name: 'Ask question' }));
  expect(await screen.findByText('Source: lines 1-2')).toBeVisible();
  expect(screen.getByRole('link', { name: 'Open source document version' })).toHaveAttribute('href', '/evidence?version=version-2');
  expect(request.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(1);
});
it('links calculation history to the retained calculation instead of a new calculation', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation(path => Promise.resolve(path.endsWith('/configuration') ? configuration : [{ ...answer, claims: [{ ...answer.claims[0], kind: 'Calculation', calculationId: 'networth-1', documentVersionId: '', sourceLocation: 'Reviewed position', ruleVersion: '1.0.0' }] }]));
  mount(api); expect(await screen.findByRole('link', { name: 'Open exact calculation' })).toHaveAttribute('href', '/net-worth?calculation=networth-1');
  expect(screen.queryByRole('link', { name: 'Open source document version' })).not.toBeInTheDocument();
});

const summaryPosition = { positionId: 'position-1', operationId: 'a'.repeat(32), customerId: 'reference-customer', position: { asOfDate: '2026-09-30', currency: 'USD', lines: [], assetsComplete: true, liabilitiesComplete: true, replacesPositionId: null }, lines: [], recordedBy: 'A100', recordedAt: '2026-10-20T00:00:00Z' };
const summaryNet = { calculationId: 'net-result', operationId: 'b'.repeat(32), customerId: 'reference-customer', position: summaryPosition, ruleId: 'monergy.net-worth', ruleVersion: '1.0.0', definitionHash: 'a'.repeat(64), numericSemantics: 'Exact totals', state: 'CALCULATED', totalAssets: '0.00', totalLiabilities: '0.00', netWorth: '0.00', issues: [], recordedBy: 'A100', recordedAt: '2026-10-20T00:00:00Z' };
const summaryCash = { ...summaryNet, calculationId: 'cash-result', ruleId: 'monergy.monthly-cashflow', totalIncome: '0.00', totalExpenses: '0.00', balance: '0.00', position: { ...summaryPosition, position: { asOfDate: '2026-09-30', currency: 'USD', lines: [], incomeComplete: true, expensesComplete: true, replacesPositionId: null, household: 'Self' } } };
it.each(['valid', 'omitted', 'swapped'])('creates a selected summary and validates %s citations', async mode => {
 const api = new AccessApi(session); const user = userEvent.setup(); window.history.replaceState({}, '', '/financial-qa?mode=summary'); vi.spyOn(crypto, 'randomUUID').mockReturnValue('cccccccc-cccc-cccc-cccc-cccccccccccc');
 const claims = Array.from({ length: mode === 'omitted' ? 5 : 6 }, (_, i) => ({ ...answer.claims[0], id: 'summary-' + String(i), kind: i < 3 ? 'Calculation' : 'MonthlyCalculation', text: 'Reviewed result ' + String(i), documentVersionId: '', calculationId: mode === 'swapped' ? 'foreign-result' : i < 3 ? 'net-result' : 'cash-result', ruleVersion: '1.0.0' }));
 const request = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
  if (path.endsWith('/configuration')) return Promise.resolve(configuration); if (path.endsWith('/history')) return Promise.resolve([]);
  if (path === 'v1/net-worth/calculations') return Promise.resolve({ items: [summaryNet], hasMore: false, nextBeforeId: null });
  if (path === 'v1/monthly-cashflow/calculations') return Promise.resolve({ items: [summaryCash], hasMore: false, nextBeforeId: null });
  expect(method).toBe('POST'); const input = body as { question: string; summaryNetWorthCalculationId: string; summaryCashflowCalculationId: string }; expect(input.summaryNetWorthCalculationId).toBe('net-result'); expect(input.summaryCashflowCalculationId).toBe('cash-result'); return Promise.resolve({ ...answer, question: input.question, claims });
 });
 mount(api, ['ai-intelligence.analysis.execute', 'search.query.execute', 'financial-profile.profile.read', 'evidence.document.read', 'financial-rules.calculation.execute']); await screen.findByText(configuration.notice); await waitFor(() => { expect(screen.getByRole('combobox', { name: 'Summary net-worth result' })).toBeEnabled(); });
 expect(screen.queryByLabelText('Your question')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Create financial summary' })).toBeDisabled();
 await user.click(screen.getByRole('combobox', { name: 'Summary net-worth result' })); await user.click(await screen.findByText('2026-09-30 · USD · saved 2026-10-20T00:00:00Z'));
 await user.click(screen.getByRole('checkbox')); await user.click(screen.getByRole('combobox', { name: 'Summary monthly result' })); await user.click(await screen.findByText('2026-09-30 · Self · USD · saved 2026-10-20T00:00:00Z')); expect(screen.getByRole('checkbox')).not.toBeChecked(); await user.click(screen.getByRole('checkbox')); await user.click(screen.getByRole('button', { name: 'Create financial summary' }));
 if (mode === 'valid') { expect((await screen.findAllByRole('link', { name: 'Open exact monthly calculation' }))[0]).toHaveAttribute('href', '/monthly-cashflow?calculation=cash-result'); expect(screen.getAllByRole('link', { name: 'Open exact calculation' })[0]).toHaveAttribute('href', '/net-worth?calculation=net-result'); expect(screen.queryByRole('link', { name: 'Open source document version' })).not.toBeInTheDocument(); } else { await screen.findByText(/No answer could be confirmed/); expect(screen.queryByText('Reviewed result 0')).not.toBeInTheDocument(); }
 expect(request.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(1);
});
it.each([false, true])('loads older summary reviews and rejects invalid owner pages (%s)', async invalid => {
 const api = new AccessApi(session); const user = userEvent.setup(); window.history.replaceState({}, '', '/financial-qa?mode=summary');
 const many = Array.from({ length: 50 }, (_, i) => ({ ...summaryNet, calculationId: 'net-' + String(i), recordedAt: '2026-10-19T00:00:' + String(i).padStart(2, '0') + 'Z' }));
 const request = vi.spyOn(api, 'product').mockImplementation(path => {
  if (path.endsWith('/configuration')) return Promise.resolve(configuration); if (path.endsWith('/history')) return Promise.resolve([]);
  if (path === 'v1/monthly-cashflow/calculations') return Promise.resolve({ items: [summaryCash], hasMore: false, nextBeforeId: null });
  if (path.includes('beforeId=net-49')) return Promise.resolve({ items: [{ ...summaryNet, customerId: invalid ? 'foreign' : summaryNet.customerId }], hasMore: false, nextBeforeId: null });
  return Promise.resolve({ items: many, hasMore: true, nextBeforeId: 'net-49' });
 });
 mount(api, ['ai-intelligence.analysis.execute', 'search.query.execute', 'financial-profile.profile.read', 'evidence.document.read', 'financial-rules.calculation.execute']);
 await screen.findByRole('button', { name: 'Load older net-worth results' }); await user.click(screen.getByRole('combobox', { name: 'Summary monthly result' })); await user.click(await screen.findByText('2026-09-30 · Self · USD · saved 2026-10-20T00:00:00Z')); await user.click(screen.getByRole('button', { name: 'Load older net-worth results' }));
 if (invalid) { await screen.findByText(/Saved summary reviews could not be loaded/); expect(screen.getByRole('combobox', { name: 'Summary net-worth result' })).toBeDisabled(); }
 else { await waitFor(() => { expect(screen.getByRole('combobox', { name: 'Summary net-worth result' })).toBeEnabled(); }); await user.type(screen.getByRole('combobox', { name: 'Summary net-worth result' }), '2026-10-20'); await user.click(await screen.findByText('2026-09-30 · USD · saved 2026-10-20T00:00:00Z')); await user.click(screen.getByRole('checkbox')); expect(screen.getByRole('button', { name: 'Create financial summary' })).toBeEnabled(); }
 expect(request.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(0);
});
