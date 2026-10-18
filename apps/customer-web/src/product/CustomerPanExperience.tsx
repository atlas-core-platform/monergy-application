import { Alert, Button, Card, Checkbox, Input, Space, Spin } from 'antd';
import { useCallback, useEffect, useRef, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import type { AccessApi } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
interface PanView {
  tenantId: string; actorId: string; customerId: string; enabled: boolean; canSave: boolean;
  agreementRevision: string | null; maskedPan: string | null; version: number;
  verificationReference: string | null; updatedAt: string | null;
}
interface View { api: AccessApi; scope: string; value: PanView; busy: boolean; uncertain: boolean }
const reference = (v: unknown) => typeof v === 'string' && /^[0-9a-f]{64}$/.test(v);
function valid(value: unknown, tenant: string, actor: string, customer: string): value is PanView {
  if (!value || typeof value !== 'object') return false;
  const p = value as Partial<PanView>;
  return p.tenantId === tenant && p.actorId === actor && p.customerId === customer &&
    typeof p.enabled === 'boolean' && typeof p.canSave === 'boolean' &&
    (p.agreementRevision === null || reference(p.agreementRevision)) && (!p.canSave || p.enabled && reference(p.agreementRevision)) &&
    (p.maskedPan === null || typeof p.maskedPan === 'string' && /^\*{6}[0-9]{3}[A-Z]$/.test(p.maskedPan)) &&
    Number.isSafeInteger(p.version) && (p.version ?? -1) >= 0 &&
    (p.version === 0 ? p.maskedPan === null && p.verificationReference === null && p.updatedAt === null
      : reference(p.verificationReference) && typeof p.updatedAt === 'string' && Number.isFinite(Date.parse(p.updatedAt))) &&
    (p.enabled || !p.canSave && p.version === 0 && p.agreementRevision === null);
}
export default function CustomerPanExperience() {
  const { api, session, authorization } = useWorkspaceSession();
  const tenant = session?.tenantId ?? ''; const actor = session?.actorId ?? '';
  const customer = authorization?.area === 'product' ? authorization.context.customerId : '';
  const scope = JSON.stringify([tenant, actor, customer, session?.authenticationContextId, session?.subjectVersion]);
  const [open, setOpen] = useState(false); const [view, setView] = useState<View | null>(null);
  const [pan, setPan] = useState(''); const [confirm, setConfirm] = useState(false);
  const [mode, setMode] = useState<'view' | 'edit' | 'remove'>('view');
  const [error, setError] = useState(''); const [notice, setNotice] = useState('');
  const generation = useRef(0); const pending = useRef<AbortController | null>(null); const mutation = useRef(false);
  const current = view?.api === api && view.scope === scope ? view : null;
  const load = useCallback(async () => {
    if (!api || !customer) return;
    pending.current?.abort(); const controller = new AbortController(); pending.current = controller; const id = ++generation.current;
    setView(null); setPan(''); setConfirm(false); setNotice(''); setError('');
    try {
      const value = await api.selfCustomerPan<unknown>('GET', undefined, controller.signal);
      if (id !== generation.current || controller.signal.aborted) return;
      if (!valid(value, tenant, actor, customer)) throw new Error('invalid');
      setView({ api, scope, value, busy: false, uncertain: false }); setMode(value.maskedPan ? 'view' : 'edit');
    } catch {
      if (id !== generation.current || controller.signal.aborted) return;
      setError('Your PAN record could not be verified. Reload when the service is available.');
    }
  }, [api, customer, tenant, actor, scope]);
  useEffect(() => {
    if (!open) return;
    const start = window.setTimeout(() => { void load(); }, 0);
    return () => { window.clearTimeout(start); generation.current += 1; pending.current?.abort(); mutation.current = false; };
  }, [open, load]);
  const change = async () => {
    if (!api || !current || current.busy || current.uncertain || mutation.current || !confirm) return;
    const removing = mode === 'remove'; const normalized = pan.trim().toUpperCase();
    if (!removing && (!current.value.canSave || !/^[A-Z]{5}[0-9]{4}[A-Z]$/.test(normalized))) {
      setError('Enter a PAN with five letters, four digits and a final letter.'); return;
    }
    mutation.current = true; setPan(''); setConfirm(false); setError(''); setNotice(''); setView({ ...current, busy: true });
    pending.current?.abort(); const controller = new AbortController(); pending.current = controller; const id = ++generation.current;
    const active = () => id === generation.current && !controller.signal.aborted;
    try {
      const operationId = crypto.randomUUID().replaceAll('-', '');
      const receipt = await api.selfCustomerPan<{ operationId?: string; version?: number; verificationReference?: string }>('PUT', {
        operationId, expectedVersion: current.value.version, pan: removing ? null : normalized, confirm: true,
        agreementRevision: removing ? null : current.value.agreementRevision,
      }, controller.signal);
      if (!active()) return;
      if (receipt.operationId !== operationId || receipt.version !== current.value.version + 1 || !reference(receipt.verificationReference)) throw new Error('invalid');
      const value = await api.selfCustomerPan<unknown>('GET', undefined, controller.signal);
      if (!active()) return;
      if (!valid(value, tenant, actor, customer) || value.version !== receipt.version || value.verificationReference !== receipt.verificationReference ||
          value.maskedPan !== (removing ? null : '******' + normalized.slice(-4))) throw new Error('invalid');
      setView({ api, scope, value, busy: false, uncertain: false }); setMode(value.maskedPan ? 'view' : 'edit');
      setNotice(removing ? 'Your stored PAN has been removed.' : 'Your PAN has been saved.');
    } catch (failure) {
      if (!active()) return;
      setView({ ...current, busy: false, uncertain: true });
      setError(failure instanceof AccessApiError && failure.status === 409
        ? 'Your PAN record or account agreements changed. Reload before making another change.'
        : 'The PAN change could not be confirmed. Reload before making another change.');
    } finally { if (active()) mutation.current = false; }
  };
  if (!api || !customer) return null;
  if (!open) return <Button onClick={() => { setOpen(true); }}>Manage PAN</Button>;
  return <Card title="PAN">
    <Space><Button disabled={current?.busy} onClick={() => { void load(); }}>Reload PAN</Button>
      <Button disabled={current?.busy} onClick={() => { setOpen(false); setPan(''); setConfirm(false); setView(null); setError(''); setNotice(''); }}>Close PAN details</Button></Space>
    <p>Self-declared profile information. Identity and KYC verification are handled separately.</p>
    {error && <Alert showIcon type="error" title={error} />}{notice && <Alert showIcon type="success" title={notice} />}
    {!current ? !error && <Spin aria-label="Loading PAN" /> : !current.value.enabled ? <Alert showIcon type="info" title="PAN is unavailable in this environment." /> : <>
      <p>Last verified PAN: <strong>{current.value.maskedPan ?? 'Not recorded'}</strong></p>
      {!current.value.canSave && <Alert showIcon type="info" title="Review the current account agreements before recording or replacing a PAN."
        description={<a href="/settings/agreements">Review account agreements</a>} />}
      {mode === 'view' ? <Space><Button disabled={!current.value.canSave || current.busy || current.uncertain} onClick={() => { setMode('edit'); setConfirm(false); }}>Replace PAN</Button>
        <Button disabled={current.busy || current.uncertain} onClick={() => { setMode('remove'); setConfirm(false); }}>Remove PAN</Button></Space> :
      <form onSubmit={e => { e.preventDefault(); void change(); }}>
        {mode === 'edit' && <><label htmlFor="customer-pan-input">PAN</label><Input.Password id="customer-pan-input" value={pan} visibilityToggle={false} maxLength={10} autoComplete="off" spellCheck={false}
          disabled={current.busy || current.uncertain || !current.value.canSave} onChange={e => { setPan(e.target.value.toUpperCase()); setConfirm(false); setNotice(''); }} /></>}
        <Checkbox checked={confirm} disabled={current.busy || current.uncertain || mode === 'edit' && !current.value.canSave} onChange={e => { setConfirm(e.target.checked); }}>
          {mode === 'remove' ? 'I confirm that I want to remove the stored PAN.' : 'I confirm that this is my self-declared PAN and want to record it.'}</Checkbox>
        <Space><Button type="primary" htmlType="submit" loading={current.busy} disabled={!confirm || current.uncertain || mode === 'edit' && !current.value.canSave}>{mode === 'remove' ? 'Confirm PAN removal' : 'Save PAN'}</Button>
          {current.value.maskedPan && <Button disabled={current.busy} onClick={() => { setMode('view'); setPan(''); setConfirm(false); }}>Cancel PAN change</Button>}</Space>
      </form>}
    </>}
  </Card>;
}
