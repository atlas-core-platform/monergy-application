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
  expect(screen.getByText('0')).toBeVisible();
  expect(screen.getByText(/relationship is not permission/)).toBeVisible();
  expect(requests.mock.calls.some(([url]) => url.endsWith('/advisor/context'))).toBe(true);
  expect(requests.mock.calls.some(([url]) => url.endsWith('/v1/context'))).toBe(false);
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
