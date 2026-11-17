import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import FinancialProfessionalWorkspace from '../src/financial-professional/FinancialProfessionalWorkspace';

const session = { tenantId: 'T001', actorId: 'A600', authenticationContextId: 'professional-session',
  subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const sources = [
  { code: 'profile-summary', label: 'Customer-reviewed financial profile', owner: 'Financial Profile',
    reference: 'financial-profile-source-001', assignedVersion: 1, currentVersion: 1, reviewed: false, reviewedAt: null },
  { code: 'supporting-evidence', label: 'Supporting evidence index', owner: 'Evidence',
    reference: 'evidence-source-001', assignedVersion: 1, currentVersion: 1, reviewed: false, reviewedAt: null },
];
const review = (overrides: Record<string, unknown> = {}) => ({
  reviewId: 'review-001', reviewReference: 'Financial review 001', state: 'Assigned', version: 1,
  assignedAt: '2026-11-15T10:00:00Z', sources, notes: [], certification: null,
  dataDurability: 'Durable test data', ...overrides,
});
function state(api: AccessApi): WorkspaceSessionState {
  return { status: 'authorized', session, api,
    authorization: { area: 'financial-professional', authority: 'FinancialProfessional', policyVersion: 9, subjectVersion: 2 },
    message: '', recovery: null, connect: vi.fn().mockResolvedValue(undefined), retry: vi.fn().mockResolvedValue(undefined),
    signOut: vi.fn().mockResolvedValue(undefined), cancel: vi.fn() };
}
function mount(api: AccessApi) {
  return render(<FoundationProvider appearance="midnight"><Context value={state(api)}>
    <FinancialProfessionalWorkspace />
  </Context></FoundationProvider>);
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('shows only consent-verified assigned review sources and the professional boundary', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T001', actorId: 'A600', items: [review()] });
  mount(api);
  expect((await screen.findAllByText('Financial review 001')).length).toBeGreaterThan(0);
  expect(screen.getByText(/Your recorded conclusion remains your professional responsibility/)).toBeVisible();
  expect(screen.getAllByText('Customer-reviewed financial profile').length).toBeGreaterThan(0);
  expect(screen.queryByText(/C001|net worth|tax return amount/i)).not.toBeInTheDocument();
});

it('posts only the review version and canonical source action', async () => {
  vi.stubGlobal('crypto', { getRandomValues: (values: Uint8Array) => { values.fill(10); return values; } });
  const api = new AccessApi(session); let write: unknown;
  vi.spyOn(api, 'case').mockImplementation((_path, method = 'GET', body) => {
    if (method === 'POST') {
      write = body;
      return Promise.resolve(review({ state: 'Under review', version: 2,
        sources: [{ ...sources[0], reviewed: true, reviewedAt: '2026-11-16T10:00:00Z' }, sources[1]] }));
    }
    return Promise.resolve({ tenantId: 'T001', actorId: 'A600', items: [review()] });
  });
  mount(api);
  const buttons = await screen.findAllByRole('button', { name: 'Mark reviewed' });
  const first = buttons[0];
  if (!first) throw new Error('Missing source review action');
  fireEvent.click(first);
  expect(await screen.findByText(/was marked professionally reviewed/)).toBeVisible();
  expect(write).toEqual({ operationId: '0a'.repeat(16), expectedVersion: 1 });
  expect(JSON.stringify(write)).not.toMatch(/tenant|actor|customer|permission|capability/i);
});

it('records signer-owned certification only after every source is reviewed', async () => {
  vi.stubGlobal('crypto', { getRandomValues: (values: Uint8Array) => { values.fill(11); return values; } });
  const api = new AccessApi(session); let write: unknown;
  const reviewed = sources.map(source => ({ ...source, reviewed: true, reviewedAt: '2026-11-16T10:00:00Z' }));
  vi.spyOn(api, 'case').mockImplementation((_path, method = 'GET', body) => {
    if (method === 'POST') {
      write = body;
      return Promise.resolve(review({ state: 'Certified', version: 4, sources: reviewed,
        certification: { outcome: 'Certified with exceptions', rationale: 'Limited to the assigned source versions.',
          certifiedAt: '2026-11-16T10:01:00Z', certifiedBy: 'A600', policyVersion: 9,
          subjectVersion: 2, customerVersion: 4, consentVersion: 7,
          sources: reviewed.map(source => ({ code: source.code, owner: source.owner,
            reference: source.reference, version: source.currentVersion })) } }));
    }
    return Promise.resolve({ tenantId: 'T001', actorId: 'A600', items: [review({
      state: 'Under review', version: 3, sources: reviewed })] });
  });
  mount(api);
  fireEvent.click(await screen.findByRole('radio', { name: 'Certified with exceptions' }));
  fireEvent.change(screen.getByLabelText('Professional rationale'), { target: { value: 'Limited to the assigned source versions.' } });
  fireEvent.click(screen.getByRole('button', { name: 'Record certification' }));
  expect(await screen.findByText('Certified with exceptions', { selector: '.ant-alert-title' })).toBeVisible();
  expect(write).toEqual({ operationId: '0b'.repeat(16), expectedVersion: 3,
    outcome: 'Certified with exceptions', rationale: 'Limited to the assigned source versions.' });
});

it('fails closed on tenant or actor-mismatched list data', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T002', actorId: 'A601', items: [review()] });
  mount(api);
  await waitFor(() => { expect(screen.getByRole('alert')).toBeVisible(); });
  expect(screen.queryByText('Customer-reviewed financial profile')).not.toBeInTheDocument();
});
