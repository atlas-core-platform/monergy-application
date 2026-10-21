import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Drawer, Input, Select, Spin } from 'antd';
import { AccessApiError } from '../access/accessApi';
import type { AccessApi } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import { evidenceContentType, maximumEvidenceBytes } from './productApi';
import type { ProductEvidenceItem } from './productApi';
import { listVault, readVault, replaceVault, saveVaultMetadata, vaultCategories } from './documentVaultApi';
import type { VaultItem, VaultMetadataChange, VaultPage, VaultReplacement } from './documentVaultApi';

type Pending = { kind: 'metadata'; version: string; change: VaultMetadataChange } | { kind: 'replacement'; document: string; change: VaultReplacement };
const message = (error: unknown) => error instanceof AccessApiError && error.code === 'VAULT_CONTENT_UNCHANGED' ? 'This file has the same content. No new version was created.' :
  error instanceof AccessApiError && error.status === 409 ? 'The document changed or this operation conflicts. Refresh the document before trying again.' :
  error instanceof AccessApiError && (error.status === 401 || error.status === 403) ? 'Your current document access is unavailable. Reconnect or contact your administrator.' :
  'The request could not be confirmed. You can retry the same operation safely.';

export default function DocumentVault({ onOpen, refreshKey }: { onOpen: (item: ProductEvidenceItem) => void; refreshKey: string }) {
  const { api, authorization, session } = useWorkspaceSession();
  const context = authorization?.area === 'product' ? authorization.context : null;
  if (!api || !context?.capabilities.includes('evidence.document.read')) return null;
  return <Vault key={`${context.tenantId}:${context.actorId}:${context.customerId}:${session?.authenticationContextId ?? ''}:${context.capabilities.join(',')}`}
    api={api} canWrite={context.capabilities.includes('evidence.document.upload')} onOpen={onOpen} refreshKey={refreshKey} />;
}

function Vault({ api, canWrite, onOpen, refreshKey }: { api: AccessApi; canWrite: boolean; onOpen: (item: ProductEvidenceItem) => void; refreshKey: string }) {
  const [category, setCategory] = useState('all'); const [search, setSearch] = useState('');
  const [after, setAfter] = useState<string | null>(null); const [refresh, setRefresh] = useState(0);
  const [page, setPage] = useState<VaultPage | null>(null); const [loading, setLoading] = useState(false); const [error, setError] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      setLoading(true); setPage(null); setError('');
      try { const result = await listVault(api, category, search, after, controller.signal); if (!controller.signal.aborted) setPage(result); }
      catch (failure) { if (!controller.signal.aborted) setError(message(failure)); }
      finally { if (!controller.signal.aborted) setLoading(false); }
    })();
    return () => { controller.abort(); };
  }, [api, category, search, after, refresh, refreshKey]);
  return <section className="product-recent" aria-labelledby="document-vault-title">
    <div className="product-section-heading"><div><p className="workspace-eyebrow">DOCUMENT VAULT</p><h2 id="document-vault-title">Browse all document versions</h2></div>
      <Button onClick={() => { setAfter(null); setRefresh(x => x + 1); }}>Refresh vault</Button></div>
    <p>Organize documents by category. Each version keeps its original evidence and source links.</p>
    <div className="product-library-controls">
      <Select id="vault-category-filter" virtual={false} aria-label="Document category" value={category} onChange={value => { setCategory(value); setAfter(null); }} options={[{ value: 'all', label: 'All categories' }, ...vaultCategories.map(value => ({ value, label: value }))]} />
      <Input aria-label="Search the vault" placeholder="Find by file name" value={search} maxLength={120} onChange={event => { setSearch(event.target.value); setAfter(null); }} />
    </div>
    {loading && <Spin aria-label="Loading document vault" />}
    {error && <Alert type="warning" title={error} />}
    {page && <><div className="product-receipts">{page.items.map(row => <article className="product-receipt" key={row.documentVersionId}>
      <button className="product-receipt-action" type="button" onClick={() => { setSelected(row.documentVersionId); }}>
        <span className="product-receipt-copy"><strong>{row.originalFileName}</strong><span>{row.metadata.category} · Version {row.version} · {row.metadata.documentType ?? 'Type not recorded'}</span></span>
        <span>{row.metadata.financialYear ?? 'Financial year not recorded'}</span>
      </button></article>)}</div>
      {page.items.length === 0 && <p>No document versions match this category and search.</p>}
      <div className="product-library-controls"><Button disabled={after === null} onClick={() => { setAfter(null); }}>First page</Button>
        <Button disabled={page.nextCursor === null} onClick={() => { setAfter(page.nextCursor); }}>Next page</Button></div></>}
    <Drawer title="Vault document" open={selected !== null} onClose={() => { setSelected(null); }} destroyOnHidden>
      {selected && <VaultDetails key={selected} api={api} version={selected} canWrite={canWrite} onOpen={row => { setSelected(null); onOpen(row); }} onSaved={() => { setRefresh(x => x + 1); }} />}
    </Drawer>
  </section>;
}

