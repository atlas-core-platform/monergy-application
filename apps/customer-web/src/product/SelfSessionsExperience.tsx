import { Alert, Button, Card, Empty, Modal, Skeleton, Space, Tag } from 'antd';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import './product.css';

interface SessionItem {
  actorId: string; sessionReference: string; establishedAt: string; expiresAt: string;
  status: 'Active' | 'Ended' | 'Expired' | 'AccessChanged'; isCurrent: boolean;
}
interface SessionPage { tenantId: string; actorId: string; items: SessionItem[]; nextCursor: string | null }
interface View { api: AccessApi; scope: string; page: SessionPage | null; after?: string; loading: boolean; busy: boolean; uncertain: boolean; error: string; notice: string }
const reference = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
const date = (value: unknown): value is string => typeof value === 'string' && Number.isFinite(Date.parse(value));
function validPage(value: unknown, tenant: string, actor: string): value is SessionPage {
  if (!value || typeof value !== 'object') return false;
  const p = value as Partial<SessionPage>;
  if (p.tenantId !== tenant || p.actorId !== actor || !Array.isArray(p.items) || p.items.length > 100 ||
      !(p.nextCursor === null || reference(p.nextCursor) && p.items.length === 100 && p.nextCursor === p.items.at(-1)?.sessionReference)) return false;
  return p.items.every((x: SessionItem | null) => x?.actorId === actor && reference(x.sessionReference) && date(x.establishedAt) && date(x.expiresAt) &&
    Date.parse(x.expiresAt) > Date.parse(x.establishedAt) && ['Active', 'Ended', 'Expired', 'AccessChanged'].includes(x.status) &&
    typeof x.isCurrent === 'boolean' && (!x.isCurrent || x.status === 'Active')) &&
    new Set(p.items.map(x => x.sessionReference)).size === p.items.length && p.items.filter(x => x.isCurrent).length <= 1;
}
const statusLabel = { Active: 'Active', Ended: 'Ended', Expired: 'Expired', AccessChanged: 'Access changed' };
export default function SelfSessionsExperience() {
  const { api, session, authorization } = useWorkspaceSession();
  const tenant = session?.tenantId ?? ''; const actor = session?.actorId ?? '';
  const customer = authorization?.area === 'product' ? authorization.context.customerId : '';
  const scope = JSON.stringify([tenant, actor, customer, session?.authenticationContextId, session?.subjectVersion]);
  const [view, setView] = useState<View | null>(null);
  const [selection, setSelection] = useState<{ api: AccessApi; scope: string; item: SessionItem } | null>(null);
  const generation = useRef(0); const pending = useRef<AbortController | null>(null); const mutation = useRef(false);
  const current = view?.api === api && view.scope === scope ? view : null;
  const selected = selection?.api === api && selection.scope === scope && current?.page ? selection.item : null;
  const load = useCallback(async (after?: string) => {
    if (!api || !customer) return;
    pending.current?.abort(); const controller = new AbortController(); pending.current = controller;
    const id = ++generation.current;
    setSelection(null);
    setView({ api, scope, page: null, after, loading: true, busy: false, uncertain: false, error: '', notice: '' });
    try {
      const page = await api.selfSessions<unknown>(after, controller.signal);
      if (id !== generation.current || controller.signal.aborted) return;
      if (!validPage(page, tenant, actor)) throw new AccessApiError(503, 'SESSION_LIST_INVALID');
      setView({ api, scope, page, after, loading: false, busy: false, uncertain: false, error: '', notice: '' });
    } catch {
      if (id !== generation.current || controller.signal.aborted) return;
      setView({ api, scope, page: null, after, loading: false, busy: false, uncertain: true, error: 'Your sessions could not be verified. Reload when the service is available.', notice: '' });
    }
  }, [api, scope, customer, tenant, actor]);
  useEffect(() => {
    const start = window.setTimeout(() => { void load(); }, 0);
    return () => { window.clearTimeout(start); generation.current += 1; pending.current?.abort(); mutation.current = false; };
  }, [load]);
  const endSession = async () => {
    if (!api || !current?.page || !selected || selected.isCurrent || selected.status !== 'Active' || current.busy || current.uncertain || mutation.current) return;
    mutation.current = true;
    const target = selected.sessionReference; const operationId = crypto.randomUUID().replaceAll('-', '');
    pending.current?.abort(); const controller = new AbortController(); pending.current = controller;
    const id = ++generation.current; const isCurrent = () => id === generation.current && !controller.signal.aborted;
    setView({ ...current, busy: true, error: '', notice: '' });
    try {
      const receipt = await api.revokeSelfSession<{ operationId?: string; sessionReference?: string; revoked?: boolean }>(operationId, target, controller.signal);
      if (!isCurrent()) return;
      if (receipt.operationId !== operationId || receipt.sessionReference !== target || receipt.revoked !== true)
        throw new AccessApiError(503, 'SESSION_RECEIPT_INVALID');
      const page = await api.selfSessions<unknown>(current.after, controller.signal);
      if (!isCurrent()) return;
      if (!validPage(page, tenant, actor) || !page.items.some(x => x.sessionReference === target && x.status === 'Ended' && !x.isCurrent))
        throw new AccessApiError(503, 'SESSION_RELOAD_INVALID');
      setSelection(null);
      setView({ ...current, page, busy: false, uncertain: false, error: '', notice: 'The selected session has ended.' });
    } catch {
      if (!isCurrent()) return;
      setSelection(null);
      setView({ ...current, busy: false, uncertain: true, notice: '', error: 'The result is not confirmed. Reload your sessions before trying another change.' });
    } finally { if (isCurrent()) mutation.current = false; }
  };
  if (!api || !customer) return <Alert type="warning" showIcon title="Connect to your own customer workspace to review sessions." />;
  return <section className="product-page" aria-labelledby="self-sessions-title">
    <header className="product-page-header"><div><p className="workspace-eyebrow">SETTINGS</p><h1 id="self-sessions-title">Sessions</h1>
      <p>Review your session activity and end a session you no longer use. Device and location details are unavailable.</p></div>
      <Button disabled={!!current && (current.busy || current.loading)} onClick={() => { void load(current?.after); }}>Reload sessions</Button>
    </header>
    {current?.error && <Alert type="error" showIcon title={current.error} />}
    {current?.notice && <Alert type="success" showIcon title={current.notice} />}
    {!current || current.loading ? <Skeleton active /> : current.page && <>
      {current.page.items.length === 0 ? <Empty description="No sessions on this page." /> : <ul aria-label="Your sessions" style={{ listStyle: 'none', padding: 0 }}>
        {current.page.items.map(item => <li key={item.sessionReference}><Card size="small" title={`Started ${new Date(item.establishedAt).toLocaleString()}`} style={{ marginBottom: 12 }}>
          <Space wrap><Tag color={item.status === 'Active' ? 'green' : undefined}>{statusLabel[item.status]}</Tag>{item.isCurrent && <Tag color="blue">Current session</Tag>}</Space>
          <p>Expires {new Date(item.expiresAt).toLocaleString()}</p>
          {item.isCurrent ? <p>Use Sign out to end the session you are using now.</p> : item.status === 'Active' && <Button danger disabled={current.busy || current.uncertain} onClick={() => { setSelection({ api, scope, item }); }}>End session</Button>}
        </Card></li>)}
      </ul>}
      <Space>{current.after && <Button disabled={current.busy || current.uncertain} onClick={() => { void load(); }}>Latest sessions</Button>}
        {current.page.nextCursor && <Button disabled={current.busy || current.uncertain} onClick={() => { void load(current.page?.nextCursor ?? undefined); }}>Older sessions</Button>}
      </Space>
    </>}
    <Modal title="End this session?" open={!!selected} closable={!current?.busy} mask={{ closable: !current?.busy }}
      confirmLoading={current?.busy} okText="End selected session" cancelButtonProps={{ disabled: current?.busy }}
      okButtonProps={{ danger: true, disabled: current?.uncertain }} onCancel={() => { if (!current?.busy) setSelection(null); }} onOk={() => { void endSession(); }}>
      {selected && <p>The session started {new Date(selected.establishedAt).toLocaleString()} will lose access. Your current session stays connected.</p>}
    </Modal>
  </section>;
}
