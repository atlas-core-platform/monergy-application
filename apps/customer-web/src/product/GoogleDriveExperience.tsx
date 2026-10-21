import { Alert, Button, Checkbox, Select, Tag } from 'antd';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';
import type { AccessApi } from '../access/accessApi';
import './product.css';

interface Receipt { fileId: string; fileVersion: string; name: string; documentId: string; documentVersionId: string; sha256: string; importedAt: string }
interface View { accountLabel?: string | null; tenantId: string; customerId: string; provider: string; status: string; connectionId: string | null; version: number; folderName: string | null; refreshedAt: string | null; imports: Receipt[]; dataDurability: string }
interface File { id: string; name: string; mimeType: string; version: string; size: number; canDownload: boolean; trashed: boolean; parents: string[]; md5Checksum: string | null }
interface Page { items: File[]; nextPageToken: string | null }
interface State { api: AccessApi; scope: string; view: View | null; page: Page | null; folders: boolean; busy: boolean; error: string; notice: string; authorizationUrl?: string }
const identifier = (value: unknown) => typeof value === 'string' && /^[A-Za-z0-9_-]{1,200}$/.test(value);
const contentTypes = new Set(['application/pdf', 'image/png', 'image/jpeg', 'text/plain', 'text/csv', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet']);
function validReceipt(value: Receipt | null | undefined) { return value && identifier(value.fileId) && /^\d+$/.test(value.fileVersion) &&
  typeof value.name === 'string' && identifier(value.documentId) && identifier(value.documentVersionId) &&
  /^[a-f0-9]{64}$/.test(value.sha256) && Number.isFinite(Date.parse(value.importedAt)); }
function validView(value: View | null | undefined, tenant: string, customer: string) {
  return value?.tenantId === tenant && value.customerId === customer && value.provider === 'Google Drive' &&
    ['Not configured', 'Configured', 'Waiting permission', 'Choose folder', 'Connected', 'Needs attention', 'Permission expired', 'Disconnected', 'Disconnected; Google revocation unconfirmed'].includes(value.status) &&
    (value.accountLabel == null || typeof value.accountLabel === 'string') && Number.isSafeInteger(value.version) && value.version >= 0 && (value.connectionId === null || /^[a-f0-9]{32}$/.test(value.connectionId)) &&
    (value.refreshedAt === null || Number.isFinite(Date.parse(value.refreshedAt))) && (value.folderName === null || typeof value.folderName === 'string') && Array.isArray(value.imports) && value.imports.every(validReceipt);
}
function validPage(value: Page | null | undefined) {
  return value && Array.isArray(value.items) && value.items.length <= 50 && (value.nextPageToken === null || typeof value.nextPageToken === 'string' && value.nextPageToken.length <= 2048) &&
    value.items.every(file => identifier(file.id) && typeof file.name === 'string' && file.name.length <= 1000 &&
      typeof file.mimeType === 'string' && /^\d+$/.test(file.version) && Number.isSafeInteger(file.size) && typeof file.canDownload === 'boolean' && typeof file.trashed === 'boolean');
}
function importable(file: File) { return !file.trashed && file.canDownload && contentTypes.has(file.mimeType) && file.size > 0 && file.size <= 1048576 && file.md5Checksum?.length === 32; }

export default function GoogleDriveExperience() {
  const { api, session, authorization } = useWorkspaceSession();
  const customer = authorization?.area === 'product' ? authorization.context : null;
  const scope = JSON.stringify([session?.tenantId, session?.actorId, session?.authenticationContextId, session?.subjectVersion, customer?.customerId]);
  const allowed = customer?.capabilities.includes('integration-gateway.connection.manage');
  const [state, setState] = useState<State | null>(null);
  const [accept, setAccept] = useState(false);
  const [selection, setSelection] = useState<string | undefined>();
  const [folderConsent, setFolderConsent] = useState(false);
  const [disconnect, setDisconnect] = useState(false);
  const active = useRef<AbortController | null>(null);
  const context = useRef({ api, scope });
  useLayoutEffect(() => { context.current = { api, scope }; }, [api, scope]);
  const current = state?.api === api && state.scope === scope ? state : null;
  const view = current?.view;
  const busy = current?.busy ?? true;
  const run = useCallback(async (task: (signal: AbortSignal, previous: State) => Promise<Partial<State>>) => {
    if (!api || !customer || !allowed) return;
    active.current?.abort(); const controller = new AbortController(); active.current = controller;
    const previous: State = state?.api === api && state.scope === scope ? state : { api, scope, view: null, page: null, folders: false, busy: false, error: '', notice: '' };
    setState({ ...previous, busy: true, error: '', notice: '' });
    try {
      const result = await task(controller.signal, previous);
      if (!controller.signal.aborted && context.current.api === api && context.current.scope === scope)
        setState({ ...previous, ...result, busy: false, error: '' });
    } catch {
      if (!controller.signal.aborted && context.current.api === api && context.current.scope === scope)
        setState({ ...previous, page: null, busy: false, error: 'The connection could not be updated. Refresh to check permission and configuration. Reconnect if permission has expired; try again later if Google or an owner service is unavailable.' });
    }
  }, [api, customer, allowed, scope, state]);
  const load = useCallback(async (signal: AbortSignal): Promise<Partial<State>> => {
    if (!api || !customer || !session) throw new Error('CONTEXT_REQUIRED');
    const result = await api.googleDrive<View>('', undefined, signal);
    if (!validView(result, session.tenantId, customer.customerId)) throw new Error('INVALID_CONNECTION');
    return { view: result, page: null, authorizationUrl: undefined };
  }, [api, customer, session]);
  // Context changes clear both rendered values and pending mutation intents.
  useEffect(() => {
    const controller = new AbortController(); active.current = controller;
    const timer = window.setTimeout(() => {
      setAccept(false); setSelection(undefined); setFolderConsent(false); setDisconnect(false);
      if (!api || !allowed) return;
      setState({ api, scope, view: null, page: null, folders: false, busy: true, error: '', notice: '' });
      void load(controller.signal).then(result => {
        if (!controller.signal.aborted) setState({ api, scope, view: null, page: null, folders: false, busy: false, error: '', notice: '', ...result });
      }).catch(() => {
        if (!controller.signal.aborted) setState({ api, scope, view: null, page: null, folders: false, busy: false, error: 'Connection status is unavailable. Refresh to try again.', notice: '' });
      });
    }, 0);
    return () => { window.clearTimeout(timer); controller.abort(); active.current?.abort(); };
  }, [api, allowed, scope, load]);
  if (!customer || !api) return <main className="product-page"><h1>Connections</h1><p>Open your customer workspace to manage connections.</p></main>;
  if (!allowed) return <main className="product-page"><h1>Connections</h1><p>Your current access does not allow managing connections. Ask your organization administrator for connection access.</p></main>;
  const list = (folders: boolean, page?: string) => { setSelection(undefined); setFolderConsent(false); void run(async signal => {
    const result = await api.googleDrive<Page>(folders ? 'folders' : 'files', undefined, signal, page);
    if (!validPage(result)) throw new Error('INVALID_FILES');
    return { page: result, folders };
  }); };
  return <main className="product-page" aria-busy={busy}>
    <h1>Connections</h1><h2>Google Drive</h2>
    <p>Choose an existing folder to use as your Monergy document source. Import supported files when you need them; nothing is imported automatically.</p>
    {current?.error && <Alert type="warning" title={current.error} showIcon />}
    {current?.notice && <Alert type="success" title={current.notice} showIcon />}
    <Button disabled={busy} onClick={() => { void run(signal => load(signal)); }}>Refresh connection</Button>
    {view && <><p><Tag>{view.status}</Tag>{view.folderName && <>Folder: <strong>{view.folderName}</strong></>}</p>
      {view.accountLabel && <p>Google account: {view.accountLabel}</p>}
      {view.refreshedAt && <p>Last confirmed refresh: {new Date(view.refreshedAt).toLocaleString()}</p>}
      <p>{view.dataDurability}. Imported files do not change financial information until you review and confirm extracted details.</p>
      {view.status === 'Not configured' && <Alert type="info" title="Your organization has not configured Google Drive. An administrator can provide its Google OAuth configuration during onboarding. You can still upload documents directly." />}
      {['Configured', 'Disconnected', 'Disconnected; Google revocation unconfirmed'].includes(view.status) && <section aria-label="Connect Google Drive">
        <p>Google will request permission to view and download all files in the selected Google account. Monergy lists folders so you can choose one, then imports only files directly inside that folder. It does not edit or delete Google files.</p>
        <p>Your Monergy permission lasts up to 90 days. You can disconnect sooner. Google may require the organization’s app to be verified before allowing access.</p>
        <Checkbox checked={accept} disabled={busy} onChange={event => { setAccept(event.target.checked); }}>I allow Google Drive access for document import as described above.</Checkbox>
        <Button disabled={busy || !accept} onClick={() => { void run(async signal => {
          const result = await api.googleDrive<{ authorizationUrl: string }>('authorize', { acceptReadAccess: true }, signal);
          const url = new URL(result.authorizationUrl);
          if (url.origin !== 'https://accounts.google.com' || url.pathname !== '/o/oauth2/v2/auth') throw new Error('INVALID_AUTHORIZATION');
          const updated = await load(signal); return { ...updated, authorizationUrl: url.href };
        }); }}>Prepare connection</Button>
      </section>}
      {current.authorizationUrl && <p><a href={current.authorizationUrl} target="_blank" rel="noopener noreferrer">Continue to Google</a>. Then return here and refresh the connection.</p>}
      {view.status === 'Waiting permission' && <p>Finish authorization in Google, then refresh. If that window is unavailable, disconnect and start again.</p>}
      {['Choose folder', 'Connected'].includes(view.status) && <section aria-label="Drive folder and files">
        <Button disabled={busy} onClick={() => { list(true); }}>Choose folder</Button>
        {view.status === 'Connected' && <Button disabled={busy} onClick={() => { list(false); }}>Refresh files</Button>}
        {current.page && <>
          {current.folders ? <>
            <Select aria-label="Google Drive folder" placeholder="Select a folder" value={selection} style={{ minWidth: 240 }} disabled={busy}
              onChange={value => { setSelection(value); setFolderConsent(false); }} options={current.page.items.map(file => ({ value: file.id, label: file.name }))} />
            <Checkbox checked={folderConsent} disabled={busy || !selection} onChange={event => { setFolderConsent(event.target.checked); }}>I allow imports from this folder.</Checkbox>
            <Button disabled={busy || !selection || !folderConsent} onClick={() => { void run(async signal => {
              const result = await api.googleDrive<View>('folder', { version: view.version, id: selection, fileVersion: null, acceptFolderImport: true }, signal);
              if (!validView(result, customer.tenantId, customer.customerId)) throw new Error('INVALID_CONNECTION');
              return { view: result, page: null, notice: 'Folder connected. Select Refresh files to choose documents to import.' };
            }); }}>Use this folder</Button>
          </> : <><p>Supported PDF, PNG, JPEG, text, CSV and XLSX files up to 1 MiB. Google Docs, shortcuts, subfolders and larger files are not imported.</p>
            <ul>{current.page.items.map(file => <li key={file.id}><strong>{file.name}</strong> — {Math.ceil(file.size / 1024)} KiB {' '}
              {importable(file) ? <Button disabled={busy} onClick={() => { void run(async signal => {
                const result = await api.googleDrive<Receipt>('import', { version: view.version, id: file.id, fileVersion: file.version }, signal);
                if (!validReceipt(result) || result.fileId !== file.id || result.fileVersion !== file.version) throw new Error('INVALID_IMPORT');
                const updated = await load(signal); return { ...updated, notice: `${file.name} imported. Open Documents to review and process it.` };
              }); }}>Import {file.name}</Button> : <span>Not supported for import</span>}</li>)}</ul>
          </>}
          {current.page.items.length === 0 && <p>No items in this page.</p>}
          {current.page.nextPageToken && <Button disabled={busy} onClick={() => { if (current.page?.nextPageToken) list(current.folders, current.page.nextPageToken); }}>Next page</Button>}
        </>}
      </section>}
      {view.connectionId && !view.status.startsWith('Disconnected') && <section aria-label="Disconnect Google Drive">
        <p>Disconnect stops new imports and removes saved Google tokens. Previously imported documents remain in Documents.</p>
        <Checkbox checked={disconnect} disabled={busy} onChange={event => { setDisconnect(event.target.checked); }}>Stop this connection.</Checkbox>
        <Button danger disabled={busy || !disconnect} onClick={() => { void run(async signal => {
          const result = await api.googleDrive<View>('disconnect', { version: view.version }, signal);
          if (!validView(result, customer.tenantId, customer.customerId)) throw new Error('INVALID_CONNECTION');
          return { view: result, page: null, authorizationUrl: undefined };
        }); }}>Disconnect</Button>
      </section>}
      {view.status === 'Disconnected; Google revocation unconfirmed' && <Alert type="warning" title="Monergy has stopped this connection and erased its tokens. Revocation could not be confirmed. Review this app in your Google Account’s third-party connections to remove access there." />}
      {view.imports.length > 0 && <section aria-label="Import history"><h2>Imported documents</h2><ul>{view.imports.map(item =>
        <li key={item.documentVersionId}>{item.name} — {new Date(item.importedAt).toLocaleString()} — <a href={'/evidence?version=' + encodeURIComponent(item.documentVersionId)}>Open document</a></li>)}</ul></section>}
    </>}
  </main>;
}
