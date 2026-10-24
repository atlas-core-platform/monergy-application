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
  const fetch = vi.fn((url: string) => {
    if (url.endsWith('/tenant-sessions')) return Promise.resolve(json(session()));
    if (url.endsWith('/v1/advisor/customers')) return Promise.resolve(json({
      tenantId: 'T001', actorId: 'A300', policyVersion: 4,
      subjectVersion: 3, customers: [],
    }));
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
it('shows a read-only reviewed financial fact only after a consent-authorized customer selection', async () => {
  const requests = owner();
  const mock = requests.getMockImplementation();
  if (!mock) throw Error('Missing owner mock');
  requests.mockImplementation((url: string) => {
    if (url.endsWith('/v1/advisor/customers')) return Promise.resolve(json({
      tenantId: 'T001', actorId: 'A300', policyVersion: 4, subjectVersion: 3,
      customers: [{ customerId: 'C001', displayName: 'Verified Customer' }],
    }));
    if (url.endsWith('/advisor/customers/C001/financial-profile'))
      return Promise.resolve(json({
        revision: 1,
        facts: [{ financialFactId: 'f1', factType: 'INCOME', label: 'Reviewed income',
          value: 50000, currency: 'INR', effectiveDate: '2026-10-01',
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
    if (url.endsWith('/v1/advisor/customers')) return Promise.resolve(json({
      tenantId: 'T001', actorId: 'A300', policyVersion: 4, subjectVersion: 3, customers: [{ customerId: 'C001', displayName: 'Verified Customer' }],
    }));
    if (url.endsWith('/advisor/customers/C001/financial-profile'))
      return Promise.resolve(json({ error: 'ADVISOR_CUSTOMER_ACCESS_DENIED' }, 403));
    return fallback(url);
  });
  mount(); await connect();
  await userEvent.click(await screen.findByRole('button', { name: 'Review information' }));
  await waitFor(() => expect(requests.mock.calls.some(([url]) => url.endsWith('/advisor/customers/C001/financial-profile'))).toBe(true));
  expect(screen.queryByText('Financial information (read only)')).not.toBeInTheDocument();
  expect(screen.queryByText('Reviewed income')).not.toBeInTheDocument();
});
it('never displays an invalid owner customer label', async () => {
  const requests = owner();
  const fallback = requests.getMockImplementation();
  if (!fallback) throw Error('Missing mock');
  requests.mockImplementation((url: string) => url.endsWith('/v1/advisor/customers')
    ? Promise.resolve(json({ tenantId: 'T001', actorId: 'A300', policyVersion: 4, subjectVersion: 3,
      customers: [{ customerId: ' invalid', displayName: 'Should not be visible' }] }))
    : fallback(url));
  mount(); await connect();
  expect(await screen.findByText(/Authorized customer names could not be verified/)).toBeVisible();
  expect(screen.queryByText('Should not be visible')).not.toBeInTheDocument();
});
it('rejects a non-advisor without rendering a workspace', async () => {
  const requests = owner(403); mount(); await connect();
  await waitFor(() => expect(requests.mock.calls.some(([url]) => url.endsWith('/revoke'))).toBe(true));
  expect(screen.queryByRole('heading', { name: 'Financial Advisor workspace' })).not.toBeInTheDocument();
});
it('rejects a mismatched owner response before revealing the workspace', async () => {
  const requests = owner(200, true); mount(); await connect();
  await waitFor(() => expect(requests.mock.calls.some(([url]) => url.endsWith('/revoke'))).toBe(true));
  expect(screen.queryByRole('heading', { name: 'Financial Advisor workspace' })).not.toBeInTheDocument();
  expect(sessionMemory.current).toBeNull();
});
