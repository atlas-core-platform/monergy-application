import { Alert, Button, Checkbox, Form, Input, Modal, Select, Skeleton, Space } from 'antd';
import { useEffect, useRef, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import type { ConsentOverview } from './consentModels';
import { validOverview } from './consentModels';

interface SharingScope { id: string; label: string; description: string }
interface SharingOptions { tenantId: string; actorId: string; customerId: string; version: number; purpose: string; maximumDays: number; scopes: SharingScope[] }
interface SharingDraft { recipientEmail: string; capabilityIds: string[]; days: number }
interface SharingIntent { requestId: string; expectedVersion: number; recipientEmail: string; capabilityIds: string[]; expiresAt: string }
interface SharingReceipt { requestId: string; recipientEmail: string; receipt: { id: string; customerId: string; actorId: string; operation: string; version: number; evidenceReference: string } }
const identifier = (value: unknown): value is string => typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$/.test(value);
function validOptions(value: unknown, tenant: string, actor: string, customer: string): value is SharingOptions {
  if (!value || typeof value !== 'object') return false;
  const v = value as Partial<SharingOptions>;
  return v.tenantId === tenant && v.actorId === actor && v.customerId === customer && v.purpose === 'customer-advice' &&
    Number.isSafeInteger(v.version) && (v.version ?? 0) >= 1 && v.maximumDays === 90 && Array.isArray(v.scopes) && v.scopes.length > 0 && v.scopes.length <= 5 &&
    v.scopes.every((s: SharingScope | null) => s && identifier(s.id) && typeof s.label === 'string' && s.label.trim().length > 0 && s.label.length <= 120 && typeof s.description === 'string' && s.description.length <= 300) &&
    new Set(v.scopes.map(s => s.id)).size === v.scopes.length;
}
export function CustomerSharingDialog({ api, tenant, actor, customer, onClose, onVerified, onUncertain }: {
  api: AccessApi; tenant: string; actor: string; customer: string;
  onClose: () => void; onVerified: (overview: ConsentOverview, email: string) => void; onUncertain: () => void;
}) {
  const [options, setOptions] = useState<SharingOptions | null>(null);
  const [loadError, setLoadError] = useState(''); const [attempt, setAttempt] = useState(0);
  const [intent, setIntent] = useState<SharingIntent | null>(null); const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false); const [validation, setValidation] = useState('');
  const [draft, setDraft] = useState<SharingDraft>({ recipientEmail: '', capabilityIds: [], days: 7 });
  const active = useRef<AbortController | null>(null); const mutation = useRef(false);
  useEffect(() => {
    const controller = new AbortController(); active.current = controller;
    void api.customerSharing<unknown>(customer, undefined, controller.signal).then(value => {
      if (controller.signal.aborted) return;
      if (!validOptions(value, tenant, actor, customer)) throw new Error('SHARING_OPTIONS_INVALID');
      setOptions(value);
    }).catch(() => { if (!controller.signal.aborted) setLoadError('Sharing options could not be verified. Reload before continuing.'); });
    return () => { controller.abort(); active.current?.abort(); };
  }, [api, tenant, actor, customer, attempt]);
  const review = (draft: SharingDraft) => {
    if (!options || mutation.current) return;
    const email = draft.recipientEmail.trim().toLowerCase();
    if (!draft.capabilityIds.length || draft.capabilityIds.some(id => !options.scopes.some(s => s.id === id)) || ![1, 7, 30, 90].includes(draft.days)) return;
    setDraft({ ...draft, recipientEmail: email });
    setIntent({ requestId: crypto.randomUUID().replaceAll('-', ''), expectedVersion: options.version, recipientEmail: email,
      capabilityIds: [...draft.capabilityIds].sort(), expiresAt: new Date(Date.now() + draft.days * 86_400_000).toISOString() });
    setConfirmed(false); setValidation('');
  };
  const grant = async () => {
    if (!intent || !confirmed || mutation.current || busy) return;
    if (Date.parse(intent.expiresAt) <= Date.now()) { setValidation('The selected expiry has passed. Return to details and review again.'); return; }
    mutation.current = true; setBusy(true);
    active.current?.abort(); const controller = new AbortController(); active.current = controller;
    const current = () => !controller.signal.aborted && active.current === controller;
    try {
      const result = await api.customerSharing<SharingReceipt>(customer, intent, controller.signal);
      if (!current()) return;
      const receipt = result.receipt;
      if (result.requestId !== intent.requestId || result.recipientEmail !== intent.recipientEmail || !identifier(receipt.id) ||
          receipt.customerId !== customer || !identifier(receipt.actorId) || receipt.actorId === actor || receipt.operation !== 'Granted' ||
          receipt.version !== intent.expectedVersion + 1 || !receipt.evidenceReference)
        throw new AccessApiError(503, 'SHARING_RECEIPT_INVALID');
      const overview = await api.customerConsent<unknown>(customer, controller.signal);
      if (!current()) return;
      if (!validOverview(overview, customer) || overview.version < receipt.version || !overview.items.some(item =>
        item.id === receipt.id && item.actorId === receipt.actorId && item.revokedAt === null &&
        Date.parse(item.expiresAt) === Date.parse(intent.expiresAt) && Date.parse(item.expiresAt) > Date.now() &&
        [...item.capabilityIds].sort().join('|') === intent.capabilityIds.join('|') &&
        (item.recipientLabel == null || item.recipientLabel.toLowerCase() === intent.recipientEmail)))
        throw new AccessApiError(503, 'SHARING_READBACK_INVALID');
      onVerified(overview, intent.recipientEmail);
    } catch { if (current()) onUncertain(); }
    finally { if (current()) { mutation.current = false; setBusy(false); } }
  };
  return <Modal title="Share information" open destroyOnHidden width={620} footer={null} closable={!busy} mask={{ closable: !busy }} onCancel={() => { if (!busy) onClose(); }}>
    <p>This records your permission for customer advice. The recipient also needs the organization’s current permissions and customer assignment to access information.</p>
    {loadError ? <><Alert type="warning" showIcon title={loadError} /><Button onClick={() => { setOptions(null); setLoadError(''); setAttempt(n => n + 1); }}>Reload sharing options</Button></> : !options ? <Skeleton active /> : intent ? <>
      <p>Recipient: <strong>{intent.recipientEmail}</strong></p>
      <ul>{intent.capabilityIds.map(id => <li key={id}>{options.scopes.find(s => s.id === id)?.label}</li>)}</ul>
      <p>Expires: {new Date(intent.expiresAt).toLocaleString()}</p>
      {validation && <Alert type="warning" showIcon title={validation} />}
      <Checkbox checked={confirmed} disabled={busy} onChange={event => { setConfirmed(event.target.checked); }}>I recognize this recipient and agree to share the selected information until this expiry.</Checkbox>
      <div style={{ marginTop: 16 }}><Space><Button type="primary" disabled={!confirmed || busy} loading={busy} onClick={() => { void grant(); }}>Confirm sharing</Button>
        <Button disabled={busy} onClick={() => { setIntent(null); setConfirmed(false); }}>Back to details</Button></Space></div>
    </> : <Form<SharingDraft> layout="vertical" initialValues={draft} onFinish={review}>
      <Form.Item name="recipientEmail" label="Recipient email" extra="Enter the registered email of someone in your organization." rules={[{ required: true, type: 'email', max: 320, message: 'Enter a valid registered email.' }]}><Input type="email" maxLength={320} autoComplete="off" /></Form.Item>
      <Form.Item name="capabilityIds" label="What to share" rules={[{ type: 'array', min: 1, required: true, message: 'Choose at least one permission.' }]}><Checkbox.Group><Space orientation="vertical">{options.scopes.map(scope => <Checkbox value={scope.id} key={scope.id}><strong>{scope.label}</strong> — {scope.description}</Checkbox>)}</Space></Checkbox.Group></Form.Item>
      <Form.Item name="days" label="Sharing duration"><Select options={[1, 7, 30, 90].map(value => ({ value, label: `${String(value)} ${value === 1 ? 'day' : 'days'}` }))} /></Form.Item>
      <Space><Button type="primary" htmlType="submit">Review sharing</Button><Button onClick={onClose}>Cancel</Button></Space>
    </Form>}
  </Modal>;
}
