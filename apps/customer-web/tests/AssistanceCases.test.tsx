import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import CustomerAssistanceCases from '../src/cases/CustomerAssistanceCases';
import DsaCaseWorkspace from '../src/cases/DsaCaseWorkspace';

const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'session-one',
  subjectVersion: 3, expiresAt: '2027-01-01T00:00:00Z' };
const submitted = { caseId: 'case-0123456789abcdef01234567', requestType: 'Financial assistance',
  state: 'Submitted', version: 1, createdAt: '2026-10-31T10:00:00Z', acceptedAt: null,
  assignedToMe: false, dataDurability: 'Temporary data' } as const;
const accepted = { ...submitted, state: 'Accepted', version: 2,
  acceptedAt: '2026-10-31T10:01:00Z', assignedToMe: true } as const;

function state(api: AccessApi, area: 'product' | 'dsa'): WorkspaceSessionState {
  return { status: 'authorized', session, api, authorization: area === 'product'
    ? { area: 'product', context: { tenantId: 'T001', actorId: 'A100', customerId: 'C001',
        customerName: 'Customer', capabilities: [], environment: 'LOCAL',
        dataDurability: 'Temporary data', productionAccepted: false } }
    : { area: 'dsa', authority: 'DsaCaseManager', policyVersion: 2, subjectVersion: 3 },
  message: '', recovery: null, connect: vi.fn().mockResolvedValue(undefined),
  retry: vi.fn().mockResolvedValue(undefined), signOut: vi.fn().mockResolvedValue(undefined), cancel: vi.fn() };
}
function mount(component: ReactNode, value: WorkspaceSessionState) {
  return render(<FoundationProvider appearance="midnight"><Context value={value}>{component}</Context></FoundationProvider>);
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('submits one actor-bound customer case and prevents a duplicate active request', async () => {
  vi.stubGlobal('crypto', { randomUUID: () => 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' });
  const api = new AccessApi(session); const writes: unknown[] = [];
  vi.spyOn(api, 'case').mockImplementation((_path, method = 'GET', body) => {
    if (method === 'POST') { writes.push(body); return Promise.resolve(submitted); }
    return Promise.resolve({ tenantId: 'T001', actorId: 'A100', items: [] });
  });
  mount(<CustomerAssistanceCases />, state(api, 'product'));
  fireEvent.click(await screen.findByRole('button', { name: 'Request assistance' }));
  expect(await screen.findByText(/submitted to the DSA/)).toBeVisible();
  expect(writes).toEqual([{ operationId: 'a'.repeat(32) }]);
  expect(screen.getByRole('button', { name: 'Request assistance' })).toBeDisabled();
  expect(screen.getByText('Submitted')).toBeVisible();
  expect(screen.queryByText(/A100|C001|customer name|net worth/i)).not.toBeInTheDocument();
});

it('shows the append-only lifecycle history returned for the current customer case', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockImplementation(path => Promise.resolve(path.endsWith('/history')
    ? { caseId: submitted.caseId, dataDurability: 'Temporary data', events: [
        { eventId: 'a'.repeat(32), action: 'Submitted', version: 1, occurredAt: submitted.createdAt },
        { eventId: 'b'.repeat(32), action: 'Accepted', version: 2, occurredAt: accepted.acceptedAt },
      ] }
    : { tenantId: 'T001', actorId: 'A100', items: [{ ...accepted, assignedToMe: false }] }));
  mount(<CustomerAssistanceCases />, state(api, 'product'));
  fireEvent.click(await screen.findByRole('button', { name: 'View history' }));
  const dialog = await screen.findByRole('dialog');
  expect(dialog).toHaveTextContent('Case history');
  expect(dialog).toHaveTextContent('Submitted');
  expect(dialog).toHaveTextContent('Accepted');
});

it('lets an eligible DSA accept exactly the visible version without exposing customer data', async () => {
  vi.stubGlobal('crypto', { randomUUID: () => 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' });
  const dsaSession = { ...session, actorId: 'D300' }; const api = new AccessApi(dsaSession); let write: unknown;
  vi.spyOn(api, 'case').mockImplementation((_path, method = 'GET', body) => {
    if (method === 'POST') { write = body; return Promise.resolve(accepted); }
    return Promise.resolve({ tenantId: 'T001', actorId: 'D300', items: [submitted] });
  });
  mount(<DsaCaseWorkspace />, { ...state(api, 'dsa'), session: dsaSession });
  fireEvent.click(await screen.findByRole('button', { name: 'Accept case' }));
  expect(await screen.findByText(/now assigned to you/)).toBeVisible();
  expect(write).toEqual({ operationId: 'b'.repeat(32), expectedVersion: 1 });
  expect(screen.getByText('Assigned to you')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Accept case' })).not.toBeInTheDocument();
  expect(screen.queryByText(/A100|C001|customer name|net worth/i)).not.toBeInTheDocument();
});

it('fails closed on tenant-mismatched queue data', async () => {
  const dsaSession = { ...session, actorId: 'D300' }; const api = new AccessApi(dsaSession);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T002', actorId: 'D300', items: [submitted] });
  mount(<DsaCaseWorkspace />, { ...state(api, 'dsa'), session: dsaSession });
  await waitFor(() => { expect(screen.getByRole('alert')).toBeVisible(); });
  expect(screen.queryByText(submitted.caseId)).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Accept case' })).not.toBeInTheDocument();
});
