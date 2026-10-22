import { Alert, Button, Drawer, Tag } from 'antd';
import { useEffect, useState } from 'react';
import { AccessApiError, displayError } from '../access/accessApi';
import type { AccessApi } from '../access/accessApi';
interface OcrLine { number: number; text: string; confidence: number }
export interface OcrResult { tenantId: string; customerId: string; documentVersionId: string; sourceHash: string; engineVersion: string; state: string; recordedAt: string; pages: { number: number; lines: OcrLine[] }[] }
interface Props { api: AccessApi; tenantId: string; customerId: string; versionId: string; sourceHash: string }
export function DocumentOcrView(props: Props) { return <ScopedDocumentOcrView key={[props.tenantId, props.customerId, props.versionId, props.sourceHash].join('|')} {...props} />; }
function ScopedDocumentOcrView({ api, tenantId, customerId, versionId, sourceHash }: Props) {
 const [open, setOpen] = useState(false); const [result, setResult] = useState<OcrResult | null>(null); const [error, setError] = useState(''); const [refresh, setRefresh] = useState(0);
 useEffect(() => {
  if (!open) return; const controller = new AbortController();
  void api.documentOcr<OcrResult>(customerId, versionId, controller.signal).then(value => {
   controller.signal.throwIfAborted();
   if (value.tenantId !== tenantId || value.customerId !== customerId || value.documentVersionId !== versionId || value.sourceHash !== sourceHash || value.engineVersion !== 'document-ocr/1; tesseract-5.3.4-eng; poppler-24.02.0' || !['NOT_PROCESSED', 'AVAILABLE', 'UNRELIABLE_TEXT', 'NO_TEXT', 'UNSUPPORTED'].includes(value.state) || !Number.isFinite(Date.parse(value.recordedAt)) || !Array.isArray(value.pages) || value.pages.length > 20 || value.pages.some((page, index) => page.number !== index + 1 || !Array.isArray(page.lines) || page.lines.length > 1000 || page.lines.some((line, i) => line.number !== i + 1 || typeof line.text !== 'string' || line.text.length === 0 || line.text.length > 4000 || /[\p{Cc}]/u.test(line.text) || !Number.isFinite(line.confidence) || line.confidence < 0 || line.confidence > 100)) || value.pages.flatMap(x => x.lines).reduce((sum, x) => sum + x.text.length, 0) > 200_000 || ['NOT_PROCESSED', 'UNSUPPORTED'].includes(value.state) && value.pages.length !== 0 || value.state === 'AVAILABLE' && (value.pages.length === 0 || value.pages.some(x => x.lines.length === 0 || x.lines.some(line => line.confidence < 90)))) throw new AccessApiError(503, 'OCR_RESULT_INVALID');
   const inferred = value.pages.every(x => x.lines.length === 0) ? 'NO_TEXT' : value.pages.some(x => x.lines.length === 0 || x.lines.some(line => line.confidence < 90)) ? 'UNRELIABLE_TEXT' : 'AVAILABLE';
   if (!['NOT_PROCESSED', 'UNSUPPORTED'].includes(value.state) && (value.pages.length === 0 || value.state !== inferred)) throw new AccessApiError(503, 'OCR_RESULT_INVALID');
   setResult(value);
  }).catch((e: unknown) => { if (!controller.signal.aborted) setError(displayError(e)); });
  return () => { controller.abort(); };
 }, [api, tenantId, customerId, versionId, sourceHash, open, refresh]);
 return <><Button onClick={() => { setResult(null); setError(''); setOpen(true); }}>Read scanned text</Button>
  <Drawer title="Scanned document text" open={open} onClose={() => { setOpen(false); setResult(null); }} size={720} destroyOnHidden>
   <p>OCR transcribes the original scan. Check the original before relying on any wording or figure. This text is separate from financial extraction and verification.</p>
   {error && <Alert type="error" title={error} />}
   {!result && !error && <p>Loading recorded OCR…</p>}
   {result?.state === 'NOT_PROCESSED' && <Alert type="info" title="No OCR result is recorded for this version." description="Request document processing first, then refresh this view." />}
   {result?.state === 'UNSUPPORTED' && <Alert type="warning" title="This scan could not be transcribed." description="Use an unlocked PDF of up to 20 pages, or a supported PNG/JPEG scan. Upload a clearer or smaller document if needed." />}
   {result?.state === 'NO_TEXT' && <Alert type="info" title="No text was detected in this scan." />}
   {result?.state === 'UNRELIABLE_TEXT' && <Alert type="warning" title="Some text is uncertain or a page has no readable text." description="Uncertain lines are marked. Review the original; this text must not be treated as verified financial information." />}
   {result?.pages.map(page => <section key={page.number} aria-label={'OCR page ' + String(page.number)}><h3>Page {page.number}</h3>{page.lines.length === 0 ? <p>No text detected on this page.</p> : page.lines.map(line => <p key={line.number} style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{line.confidence < 90 && <Tag color="orange">Uncertain text</Tag>}{line.text}</p>)}</section>)}
   <Button onClick={() => { setResult(null); setError(''); setRefresh(x => x + 1); }}>Refresh scanned text</Button>
  </Drawer>
 </>;
}
