import { Alert, Button, Card, Checkbox, Skeleton, Space, Tag } from 'antd';
import { useCallback, useEffect, useRef, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import type { AccessApi } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import './product.css';

interface AgreementDocument { kind: string; version: string; title: string; body: string; sha256: string }
interface AgreementReceipt { operationId: string; revision: string; acceptedAt: string }
interface AgreementView {
  tenantId: string; actorId: string; customerId: string;
  publication: { revision: string; publishedAt: string; documents: AgreementDocument[] } | null;
  acceptance: AgreementReceipt | null;
}
interface View { api: AccessApi; scope: string; data: AgreementView | null; checked: string[]; saving: boolean; uncertain: boolean; error: string; notice: string }
const labels: Record<string, string> = {
  TERMS: 'I accept the displayed terms.', PRIVACY: 'I acknowledge the displayed privacy notice.', CONSENT: 'I consent to processing as described in the displayed consent notice.',
};
const hash = (v: unknown): v is string => typeof v === 'string' && /^[0-9a-f]{64}$/.test(v);
async function valid(value: unknown, tenant: string, actor: string, customer: string): Promise<boolean> {
  if (!value || typeof value !== 'object') return false;
  const v = value as Partial<AgreementView>;
  if (v.tenantId !== tenant || v.actorId !== actor || v.customerId !== customer) return false;
  if (v.publication === null) return v.acceptance === null;
  const p = v.publication;
  if (!p || !hash(p.revision) || !Number.isFinite(Date.parse(p.publishedAt)) || Date.parse(p.publishedAt) > Date.now() ||
    !Array.isArray(p.documents) || p.documents.length !== 3 || p.documents.map(d => d.kind).sort().join(',') !== 'CONSENT,PRIVACY,TERMS') return false;
  for (const d of p.documents) {
    if (typeof d.version !== 'string' || !/^[A-Za-z0-9_.-]{1,64}$/.test(d.version) ||
      typeof d.title !== 'string' || !d.title.trim() || d.title.length > 120 ||
      typeof d.body !== 'string' || !d.body.trim() || d.body.length > 32_000 || !hash(d.sha256)) return false;
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(d.body));
    if ([...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('') !== d.sha256) return false;
  }
  return v.acceptance === null || !!v.acceptance && /^[0-9a-f]{32}$/.test(v.acceptance.operationId) &&
    v.acceptance.revision === p.revision && Number.isFinite(Date.parse(v.acceptance.acceptedAt)) &&
    Date.parse(v.acceptance.acceptedAt) >= Date.parse(p.publishedAt);
}
export default function CustomerAccountAgreementsExperience() {
  const { api, session, authorization, refreshContext } = useWorkspaceSession();
  const customer = authorization?.area === 'product' ? authorization.context.customerId : '';
  const tenant = session?.tenantId ?? ''; const actor = session?.actorId ?? '';
  const scope = JSON.stringify([tenant, actor, customer, session?.authenticationContextId, session?.subjectVersion]);
  const [view, setView] = useState<View | null>(null); const generation = useRef(0); const pending = useRef<AbortController | null>(null);
  const current = view?.api === api && view.scope === scope ? view : null;
  const load = useCallback(async () => {
    if (!api || !customer) return;
    pending.current?.abort(); const controller = new AbortController(); pending.current = controller; const id = ++generation.current;
    try {
      const data = await api.selfAccountAgreements<AgreementView>('GET', undefined, controller.signal);
      if (!await valid(data, tenant, actor, customer)) throw new AccessApiError(503, 'AGREEMENTS_RESPONSE_INVALID');
      if (id !== generation.current || controller.signal.aborted) return;
      setView({ api, scope, data, checked: [], saving: false, uncertain: false, error: '', notice: '' });
    } catch {
      if (id !== generation.current || controller.signal.aborted) return;
      setView({ api, scope, data: null, checked: [], saving: false, uncertain: true, error: 'Your account agreements could not be verified. Reload when the service is available.', notice: '' });
    }
  }, [api, scope, customer, tenant, actor]);
  const invalidate = useCallback(() => { generation.current += 1; pending.current?.abort(); }, []);
  useEffect(() => { const start = window.setTimeout(() => { void load(); }, 0); return () => { window.clearTimeout(start); invalidate(); }; }, [load, invalidate]);
  const save = async () => {
    if (!api || !current?.data?.publication || current.data.acceptance || current.checked.length !== 3 || current.saving || current.uncertain) return;
    const revision = current.data.publication.revision; const operationId = crypto.randomUUID().replaceAll('-', '');
    pending.current?.abort(); const controller = new AbortController(); pending.current = controller; const id = ++generation.current;
    const same = () => id === generation.current && !controller.signal.aborted;
    setView({ ...current, saving: true, error: '', notice: '' });
    try {
      const receipt = await api.selfAccountAgreements<AgreementReceipt>('POST', {
        operationId, revision, acceptTerms: true, acknowledgePrivacy: true, consentToProcessing: true,
      }, controller.signal);
      if (!same()) return;
      if (receipt.operationId !== operationId || receipt.revision !== revision || !Number.isFinite(Date.parse(receipt.acceptedAt)))
        throw new AccessApiError(503, 'AGREEMENTS_RECEIPT_INVALID');
      const data = await api.selfAccountAgreements<AgreementView>('GET', undefined, controller.signal);
      if (!await valid(data, tenant, actor, customer) || data.acceptance?.operationId !== operationId ||
        data.acceptance.revision !== revision || data.acceptance.acceptedAt !== receipt.acceptedAt)
        throw new AccessApiError(503, 'AGREEMENTS_READBACK_INVALID');
      if (!same()) return;
      await refreshContext?.();
      if (!same()) return;
      setView({ ...current, data, checked: [], saving: false, uncertain: false, error: '', notice: 'Your agreement choices have been recorded.' });
    } catch (failure) {
      if (!same()) return;
      setView({ ...current, saving: false, uncertain: true, notice: '', error: failure instanceof AccessApiError && failure.status === 409
        ? 'The agreement information changed. Reload and review the current documents before continuing.'
        : 'The result is not confirmed. Reload your agreement record before trying again.' });
    }
  };
  if (!api || !customer) return <Alert showIcon type="warning" title="Connect to your own customer workspace to review account agreements." />;
  const data = current?.data;
  return <section className="product-page" aria-labelledby="account-agreements-title">
    <header className="product-page-header"><div><p className="workspace-eyebrow">YOUR ACCOUNT</p><h1 id="account-agreements-title">Account agreements</h1><p>Review the published documents and record your choices explicitly.</p></div>
      <Button disabled={current?.saving} onClick={() => { setView(null); void load(); }}>Reload agreements</Button></header>
    {current?.error && <Alert showIcon type="error" title={current.error} />}
    {current?.notice && <Alert showIcon type="success" title={current.notice} />}
    {!current ? <Skeleton active /> : data?.publication === null ? <Alert showIcon type="info" title="Account agreements have not been published for your organization." description="You can review and record your choices when the documents are available." /> : data?.publication && <>
      {data.acceptance && <Alert showIcon type="success" title="Current agreements recorded" description={`Recorded on ${new Date(data.acceptance.acceptedAt).toLocaleString()}.`} />}
      {data.publication.documents.map(d => <Card key={d.kind} title={d.title} extra={<Tag>Version {d.version}</Tag>}>
        <div tabIndex={0} role="region" aria-label={d.title} style={{ whiteSpace: 'pre-wrap', maxHeight: 280, overflow: 'auto', marginBottom: 16 }}>{d.body}</div>
        {!data.acceptance && <Checkbox checked={current.checked.includes(d.kind)} disabled={current.saving || current.uncertain}
          onChange={e => { setView({ ...current, checked: e.target.checked ? [...current.checked, d.kind] : current.checked.filter(k => k !== d.kind) }); }}>{labels[d.kind]}</Checkbox>}
      </Card>)}
      {!data.acceptance && <Space><Button type="primary" loading={current.saving} disabled={current.checked.length !== 3 || current.uncertain} onClick={() => { void save(); }}>Record agreement choices</Button></Space>}
    </>}
  </section>;
}
