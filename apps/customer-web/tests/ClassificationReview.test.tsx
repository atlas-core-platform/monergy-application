import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { ClassificationReview } from '../src/access/ClassificationReview';
import type { ClassificationRecord, ReviewPage } from '../src/access/ClassificationReview';
const row: ClassificationRecord = { tenantId: 'T001', customerId: 'customer-one', documentVersionId: 'version-one', sourceHash: 'a'.repeat(64), classifierVersion: 'document-classification/1.0.0', automaticClass: 'UNCLASSIFIED', documentClass: 'UNCLASSIFIED', reason: 'NO_SUPPORTED_TITLE', classifiedAt: '2026-10-01T00:00:00Z', revision: 1, needsReview: true, reviews: [] };
function setup(canReview = true, fault?: (method: string) => unknown) {
 const api = new AccessApi({ tenantId: 'T001', actorId: 'A900', authenticationContextId: 'admin-session', subjectVersion: 1, expiresAt: '2099-01-01T00:00:00Z' });
 const calls: { path: string; method: string; body: unknown }[] = []; let current = row;
 vi.spyOn(api, 'classificationReviews').mockImplementation(async (path = '', method = 'GET', body?: unknown) => {
  await Promise.resolve(); calls.push({ path, method, body }); const injected = fault?.(method); if (injected !== undefined) return injected;
  if (method === 'POST') { current = { ...row, documentClass: 'OTHER', revision: 2, needsReview: false, reviews: [{ revision: 2, actorId: 'A900', documentClass: 'OTHER', reason: 'SOURCE_REVIEWED', reviewedAt: '2026-10-02T00:00:00Z' }] }; return current; }
  return { tenantId: 'T001', items: current.needsReview || path.includes('all') ? [current] : [], nextCursor: null, canReview } satisfies ReviewPage;
 });
 render(<FoundationProvider><ClassificationReview api={api} tenantId="T001" /></FoundationProvider>); return calls;
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
async function open() { fireEvent.click(await screen.findByRole('button', { name: 'Review classification version-one' })); }
it('requires explicit confirmation and submits exact source revision without financial data', async () => {
 const calls = setup(); await open(); expect(screen.getByRole('button', { name: 'Save classification review' })).toBeDisabled();
 fireEvent.click(screen.getByRole('checkbox')); fireEvent.click(screen.getByRole('button', { name: 'Save classification review' }));
 expect(await screen.findByText(/Revision 2: OTHER/)).toBeVisible(); expect(calls.find(x => x.method === 'POST')).toEqual({ path: '/customer-one/version-one', method: 'POST', body: { expectedRevision: 1, documentClass: 'OTHER', reason: 'SOURCE_REVIEWED' } });
 expect(screen.getByRole('link', { name: 'View processing for this version' })).toHaveAttribute('href', '/access/processing?customerId=customer-one&documentVersionId=version-one');
 expect(screen.getByText(/does not verify identity/)).toBeVisible();
});
it('keeps ungranted administrator metadata read only', async () => { const calls = setup(false); await open(); expect(screen.getByText(/Review permission is not configured/)).toBeVisible(); expect(screen.getByRole('checkbox')).toBeDisabled(); expect(screen.getByRole('button', { name: 'Save classification review' })).toBeDisabled(); expect(calls.every(x => x.method === 'GET')).toBe(true); });
it('handles competing reviewer changes without silently retrying', async () => { const calls = setup(true, method => { if (method === 'POST') throw new AccessApiError(409, 'CLASSIFICATION_REVISION_CONFLICT'); }); await open(); fireEvent.click(screen.getByRole('checkbox')); fireEvent.click(screen.getByRole('button', { name: 'Save classification review' })); expect(await screen.findByText(/This classification changed/)).toBeVisible(); expect(screen.getByRole('checkbox')).not.toBeChecked(); expect(calls.filter(x => x.method === 'POST')).toHaveLength(1); });
it.each(['tenant', 'version', 'class', 'history'])('rejects forged %s metadata', async mode => {
 setup(true, () => ({ tenantId: 'T001', canReview: true, nextCursor: null, items: [{ ...row, ...(mode === 'tenant' ? { tenantId: 'T002' } : mode === 'version' ? { documentVersionId: 'bad version' } : mode === 'class' ? { documentClass: 'PRIVATE VALUE' } : { revision: 4 }) }] }));
 expect(await screen.findByRole('alert')).toBeVisible(); expect(screen.queryByRole('button', { name: 'Review classification version-one' })).not.toBeInTheDocument();
});
it('fails closed when the owner is unavailable', async () => { setup(true, () => { throw new AccessApiError(503, 'UNAVAILABLE'); }); expect(await screen.findByRole('alert')).toBeVisible(); expect(screen.queryByText('version-one')).not.toBeInTheDocument(); });
it('uses the bounded owner cursor for subsequent pages', async () => {
 const rows = Array.from({ length: 50 }, (_, i) => ({ ...row, documentVersionId: 'version-' + String(i) }));
 const calls = setup(true, () => ({ tenantId: 'T001', canReview: true, nextCursor: 'b'.repeat(64), items: rows }));
 const next = screen.getByRole('button', { name: 'Next classifications' }); await waitFor(() => { expect(next).toBeEnabled(); }); fireEvent.click(next);
 await waitFor(() => { expect(calls.at(-1)?.path).toContain('after=' + 'b'.repeat(64)); });
});
