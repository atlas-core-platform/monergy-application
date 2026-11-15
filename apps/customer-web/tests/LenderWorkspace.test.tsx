import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import LenderWorkspace from '../src/lender/LenderWorkspace';

const session = { tenantId: 'T001', actorId: 'A500', authenticationContextId: 'lender-session',
  subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const documents = [
  { code: 'identity', label: 'Identity document', present: true, reviewed: false, reviewedAt: null },
  { code: 'income', label: 'Income evidence', present: true, reviewed: false, reviewedAt: null },
  { code: 'bank-statements', label: 'Bank statements', present: true, reviewed: false, reviewedAt: null },
];
const application = (overrides: Record<string, unknown> = {}) => ({
  applicationId: 'loan-001', applicationReference: 'Lending application 001', state: 'Assigned',
  version: 1, assignedAt: '2026-11-14T10:00:00Z', requiredDocuments: documents,
  decision: null, dataDurability: 'Durable test data', ...overrides,
});
function state(api: AccessApi): WorkspaceSessionState {
  return { status: 'authorized', session, api,
    authorization: { area: 'lender', authority: 'LenderProfessional', policyVersion: 8, subjectVersion: 2 },
    message: '', recovery: null, connect: vi.fn().mockResolvedValue(undefined),
    retry: vi.fn().mockResolvedValue(undefined), signOut: vi.fn().mockResolvedValue(undefined), cancel: vi.fn() };
}
function mount(api: AccessApi) {
  return render(<FoundationProvider appearance="midnight"><Context value={state(api)}>
    <LenderWorkspace />
  </Context></FoundationProvider>);
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('shows only assigned application references and no customer or underwriting claims', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T001', actorId: 'A500', items: [application()] });
  mount(api);
  expect((await screen.findAllByText('Lending application 001')).length).toBeGreaterThan(0);
  expect(screen.getByText(/does not determine eligibility, pricing or creditworthiness/)).toBeVisible();
  expect(screen.queryByText(/customer id|C001|net worth|credit score/i)).not.toBeInTheDocument();
});

it('reviews the exact assigned document without posting tenant actor or permission data', async () => {
  vi.stubGlobal('crypto', { getRandomValues: (values: Uint8Array) => { values.fill(10); return values; } });
  const api = new AccessApi(session); let write: unknown; let current = application();
  vi.spyOn(api, 'case').mockImplementation((_path, method = 'GET', body) => {
    if (method === 'POST') {
      write = body;
      current = application({ state: 'Under review', version: 2,
        requiredDocuments: [{ ...documents[0], reviewed: true, reviewedAt: '2026-11-15T10:00:00Z' }, ...documents.slice(1)] });
      return Promise.resolve(current);
    }
    return Promise.resolve({ tenantId: 'T001', actorId: 'A500', items: [current] });
  });
  mount(api);
  const review = (await screen.findAllByRole('button', { name: 'Mark reviewed' }))[0];
  if (!review) throw new Error('Missing review action');
  fireEvent.click(review);
  expect(await screen.findByText(/Identity document was marked reviewed/)).toBeVisible();
  expect(write).toEqual({ operationId: '0a'.repeat(16), expectedVersion: 1 });
  expect(JSON.stringify(write)).not.toMatch(/tenant|actor|permission|capability/i);
});

it('records a lender-owned decision only after every required document is reviewed', async () => {
  vi.stubGlobal('crypto', { getRandomValues: (values: Uint8Array) => { values.fill(11); return values; } });
  const api = new AccessApi(session); let write: unknown;
  const reviewed = documents.map(document => ({ ...document, reviewed: true, reviewedAt: '2026-11-15T10:00:00Z' }));
  vi.spyOn(api, 'case').mockImplementation((_path, method = 'GET', body) => {
    if (method === 'POST') {
      write = body;
      return Promise.resolve(application({ state: 'Decided', version: 5, requiredDocuments: reviewed,
        decision: { decision: 'Approved', reason: "Recorded under the lender's policy.", decidedAt: '2026-11-15T10:01:00Z' } }));
    }
    return Promise.resolve({ tenantId: 'T001', actorId: 'A500', items: [application({
      state: 'Under review', version: 4, requiredDocuments: reviewed })] });
  });
  mount(api);
  fireEvent.click(await screen.findByRole('radio', { name: 'Approved' }));
  fireEvent.change(screen.getByLabelText('Decision reason'), { target: { value: "Recorded under the lender's policy." } });
  fireEvent.click(screen.getByRole('button', { name: 'Record decision' }));
  expect(await screen.findByText(/Recorded decision: Approved/)).toBeVisible();
  expect(write).toEqual({ operationId: '0b'.repeat(16), expectedVersion: 4,
    decision: 'Approved', reason: "Recorded under the lender's policy." });
});

it('fails closed on tenant or actor-mismatched list data', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T002', actorId: 'A501', items: [application()] });
  mount(api);
  await waitFor(() => { expect(screen.getByRole('alert')).toBeVisible(); });
  expect(screen.queryByText('Lending application 001')).not.toBeInTheDocument();
});
