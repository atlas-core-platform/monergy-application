import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { DocumentOcrView } from '../src/product/DocumentOcrView';
import type { OcrResult } from '../src/product/DocumentOcrView';
const result: OcrResult = { tenantId: 'T001', customerId: 'customer-one', documentVersionId: 'version-one', sourceHash: 'a'.repeat(64), engineVersion: 'document-ocr/1; tesseract-5.3.4-eng; poppler-24.02.0', state: 'AVAILABLE', recordedAt: '2026-10-22T00:00:00Z', pages: [{ number: 1, lines: [{ number: 1, text: 'Private first page', confidence: 99 }] }, { number: 2, lines: [{ number: 1, text: 'Private second page', confidence: 99 }] }] };
function setup(value: unknown = result) {
 const api = new AccessApi({ tenantId: 'T001', actorId: 'A100', authenticationContextId: 'session-one', subjectVersion: 1, expiresAt: '2099-01-01T00:00:00Z' });
 const request = vi.spyOn(api, 'documentOcr').mockImplementation(async () => { await Promise.resolve(); if (value instanceof Error) throw value; return value; });
 const props = { api, tenantId: result.tenantId, customerId: result.customerId, versionId: result.documentVersionId, sourceHash: result.sourceHash };
 const view = render(<FoundationProvider><DocumentOcrView {...props} /></FoundationProvider>); return { request, view, props };
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
function open() { fireEvent.click(screen.getByRole('button', { name: 'Read scanned text' })); }
it('reads pages lazily and refreshes without processing commands', async () => {
 const { request } = setup(); expect(request).not.toHaveBeenCalled(); open(); expect(await screen.findByText('Private second page')).toBeVisible(); expect(screen.getByRole('region', { name: 'OCR page 1' })).toBeVisible();
 expect(request).toHaveBeenCalledWith('customer-one', 'version-one', expect.any(AbortSignal)); fireEvent.click(screen.getByRole('button', { name: 'Refresh scanned text' })); await waitFor(() => { expect(request).toHaveBeenCalledTimes(2); });
});
it('preserves uncertain lines and empty pages with explicit warnings', async () => {
 setup({ ...result, state: 'UNRELIABLE_TEXT', pages: [{ number: 1, lines: [{ number: 1, text: 'Uncertain amount', confidence: 45 }] }, { number: 2, lines: [] }] }); open(); expect(await screen.findByText('Uncertain amount')).toBeVisible(); expect(screen.getByText('Uncertain text')).toBeVisible(); expect(screen.getByText('No text detected on this page.')).toBeVisible();
});
it.each(['NOT_PROCESSED', 'UNSUPPORTED', 'NO_TEXT'])('shows %s without inventing text', async state => {
 setup({ ...result, state, pages: state === 'NO_TEXT' ? [{ number: 1, lines: [] }] : [] }); open(); expect(await screen.findByRole('alert')).toBeVisible(); expect(screen.queryByText('Private first page')).not.toBeInTheDocument();
});
it.each(['tenant', 'customer', 'version', 'hash', 'page', 'confidence', 'state'])('rejects invalid %s before displaying text', async mode => {
 const bad = structuredClone(result); const page = bad.pages[0]; const line = page?.lines[0]; if (!page || !line) throw new Error("Missing fixture line"); if (mode === 'tenant') bad.tenantId = 'T002'; if (mode === 'customer') bad.customerId = 'foreign'; if (mode === 'version') bad.documentVersionId = 'foreign'; if (mode === 'hash') bad.sourceHash = 'b'.repeat(64); if (mode === 'page') page.number = 2; if (mode === 'confidence') line.confidence = 42; if (mode === 'state') bad.state = 'NO_TEXT';
 setup(bad); open(); expect(await screen.findByRole('alert')).toBeVisible(); expect(screen.queryByText('Private first page')).not.toBeInTheDocument();
});
it('hides old content when the source changes', async () => {
 const { props, view, request } = setup(); open(); expect(await screen.findByText('Private first page')).toBeVisible(); view.rerender(<FoundationProvider><DocumentOcrView {...props} versionId="version-two" /></FoundationProvider>); expect(screen.queryByText('Private first page')).not.toBeInTheDocument(); expect(request).toHaveBeenCalledTimes(1);
});
it('revoked access clears stale text on refresh', async () => {
 const { request } = setup(); open(); expect(await screen.findByText('Private first page')).toBeVisible(); request.mockRejectedValueOnce(new AccessApiError(403, 'ACCESS_DENIED')); fireEvent.click(screen.getByRole('button', { name: 'Refresh scanned text' })); expect(await screen.findByRole('alert')).toBeVisible(); expect(screen.queryByText('Private first page')).not.toBeInTheDocument();
});
