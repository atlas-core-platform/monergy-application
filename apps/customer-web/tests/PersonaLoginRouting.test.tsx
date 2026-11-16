import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { WorkspaceEntry } from '../src/access/WorkspaceEntry';
import { WorkspaceSessionProvider } from '../src/access/WorkspaceSessionProvider';
import { workspaceAreaForPath } from '../src/App';

const session = (actorId = 'A300') => ({
  tenantId: 'T001', actorId, subjectVersion: 3,
  authenticationContextId: 'activated-persona-session',
  expiresAt: new Date(Date.now() + 1800000).toISOString(),
});
const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const mount = () => render(<FoundationProvider appearance="midnight">
  <WorkspaceSessionProvider area="advisor">
    <WorkspaceEntry area="advisor"><h2>Protected advisor workspace</h2></WorkspaceEntry>
  </WorkspaceSessionProvider>
</FoundationProvider>);
const mountOperationsSupport = () => render(<FoundationProvider appearance="midnight">
  <WorkspaceSessionProvider area="operations-support">
    <WorkspaceEntry area="operations-support"><h2>Protected recovery workspace</h2></WorkspaceEntry>
  </WorkspaceSessionProvider>
</FoundationProvider>);
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it.each([
  ['/access', 'administration'],
  ['/access/users', 'administration'],
  ['/advisor', 'advisor'],
  ['/dsa', 'dsa'],
  ['/lender', 'lender'],
  ['/financial-professional', 'financial-professional'],
  ['/property-valuer', 'property-valuer'],
  ['/legal-advisor', 'legal-advisor'],
  ['/operations-support', 'operations-support'],
  ['/kyc/reviews', 'kyc-review'],
  ['/', 'product'],
  ['/evidence', 'product'],
])('maps %s to the exact authorization area %s', (path, area) => {
  expect(workspaceAreaForPath(path)).toBe(area);
});

it('prefers activated email/password when enabled and verifies current Advisor authority', async () => {
  const request = vi.fn((url: string) => {
    if (url.endsWith('/persona-credentials')) return Promise.resolve(json({ enabled: true }));
    if (url.endsWith('/persona-credentials/sign-in')) return Promise.resolve(json(session()));
    if (url.endsWith('/advisor/context')) return Promise.resolve(json({
      tenantId: 'T001', actorId: 'A300', authority: 'FinancialAdvisor',
      policyVersion: 4, subjectVersion: 3, recordedRelationshipCount: 0,
    }));
    return Promise.resolve(json({}, 404));
  });
  vi.stubGlobal('fetch', request);
  mount();
  const user = userEvent.setup();
  fireEvent.change(await screen.findByRole('textbox', { name: 'Email address' }),
    { target: { value: 'a300@example.test' } });
  await user.type(screen.getByLabelText('Password'), 'a long password value');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  expect(await screen.findByText('Protected advisor workspace')).toBeVisible();
  expect(screen.queryByLabelText('Local access key')).not.toBeInTheDocument();
  expect(request.mock.calls.some(([url]) => url.endsWith('/advisor/context'))).toBe(true);
});

it('does not grant an Advisor workspace when the verified identity has the wrong role', async () => {
  const request = vi.fn((url: string) => {
    if (url.endsWith('/persona-credentials')) return Promise.resolve(json({ enabled: true }));
    if (url.endsWith('/persona-credentials/sign-in')) return Promise.resolve(json(session('A100')));
    if (url.endsWith('/advisor/context'))
      return Promise.resolve(json({ error: 'ADVISOR_ROLE_REQUIRED' }, 403));
    if (url.endsWith('/revoke')) return Promise.resolve(json({ revoked: true }));
    return Promise.resolve(json({}, 404));
  });
  vi.stubGlobal('fetch', request);
  mount();
  const user = userEvent.setup();
  fireEvent.change(await screen.findByRole('textbox', { name: 'Email address' }),
    { target: { value: 'a100@example.test' } });
  await user.type(screen.getByLabelText('Password'), 'a long password value');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  expect(await screen.findByText('An active Financial Advisor / Relationship Manager role is required.')).toBeVisible();
  expect(screen.queryByText('Protected advisor workspace')).not.toBeInTheDocument();
});

it('retains local testing only when credential activation is disabled', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ enabled: false })));
  mount();
  expect(await screen.findByLabelText('Local access key')).toBeVisible();
  expect(screen.queryByRole('textbox', { name: 'Email address' })).not.toBeInTheDocument();
});

it('verifies the exact Operations Support authority before opening recovery', async () => {
  const request = vi.fn((url: string) => {
    if (url.endsWith('/persona-credentials')) return Promise.resolve(json({ enabled: true }));
    if (url.endsWith('/persona-credentials/sign-in')) return Promise.resolve(json(session('A850')));
    if (url.endsWith('/operations/context')) return Promise.resolve(json({
      tenantId: 'T001', actorId: 'A850', authority: 'OperationsSupport',
      policyVersion: 12, subjectVersion: 3,
    }));
    return Promise.resolve(json({}, 404));
  });
  vi.stubGlobal('fetch', request);
  mountOperationsSupport();
  const user = userEvent.setup();
  fireEvent.change(await screen.findByRole('textbox', { name: 'Email address' }),
    { target: { value: 'a850@example.test' } });
  await user.type(screen.getByLabelText('Password'), 'a long password value');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  expect(await screen.findByText('Protected recovery workspace')).toBeVisible();
  expect(request.mock.calls.some(([url]) => url.endsWith('/operations/context'))).toBe(true);
});

it('fails closed when the recovery owner does not confirm Operations Support', async () => {
  const request = vi.fn((url: string) => {
    if (url.endsWith('/persona-credentials')) return Promise.resolve(json({ enabled: true }));
    if (url.endsWith('/persona-credentials/sign-in')) return Promise.resolve(json(session('A100')));
    if (url.endsWith('/operations/context'))
      return Promise.resolve(json({ error: 'OPERATIONS_SUPPORT_REQUIRED' }, 403));
    if (url.endsWith('/revoke')) return Promise.resolve(json({ revoked: true }));
    return Promise.resolve(json({}, 404));
  });
  vi.stubGlobal('fetch', request);
  mountOperationsSupport();
  const user = userEvent.setup();
  fireEvent.change(await screen.findByRole('textbox', { name: 'Email address' }),
    { target: { value: 'a100@example.test' } });
  await user.type(screen.getByLabelText('Password'), 'a long password value');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  expect(await screen.findByText('An active Operations Support recovery permission is required.')).toBeVisible();
  expect(screen.queryByText('Protected recovery workspace')).not.toBeInTheDocument();
});
