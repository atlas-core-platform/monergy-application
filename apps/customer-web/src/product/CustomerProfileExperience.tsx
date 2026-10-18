import { Alert, Button, Card, Descriptions, Form, Input, Skeleton, Space, Tag } from 'antd';
import { useCallback, useEffect, useRef, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import type { AccessApi } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import CustomerPanExperience from './CustomerPanExperience';
import './product.css';

interface CustomerProfile {
  tenantId: string; actorId: string; customerId: string;
  displayName: string; email: string; mobile: string | null; dateOfBirth: string | null;
  version: number; updatedAt: string | null; emailVerifiedAt: string | null;
}
interface ProfileDraft { displayName: string; mobile: string; dateOfBirth: string }
interface ProfileView {
  api: AccessApi; scope: string; profile: CustomerProfile | null;
  loading: boolean; saving: boolean; dirty: boolean; uncertain: boolean;
  error: string; notice: string;
}
const dateOrNull = (value: unknown) => value === null || typeof value === 'string' && Number.isFinite(Date.parse(value));
const todayUtc = () => new Date().toISOString().slice(0, 10);
function validBirthDate(value: unknown): value is string | null {
  if (value === null) return true;
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value) || value < '0001-01-01' || value > todayUtc()) return false;
  const parsed = new Date(value + 'T00:00:00Z');
  return Number.isFinite(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value;
}
function validProfile(value: unknown, tenant: string, actor: string, customer: string): value is CustomerProfile {
  if (!value || typeof value !== 'object') return false;
  const p = value as Partial<CustomerProfile>;
  return p.tenantId === tenant && p.actorId === actor && p.customerId === customer &&
    typeof p.displayName === 'string' && p.displayName.trim().length > 0 && p.displayName.length <= 120 &&
    typeof p.email === 'string' && p.email.length <= 320 && p.email.includes('@') &&
    (p.mobile === null || typeof p.mobile === 'string' && /^\+[1-9][0-9]{6,14}$/.test(p.mobile)) &&
    validBirthDate(p.dateOfBirth) && Number.isSafeInteger(p.version) && (p.version ?? -1) >= 0 && dateOrNull(p.updatedAt) && dateOrNull(p.emailVerifiedAt);
}
export default function CustomerProfileExperience() {
  const { api, session, authorization, refreshContext } = useWorkspaceSession();
  const customer = authorization?.area === 'product' ? authorization.context.customerId : '';
  const tenant = session?.tenantId ?? ''; const actor = session?.actorId ?? '';
  const scope = JSON.stringify([tenant, actor, customer, session?.authenticationContextId, session?.subjectVersion]);
  const [form] = Form.useForm<ProfileDraft>();
  const [view, setView] = useState<ProfileView | null>(null);
  const generation = useRef(0);
  const pending = useRef<AbortController | null>(null);
  const current = view?.api === api && view.scope === scope ? view : null;
  const load = useCallback(async () => {
    if (!api || !customer) return;
    pending.current?.abort();
    const controller = new AbortController(); pending.current = controller;
    const id = ++generation.current;
    try {
      const profile = await api.selfCustomerProfile<unknown>('GET', undefined, controller.signal);
      if (id !== generation.current || controller.signal.aborted) return;
      if (!validProfile(profile, tenant, actor, customer)) throw new AccessApiError(503, 'PROFILE_RESPONSE_INVALID');
      form.setFieldsValue({ displayName: profile.displayName, mobile: profile.mobile ?? '', dateOfBirth: profile.dateOfBirth ?? '' });
      setView({ api, scope, profile, loading: false, saving: false, dirty: false, uncertain: false, error: '', notice: '' });
    } catch {
      if (id !== generation.current || controller.signal.aborted) return;
      setView({ api, scope, profile: null, loading: false, saving: false, dirty: false, uncertain: true, error: 'Your profile could not be verified. Reload when the service is available.', notice: '' });
    }
  }, [api, scope, customer, tenant, actor, form]);
  useEffect(() => {
    const start = window.setTimeout(() => { void load(); }, 0);
    return () => { window.clearTimeout(start); generation.current += 1; pending.current?.abort(); };
  }, [load]);
  const save = async (values: ProfileDraft) => {
    if (!api || !current?.profile || current.saving || current.uncertain) return;
    const expected = current.profile.version;
    const operationId = crypto.randomUUID().replaceAll('-', '');
    pending.current?.abort();
    const controller = new AbortController(); pending.current = controller;
    const id = ++generation.current;
    const isCurrent = () => id === generation.current && !controller.signal.aborted;
    setView({ ...current, saving: true, error: '', notice: '' });
    try {
      const receipt = await api.selfCustomerProfile<{ operationId?: string; version?: number }>('PUT', {
        operationId, expectedVersion: expected, displayName: values.displayName.trim(), mobile: values.mobile.trim() || null, dateOfBirth: values.dateOfBirth || null,
      }, controller.signal);
      if (!isCurrent()) return;
      if (receipt.operationId !== operationId || receipt.version !== expected + 1)
        throw new AccessApiError(503, 'PROFILE_RECEIPT_INVALID');
      const profile = await api.selfCustomerProfile<unknown>('GET', undefined, controller.signal);
      if (!isCurrent()) return;
      if (!validProfile(profile, tenant, actor, customer) || profile.version < receipt.version || profile.dateOfBirth !== (values.dateOfBirth || null))
        throw new AccessApiError(503, 'PROFILE_RESPONSE_INVALID');
      await refreshContext?.();
      if (!isCurrent()) return;
      form.setFieldsValue({ displayName: profile.displayName, mobile: profile.mobile ?? '', dateOfBirth: profile.dateOfBirth ?? '' });
      setView({ api, scope, profile, loading: false, saving: false, dirty: false, uncertain: false, error: '', notice: 'Your profile has been saved.' });
    } catch (failure) {
      if (!isCurrent()) return;
      setView({ ...current, saving: false, dirty: true, uncertain: true, notice: '', error:
        failure instanceof AccessApiError && failure.status === 409
          ? 'Your profile changed since it was loaded. Reload the latest profile before saving again.'
          : 'The save result is not confirmed. Reload your profile before making another change.' });
    }
  };
  if (!api || !customer) return <Alert type="warning" showIcon title="Connect to your own customer workspace to open My Profile." />;
  return <section className="product-page" aria-labelledby="customer-profile-title">
    <header className="product-page-header"><div><p className="workspace-eyebrow">YOUR ACCOUNT</p><h1 id="customer-profile-title">My Profile</h1><p>Keep your name and contact details up to date.</p></div>
      <Button disabled={current?.saving} loading={!current && !!api} onClick={() => { setView(null); void load(); }}>{current?.dirty ? 'Discard changes and reload' : 'Reload profile'}</Button>
    </header>
    {current?.error && <Alert type="error" showIcon title={current.error} />}
    {current?.notice && <Alert type="success" showIcon title={current.notice} />}
    {!current || current.loading ? <Skeleton active /> : current.profile && <Card title="Your details">
      <Descriptions column={1} items={[
        { key: 'email', label: 'Registered email', children: current.profile.email },
        { key: 'verified', label: 'Email verification', children: current.profile.emailVerifiedAt
          ? <Tag color="green">Email verified</Tag> : <Tag>Verification not recorded</Tag> },
        { key: 'updated', label: 'Last profile update', children: current.profile.updatedAt
          ? new Date(current.profile.updatedAt).toLocaleString() : 'No profile changes recorded' },
      ]} />
      <Form<ProfileDraft> form={form} layout="vertical" disabled={current.saving || current.uncertain} onFinish={(values) => { void save(values); }}
        onValuesChange={(_: unknown, values: ProfileDraft) => { setView({ ...current, dirty: values.displayName.trim() !== current.profile?.displayName || (values.mobile.trim() || null) !== current.profile.mobile || (values.dateOfBirth || null) !== current.profile.dateOfBirth, notice: '' }); }}>
        <Form.Item name="displayName" label="Your name" rules={[{ required: true, whitespace: true, message: 'Enter your name.' }, { max: 120, message: 'Use 120 characters or fewer.' }]}><Input maxLength={120} autoComplete="name" /></Form.Item>
        <Form.Item name="mobile" label="Mobile number" extra="Optional contact detail. This number has not been verified for sign-in." rules={[{ pattern: /^\+[1-9][0-9]{6,14}$/, message: 'Include the country code, for example +919876543210.' }]}><Input maxLength={16} type="tel" autoComplete="tel" placeholder="+919876543210" /></Form.Item>
        <Form.Item name="dateOfBirth" label="Date of birth" extra="Optional, self-declared profile detail. This does not verify your identity or KYC. Clear the field to remove it."
          rules={[{ validator: (_, value: unknown) => validBirthDate(value === '' ? null : value) ? Promise.resolve() : Promise.reject(new Error('Enter a valid date of birth that is not in the future.')) }]}>
          <Input type="date" min="0001-01-01" max={todayUtc()} autoComplete="bday" />
        </Form.Item>
        <Space><Button type="primary" htmlType="submit" loading={current.saving} disabled={!current.dirty || current.uncertain}>Save profile</Button>
          {current.dirty && !current.uncertain && <Button onClick={() => { if (!current.profile) return; form.setFieldsValue({ displayName: current.profile.displayName, mobile: current.profile.mobile ?? '', dateOfBirth: current.profile.dateOfBirth ?? '' }); setView({ ...current, dirty: false, error: '', notice: '' }); }}>Discard changes</Button>}
        </Space>
      </Form>
    </Card>}
    {current?.profile && <CustomerPanExperience key={scope} />}
  </section>;
}
