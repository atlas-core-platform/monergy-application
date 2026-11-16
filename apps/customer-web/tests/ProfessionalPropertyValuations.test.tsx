import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import ProfessionalPropertyValuations from '../src/product/ProfessionalPropertyValuations';

const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'customer-session',
  subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('shows the latest professional result separately from the customer-reported value', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T001', actorId: 'A100', items: [{
    valuationId: 'valuation-001', propertyReference: 'property-saved', propertyLabel: 'Family home',
    revision: 2, marketValue: 430000, currency: 'USD', effectiveDate: '2026-11-16',
    basis: 'Comparable sales', findings: 'Revised professional conclusion.',
    recordedAt: '2026-11-16T10:00:00Z', recordedBy: 'A700', evidenceCount: 2,
    dataDurability: 'Durable test data',
  }] });
  render(<ProfessionalPropertyValuations api={api} session={session} />);
  expect(await screen.findByText('USD 430,000')).toBeVisible();
  expect(screen.getByText(/Separate from your reported property value/)).toBeVisible();
  expect(screen.getByText(/Recorded by Property Valuer A700/)).toBeVisible();
});

it('fails closed on foreign result envelopes', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T002', actorId: 'A100', items: [] });
  render(<ProfessionalPropertyValuations api={api} session={session} />);
  await waitFor(() => expect(screen.getByRole('alert')).toBeVisible());
});
