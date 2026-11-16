import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import LegalAdvisorWorkspace from '../src/legal-advisor/LegalAdvisorWorkspace';

const session = { tenantId: 'T001', actorId: 'A800', authenticationContextId: 'legal-session',
  subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const evidence = [{ code: 'estate-document', label: 'Assigned estate document', owner: 'Evidence' as const,
  reference: 'evidence-source-001', assignedVersion: 1, currentVersion: 1, inspected: false, inspectedAt: null }];
const review = (overrides: Record<string, unknown> = {}) => ({
  reviewId: 'review-001', matterReference: 'matter-estate', matterLabel: 'Family estate documents',
  state: 'Assigned', version: 1, assignedAt: '2026-11-15T10:00:00Z', evidence, revisions: [],
  dataDurability: 'Durable test data', ...overrides,
});
function state(api: AccessApi): WorkspaceSessionState {
  return { status: 'authorized', session, api,
    authorization: { area: 'legal-advisor', authority: 'LegalAdvisor', policyVersion: 10, subjectVersion: 2 },
    message: '', recovery: null, connect: vi.fn().mockResolvedValue(undefined),
    retry: vi.fn().mockResolvedValue(undefined), signOut: vi.fn().mockResolvedValue(undefined), cancel: vi.fn() };
}
function mount(api: AccessApi) {
  return render(<FoundationProvider appearance="midnight"><Context value={state(api)}>
    <LegalAdvisorWorkspace />
  </Context></FoundationProvider>);
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('shows only assigned Evidence documents and the human-owned boundary', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T001', actorId: 'A800', items: [review()] });
  mount(api);
  expect((await screen.findAllByText('Family estate documents')).length).toBeGreaterThan(0);
  expect(screen.getByText(/Identity, Consent and financial advice remain with their existing owners/)).toBeVisible();
  expect(screen.getByText('Assigned estate document')).toBeVisible();
  expect(screen.queryByText(/income|net worth|document contents/i)).not.toBeInTheDocument();
});

it('posts only the assignment version and canonical document inspection action', async () => {
  vi.stubGlobal('crypto', { getRandomValues: (values: Uint8Array) => { values.fill(10); return values; } });
  const api = new AccessApi(session); let write: unknown; let path = '';
  vi.spyOn(api, 'case').mockImplementation((requested, method = 'GET', body) => {
    if (method === 'POST') { path = requested; write = body; return Promise.resolve(review({
      state: 'Inspection in progress', version: 2,
      evidence: [{ ...evidence[0], inspected: true, inspectedAt: '2026-11-16T10:00:00Z' }],
    })); }
    return Promise.resolve({ tenantId: 'T001', actorId: 'A800', items: [review()] });
  });
  mount(api);
  fireEvent.click(await screen.findByRole('button', { name: 'Record inspection' }));
  await waitFor(() => {
    expect(write).toEqual({ operationId: '0a'.repeat(16), expectedVersion: 1 });
  });
  expect(path).toContain('/documents/estate-document/inspection');
  expect(JSON.stringify(write)).not.toMatch(/tenant|actor|customer|permission|capability/i);
});

it('shows append-only outcomes bound to exact document versions', async () => {
  const api = new AccessApi(session);
  const inspected = [{ ...evidence[0], inspected: true, inspectedAt: '2026-11-16T10:00:00Z' }];
  const revision = { revision: 1, outcome: 'ACTION_REQUIRED', findings: 'Update beneficiary details.',
    recordedAt: '2026-11-16T10:01:00Z', recordedBy: 'A800', policyVersion: 10, subjectVersion: 2,
    customerVersion: 4, consentVersion: 7, evidence: [{ code: 'estate-document', owner: 'Evidence',
      reference: 'evidence-source-001', version: 1 }] };
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T001', actorId: 'A800', items: [review({
    state: 'Reviewed', version: 3, evidence: inspected, revisions: [revision] })] });
  mount(api);
  expect(await screen.findByText(/Revision 1 · Action required/)).toBeVisible();
  expect(screen.getByText(/1 exact document versions/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Record revised outcome' })).toBeEnabled();
});

it('fails closed on tenant or actor-mismatched assignment data', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T002', actorId: 'A801', items: [review()] });
  mount(api);
  await waitFor(() => expect(screen.getByRole('alert')).toBeVisible());
  expect(screen.queryByText('Assigned estate document')).not.toBeInTheDocument();
});
