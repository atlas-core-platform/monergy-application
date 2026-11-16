import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import ProfessionalLegalReviews from '../src/product/ProfessionalLegalReviews';

const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'customer-session',
  subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('shows the latest customer-safe professional result without document contents', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T001', actorId: 'A100', items: [{
    reviewId: 'review-001', matterReference: 'matter-estate', matterLabel: 'Family estate documents',
    revision: 2, outcome: 'ACTION_REQUIRED', findings: 'Update beneficiary details.',
    recordedAt: '2026-11-16T10:00:00Z', recordedBy: 'A800', evidenceCount: 2,
    dataDurability: 'Durable test data',
  }] });
  render(<ProfessionalLegalReviews api={api} session={session} />);
  expect(await screen.findByText('Action required')).toBeVisible();
  expect(screen.getByText('Update beneficiary details.')).toBeVisible();
  expect(screen.getByText(/Recorded by Legal Advisor A800/)).toBeVisible();
  expect(screen.queryByText(/evidence-source/i)).not.toBeInTheDocument();
});

it('fails closed on foreign result envelopes', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'case').mockResolvedValue({ tenantId: 'T002', actorId: 'A100', items: [] });
  render(<ProfessionalLegalReviews api={api} session={session} />);
  await waitFor(() => expect(screen.getByRole('alert')).toBeVisible());
});