function VaultDetails({ api, version, canWrite, onOpen, onSaved }: { api: AccessApi; version: string; canWrite: boolean; onOpen: (item: ProductEvidenceItem) => void; onSaved: () => void }) {
  const controller = useRef(new AbortController());
  const [row, setRow] = useState<VaultItem | null>(null); const [error, setError] = useState(''); const [notice, setNotice] = useState('');
  const [category, setCategory] = useState('Other'); const [documentType, setType] = useState(''); const [date, setDate] = useState(''); const [year, setYear] = useState('');
  const [file, setFile] = useState<File | null>(null); const [pending, setPending] = useState<Pending | null>(null); const [busy, setBusy] = useState(false); const [reload, setReload] = useState(0);
  const [replacementVersion, setReplacementVersion] = useState<string | null>(null);
  useEffect(() => {
    const active = new AbortController(); controller.current = active;
    void readVault(api, version, active.signal).then(value => {
      if (active.signal.aborted) return; setRow(value); setCategory(value.metadata.category); setType(value.metadata.documentType ?? ''); setDate(value.metadata.documentDate ?? ''); setYear(value.metadata.financialYear ?? '');
    }).catch((failure: unknown) => { if (!active.signal.aborted) setError(message(failure)); });
    return () => { active.abort(); };
  }, [api, version, reload]);
  async function execute(operation: Pending) {
    if (busy) return; setPending(operation); setBusy(true); setError(''); setNotice('');
    const signal = controller.current.signal;
    try {
      if (operation.kind === 'metadata') {
        const result = await saveVaultMetadata(api, operation.version, operation.change, signal);
        if (signal.aborted) return;
        setRow(current => current ? { ...current, metadata: result } : current); setNotice('Document metadata saved.');
      } else {
        const newVersion = await replaceVault(api, operation.document, operation.change, signal);
        if (signal.aborted) return;
        setReplacementVersion(newVersion); setNotice('Replacement saved as a new version. Open it to start processing and review its financial information.'); setFile(null);
      }
      setPending(null); onSaved();
    } catch (failure) {
      if (signal.aborted) return; setError(message(failure));
      // Definite rejection permits a fresh review; an uncertain write retains its exact payload and operation ID.
      if (failure instanceof AccessApiError && [400, 401, 403, 404, 409, 422].includes(failure.status)) { setPending(null); setRow(null); }
    } finally { if (!signal.aborted) setBusy(false); }
  }
  async function prepareReplacement() {
    if (!file || !row || busy || pending) return;
    setBusy(true); const signal = controller.current.signal;
    try {
      const base64 = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader(); reader.onerror = () => { reject(new Error('File read failed')); };
        reader.onload = () => { const result = typeof reader.result === 'string' ? reader.result : ''; const comma = result.indexOf(','); if (comma < 0) reject(new Error('File encoding failed')); else resolve(result.slice(comma + 1)); }; reader.readAsDataURL(file);
      });
      if (signal.aborted) return;
      setPending({ kind: 'replacement', document: row.documentId, change: { operationId: crypto.randomUUID(), expectedVersionId: row.documentVersionId, originalFileName: file.name, contentType: evidenceContentType(file), contentBase64: base64 } });
      setNotice('Review replacement: the original stays available. A different file becomes the next version only if this is still the latest version. Processing starts separately.');
    } catch { if (!signal.aborted) setError('The file could not be read. Select it again.'); }
    finally { if (!signal.aborted) setBusy(false); }
  }
  return <div className="product-review product-vault-detail">
    {error && <Alert type="warning" title={error} />}{notice && <Alert type="info" title={notice} />}
    {!row && !error && <Spin />}
    {!pending && <Button disabled={busy} onClick={() => { setError(''); setRow(null); setReload(x => x + 1); }}>Refresh document</Button>}
    {row && <><h3>{row.originalFileName}</h3><p>Version {row.version} · {row.contentType} · {row.contentLength} bytes</p>
      <dl><dt>Source</dt><dd>{row.sourceName}</dd><dt>Received</dt><dd>{row.receivedAt}</dd>
        <dt>Malware scan</dt><dd>{row.scanStatus === 'VERIFIED_AT_INTAKE' ? 'Verified at intake' : 'No recorded scan evidence'}</dd>
        <dt>Document authenticity</dt><dd>Not verified. Processing and customer-entered metadata do not verify authenticity.</dd>
        <dt>Storage</dt><dd>{row.storageMode === 'TEMPORARY_REFERENCE' ? 'Temporary test environment storage; may be cleared on reset' : 'Evidence owner managed storage'}</dd>
        <dt>Storage location</dt><dd>{row.storageLocation ?? 'Not recorded'}</dd><dt>Storage version</dt><dd>{row.storageVersionId ?? 'Not recorded'}</dd>
        <dt>Original storage provider</dt><dd>{row.sourceLocation?.provider ?? 'Not recorded (manual or legacy intake)'}</dd>
        <dt>Provider folder ID</dt><dd>{row.sourceLocation?.folderId ?? 'Not recorded'}</dd><dt>Provider file ID</dt><dd>{row.sourceLocation?.fileId ?? 'Not recorded'}</dd><dt>Provider file version</dt><dd>{row.sourceLocation?.fileVersion ?? 'Not recorded'}</dd>
        <dt>Checksum (SHA-256)</dt><dd className="product-hash">{row.contentSha256}</dd></dl>
      <Button onClick={() => { onOpen(row); }}>Version history, processing and related financial records</Button>
      <h4>Customer-entered metadata</h4><p>These labels organize your vault. They do not change automated classification or financial records.</p>
      <fieldset disabled={!canWrite || busy || pending !== null || replacementVersion !== null}>
        <label>Category<Select id="vault-metadata-category" virtual={false} aria-label="Edit document category" value={category} disabled={!canWrite || busy || pending !== null || replacementVersion !== null} onChange={setCategory} options={vaultCategories.map(value => ({ value, label: value }))} /></label>
        <label>Document type<Input aria-label="Document type" value={documentType} maxLength={80} onChange={event => { setType(event.target.value); }} /></label>
        <label>Document date<Input aria-label="Document date" type="date" value={date} onChange={event => { setDate(event.target.value); }} /></label>
        <label>Financial year<Input aria-label="Financial year" placeholder="2025-2026" value={year} maxLength={9} onChange={event => { setYear(event.target.value); }} /></label>
        <Button disabled={!canWrite || busy || pending !== null || replacementVersion !== null} onClick={() => { void execute({ kind: 'metadata', version, change: { operationId: crypto.randomUUID(), expectedRevision: row.metadata.revision, category, documentType: documentType.trim() || null, documentDate: date || null, financialYear: year || null } }); }}>Save metadata</Button>
        <h4>Replace this document</h4><p>Choose a changed file, up to 1 MB. Earlier versions and their financial source links remain available.</p>
        <input aria-label="Replacement file" type="file" accept=".pdf,.png,.jpg,.jpeg,.txt,.csv,.xlsx" onChange={event => { const candidate = event.target.files?.[0] ?? null; if (candidate && (candidate.size < 1 || candidate.size > maximumEvidenceBytes)) { setError('Choose a file between 1 byte and 1 MB.'); setFile(null); } else { setFile(candidate); setError(''); } }} />
        <Button disabled={!file || !canWrite || busy || pending !== null || replacementVersion !== null} onClick={() => { void prepareReplacement(); }}>Review replacement</Button>
      </fieldset></>}
    {pending && <><p>{pending.kind === 'replacement' ? pending.change.originalFileName : 'Recover metadata save'} · Same operation will be reused.</p>
      <Button loading={busy} onClick={() => { void execute(pending); }}>{pending.kind === 'replacement' && !error ? 'Confirm replacement' : 'Retry same operation'}</Button></>}
    {replacementVersion && <Button onClick={() => { void readVault(api, replacementVersion, controller.current.signal).then(value => { if (!controller.current.signal.aborted) onOpen(value); }).catch((failure: unknown) => { if (!controller.current.signal.aborted) setError(message(failure)); }); }}>Open replacement and process</Button>}
  </div>;
}
