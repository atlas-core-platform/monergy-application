import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import type { WorkspaceSessionState } from '../src/access/workspaceSession';
import PropertyValuerWorkspace from '../src/property-valuer/PropertyValuerWorkspace';

const session = { tenantId: 'T001', actorId: 'A700', authenticationContextId: 'valuer-session',
  subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const evidence = [
  { code: 'property-record', label: 'Customer-reviewed property record', owner: 'Financial Profile',
    reference: 'property-saved', assignedVersion: 1, currentVersion: 1, inspected: false, inspectedAt: null },
  { code: 'title-evidence', label: 'Assigned title evidence', owner: 'Evidence',
    reference: 'evidence-source-001', assignedVersion: 1, currentVersion: 1, inspected: false, inspectedAt: null },
];
const valuation = (overrides: Record<string, unknown> = {}) => ({
  valuationId: 'valuation-001', propertyReference: 'property-saved', propertyLabel: 'Family home',
  state: 'Assigned', version: 1, assignedAt: '2026-11-15T10:00:00Z', evidence, revisions: [],
  dataDurability: 'Durable test data', ...overrides,
});
function state(api: AccessApi): WorkspaceSessionState {
  return { status: 'authorized', session, api,
    authorization: { area: 'property-valuer', authority: 'PropertyValuer', policyVersion: 10, subjectVersion: 2 },
    message: '', recovery: null, connect: vi.fn().mockResolvedValue(undefined),
    retry: vi.fn().mockResolvedValue(undefined), signOut: vi.fn().mockResolvedValue(undefined), cancel: vi.fn() };
}
function mount(api: AccessApi) {
  return render(<FoundationProvider appearance="midnight"><Context value={state(api)}>
    <PropertyValuerWorkspace />
  </Context></FoundationProvider>);
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('shows only the assigned property and distinguishes reported values from professional valuation', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T001', actorId: 'A700', items: [valuation()] });
  mount(api);
  expect((await screen.findAllByText('Family home')).length).toBeGreaterThan(0);
  expect(screen.getByText(/Customer-reported property values and Monergy calculations are supporting information only/)).toBeVisible();
  expect(screen.getByText('Assigned title evidence')).toBeVisible();
  expect(screen.queryByText(/customer id|C001|income|net worth/i)).not.toBeInTheDocument();
});

it('posts only the assignment version and canonical evidence inspection action', async () => {
  vi.stubGlobal('crypto', { getRandomValues: (values: Uint8Array) => { values.fill(10); return values; } });
  const api = new AccessApi(session); let write: unknown; let path = '';
  vi.spyOn(api, 'case').mockImplementation((requested, method = 'GET', body) => {
    if (method === 'POST') {
      path = requested; write = body;
      return Promise.resolve(valuation({ state: 'Inspection in progress', version: 2,
        evidence: [{ ...evidence[0], inspected: true, inspectedAt: '2026-11-16T10:00:00Z' }, evidence[1]] }));
    }
    return Promise.resolve({ tenantId: 'T001', actorId: 'A700', items: [valuation()] });
  });
  mount(api);
  const first = (await screen.findAllByRole('button', { name: 'Mark reviewed' }))[0];
  if (!first) throw new Error('Missing inspection action');
  fireEvent.click(first);
  await waitFor(() => { expect(write).toEqual({ operationId: '0a'.repeat(16), expectedVersion: 1 }); });
  expect(path).toContain('/property-record/inspection');
  expect(JSON.stringify(write)).not.toMatch(/tenant|actor|customer|permission|capability/i);
});

it('shows immutable revision history from exact evidence snapshots', async () => {
  const api = new AccessApi(session);
  const inspected = evidence.map(item => ({ ...item, inspected: true, inspectedAt: '2026-11-16T10:00:00Z' }));
  const revision = { revision: 1, marketValue: 425000, currency: 'USD', effectiveDate: '2026-11-15',
    basis: 'Comparable sales', findings: 'Limited to the assigned exact evidence versions.',
    recordedAt: '2026-11-16T10:01:00Z', recordedBy: 'A700', policyVersion: 10, subjectVersion: 2,
    customerVersion: 4, consentVersion: 7, evidence: inspected.map(item => ({ code: item.code,
      owner: item.owner, reference: item.reference, version: item.currentVersion })) };
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T001', actorId: 'A700', items: [valuation({
    state: 'Valued', version: 4, evidence: inspected, revisions: [revision] })] });
  mount(api);
  expect(await screen.findByText(/15 Nov 2026/)).toBeVisible();
  expect(screen.getByText(/2 supporting documents reviewed/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Record revised valuation' })).toBeEnabled();
});

it('fails closed on tenant or actor-mismatched assignment data', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T002', actorId: 'A701', items: [valuation()] });
  mount(api);
  await waitFor(() => expect(screen.getByRole('alert')).toBeVisible());
  expect(screen.queryByText('Assigned title evidence')).not.toBeInTheDocument();
});
