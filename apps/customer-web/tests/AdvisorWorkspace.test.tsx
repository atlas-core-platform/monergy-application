import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { WorkspaceEntry } from '../src/access/WorkspaceEntry';
import { WorkspaceSessionProvider } from '../src/access/WorkspaceSessionProvider';
import AdvisorWorkspace from '../src/advisor/AdvisorWorkspace';
import { sessionMemory } from '../src/access/workspaceSession';

const json = (data: unknown, status = 200) => new Response(JSON.stringify(data),
  { status, headers: { 'Content-Type': 'application/json' } });
const session = () => ({
  tenantId: 'T001', actorId: 'A300', subjectVersion: 3,
  authenticationContextId: 'advisor-session',
  expiresAt: new Date(Date.now() + 1800000).toISOString(),
});
const advisor = () => ({
  tenantId: 'T001', actorId: 'A300', authority: 'FinancialAdvisor',
  policyVersion: 4, subjectVersion: 3, recordedRelationshipCount: 0,
});
const access = (overrides: Record<string, unknown> = {}) => ({
  requestId: null, target: 'financial-advisor.assigned-customer-financial-review',
  targetLabel: 'Assigned customer financial review',
  grantExplanation: 'Allows this Financial Advisor to read assigned customer financial information with current consent.',
  state: 'NotRequested',
  hasAccess: false, canRequest: true, createdAt: null, decisionAt: null,
  fulfilledAt: null, decisionReason: null, ...overrides,
});
const accessPage = (overrides: Record<string, unknown> = {}) => ({
  tenantId: 'T001', actorId: 'A300', policyVersion: 4, subjectVersion: 3,
  items: [access(overrides)],
});
function mount() {
  return render(
    <FoundationProvider appearance="midnight">
      <WorkspaceSessionProvider area="advisor">
        <WorkspaceEntry area="advisor"><AdvisorWorkspace /></WorkspaceEntry>
      </WorkspaceSessionProvider>
    </FoundationProvider>,
  );
}
function owner(contextStatus = 200, foreignActor = false) {
  const fetch = vi.fn((url: string, options?: RequestInit) => {
    if (options?.signal?.aborted) return Promise.reject(new Error('aborted'));
    if (url.endsWith('/tenant-sessions')) return Promise.resolve(json(session()));
    if (url.endsWith('/v1/advisor/customers')) return Promise.resolve(json({
      tenantId: 'T001', actorId: 'A300', policyVersion: 4,
      subjectVersion: 3, customers: [],
    }));
    if (url.endsWith('/access-requests')) return Promise.resolve(json(accessPage()));
    if (url.endsWith('/advisor/context')) return Promise.resolve(json(
      contextStatus === 200 ? { ...advisor(), ...(foreignActor ? { actorId: 'A100' } : {}) }
        : { error: 'ADVISOR_ROLE_REQUIRED' }, contextStatus));
    if (url.endsWith('/revoke')) return Promise.resolve(json(true));
    return Promise.resolve(json({}, 404));
  });
  vi.stubGlobal('fetch', fetch);
  return fetch;
}
async function connect() {
  fireEvent.change(await screen.findByLabelText('Local access key'), { target: { value: 'local-secret' } });
  await userEvent.click(screen.getByRole('button', { name: 'Connect workspace' }));
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
it('enters only the verified Financial Advisor workspace, without displaying customer financial data', async () => {
  const requests = owner(); mount(); await connect();
  expect(await screen.findByRole('heading', { name: 'Financial Advisor workspace' })).toBeVisible();
  expect(screen.getAllByText('0').length).toBeGreaterThan(0);
  expect(screen.getByText(/relationship does not grant financial access/)).toBeVisible();
  expect(requests.mock.calls.some(([url]) => url.endsWith('/advisor/context'))).toBe(true);
  expect(requests.mock.calls.some(([url]) => url.endsWith('/v1/context'))).toBe(false);
});
it('does not expose an unconsented financial profile when no customers are authorized', async () => {
  const requests = owner(); mount(); await connect();
  expect(await screen.findByText(/No customer reviews are authorized yet/)).toBeVisible();
  expect(requests.mock.calls.some(([url]) => url.includes('/financial-profile'))).toBe(false);
  expect(screen.queryByText('Financial information (read only)')).not.toBeInTheDocument();
});
it('submits only an idempotency operation and never browser-supplied authority fields', async () => {
  vi.stubGlobal('crypto', { randomUUID: () => 'dddddddd-dddd-dddd-dddd-dddddddddddd' });
  const requests = owner();
  const fallback = requests.getMockImplementation();
  if (!fallback) throw Error('Missing owner mock');
  requests.mockImplementation((url: string, options?: RequestInit) => {
    if (url.endsWith('/access-requests/financial-advisor.assigned-customer-financial-review/submit') && options?.method === 'POST') {
      expect(typeof options.body).toBe('string');
      expect(JSON.parse(options.body as string)).toEqual({ operationId: 'd'.repeat(32) });
      return Promise.resolve(json(access({ requestId: 'a'.repeat(32), state: 'Submitted',
        canRequest: false, createdAt: new Date().toISOString() })));
    }
    return fallback(url);
  });
  mount(); await connect();
  await userEvent.click(await screen.findByRole('button', { name: 'Request access' }));
  expect(await screen.findByText(/request was submitted to an Organization Administrator/)).toBeVisible();
  expect(screen.getByText(/Submitted · Under review/)).toBeVisible();
  expect(requests.mock.calls.filter(([url, options]) =>
    url.endsWith('/access-requests/financial-advisor.assigned-customer-financial-review/submit') &&
    options?.method === 'POST')).toHaveLength(1);
});
it('shows a rejected lifecycle safely and allows a new bounded request', async () => {
  const requests = owner();
  const fallback = requests.getMockImplementation();
  if (!fallback) throw Error('Missing owner mock');
  requests.mockImplementation((url: string, options?: RequestInit) =>
    url.endsWith('/access-requests') && options?.method !== 'POST'
      ? Promise.resolve(json(accessPage({ requestId: 'b'.repeat(32), state: 'Rejected',
          decisionAt: new Date().toISOString(), decisionReason: 'Not approved by your Organization Administrator' })))
      : fallback(url));
  mount(); await connect();
  expect(await screen.findByText('The request was not approved')).toBeVisible();
  expect(screen.getByText('Not approved by your Organization Administrator')).toBeVisible();
  expect(screen.getByRole('button', { name: 'Request access' })).toBeEnabled();
});
it('shows a read-only reviewed financial fact only after a consent-authorized customer selection', async () => {
  const requests = owner();
  const mock = requests.getMockImplementation();
  if (!mock) throw Error('Missing owner mock');
  requests.mockImplementation((url: string) => {
    if (url.endsWith('/access-requests')) return Promise.resolve(json(accessPage({
      requestId: 'c'.repeat(32), state: 'Fulfilled', hasAccess: true, canRequest: false,
      decisionAt: new Date().toISOString(), fulfilledAt: new Date().toISOString(),
    })));
    if (url.endsWith('/v1/advisor/customers')) return Promise.resolve(json({
      tenantId: 'T001', actorId: 'A300', policyVersion: 4, subjectVersion: 3,
      customers: [{ customerId: 'C001', displayName: 'Verified Customer' }],
    }));
    if (url.endsWith('/advisor/customers/C001/financial-profile'))
      return Promise.resolve(json({
        revision: 1,
        facts: [{ financialFactId: 'f1', factType: 'INCOME', label: 'Reviewed income',
          value: 50000, currency: 'INR', effectiveDate: '2026-10-01', revision: 1,
          source: { sourceName: 'Reviewed income document' } }],
      }));
    return mock(url);
  });
  mount(); await connect();
  await userEvent.click(await screen.findByRole('button', { name: 'Review information' }));
  expect(await screen.findByText('Reviewed income')).toBeVisible();
  expect(screen.getByText(/Source: Reviewed income document/)).toBeVisible();
  expect(screen.getAllByText('Verified Customer').length).toBeGreaterThan(0);
  expect(screen.queryByText('C001')).not.toBeInTheDocument();
});
it('fails closed when the owner revokes financial access after listing the assigned customer', async () => {
  const requests = owner();
  const fallback = requests.getMockImplementation();
  if (!fallback) throw Error('Missing owner mock');
  requests.mockImplementation((url: string) => {
    if (url.endsWith('/access-requests')) return Promise.resolve(json(accessPage({
      requestId: 'c'.repeat(32), state: 'Fulfilled', hasAccess: true, canRequest: false,
      decisionAt: new Date().toISOString(), fulfilledAt: new Date().toISOString(),
    })));
    if (url.endsWith('/v1/advisor/customers')) return Promise.resolve(json({
      tenantId: 'T001', actorId: 'A300', policyVersion: 4, subjectVersion: 3, customers: [{ customerId: 'C001', displayName: 'Verified Customer' }],
    }));
    if (url.endsWith('/advisor/customers/C001/financial-profile'))
      return Promise.resolve(json({ error: 'ADVISOR_CUSTOMER_ACCESS_DENIED' }, 403));
    return fallback(url);
  });
  mount(); await connect();
  await userEvent.click(await screen.findByRole('button', { name: 'Review information' }));
  await waitFor(() => { expect(requests.mock.calls.some(([url]) => url.endsWith('/advisor/customers/C001/financial-profile'))).toBe(true); });
  expect(screen.queryByText('Financial information (read only)')).not.toBeInTheDocument();
  expect(screen.queryByText('Reviewed income')).not.toBeInTheDocument();
});
it('never displays an invalid owner customer label', async () => {
  const requests = owner();
  const fallback = requests.getMockImplementation();
  if (!fallback) throw Error('Missing mock');
  requests.mockImplementation((url: string) => {
    if (url.endsWith('/access-requests')) return Promise.resolve(json(accessPage({
      requestId: 'c'.repeat(32), state: 'Fulfilled', hasAccess: true, canRequest: false,
      decisionAt: new Date().toISOString(), fulfilledAt: new Date().toISOString(),
    })));
    return url.endsWith('/v1/advisor/customers')
      ? Promise.resolve(json({ tenantId: 'T001', actorId: 'A300', policyVersion: 4, subjectVersion: 3,
        customers: [{ customerId: ' invalid', displayName: 'Should not be visible' }] }))
      : fallback(url);
  });
  mount(); await connect();
  expect(await screen.findByText(/Authorized customer names could not be verified/)).toBeVisible();
  expect(screen.queryByText('Should not be visible')).not.toBeInTheDocument();
});
it('rejects a non-advisor without rendering a workspace', async () => {
  const requests = owner(403); mount(); await connect();
  await waitFor(() => { expect(requests.mock.calls.some(([url]) => url.endsWith('/revoke'))).toBe(true); });
  expect(screen.queryByRole('heading', { name: 'Financial Advisor workspace' })).not.toBeInTheDocument();
});
it('rejects a mismatched owner response before revealing the workspace', async () => {
  const requests = owner(200, true); mount(); await connect();
  await waitFor(() => { expect(requests.mock.calls.some(([url]) => url.endsWith('/revoke'))).toBe(true); });
  expect(screen.queryByRole('heading', { name: 'Financial Advisor workspace' })).not.toBeInTheDocument();
  expect(sessionMemory.current).toBeNull();
});
