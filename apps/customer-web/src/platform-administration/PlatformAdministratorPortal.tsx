import { Alert, Button, Card, Form, Input, Modal, Select, Space, Switch, Table, Tag, Timeline, Typography } from 'antd';
import { useCallback, useEffect, useState } from 'react';
import { FoundationProvider, WorkspaceShell } from '@monergy/ui-foundation';
import '../access/access.css';

type Posture = 'Normal' | 'Investigating' | 'Maintenance';
interface PlatformSession { actorId: string; authority: string; session: string; expiresAt: string }
interface PlatformContext { actorId: string; authority: string; environment: string; tenantAccess: boolean; customerDataAccess: boolean; breakGlassAvailable: boolean; productionAccepted: boolean }
interface Service { id: string; label: string; implementation: string; available: boolean }
interface ServicePosture { serviceId: string; posture: Posture; note: string; changedBy: string; changedAt: string; version: number }
interface Gate { id: string; state: string; detail: string }
interface Receipt { operationId: string; serviceId: string; posture: Posture; note: string; actorId: string; recordedAt: string; version: number }
interface Topology { services: Service[]; operationsRevision: number; postures: ServicePosture[] }
interface IntegrationProvider { code: string; displayName: string; category: string; enabled: boolean; scope: 'Platform' | 'Organization'; endpoint?: string; clientId?: string; callbackUri?: string; model?: string; options: Record<string, string>; secretReferences: string[]; subscriptionStatus: string; readiness: string; updatedAt: string }
interface Integrations { revision: number; providers: IntegrationProvider[] }

const request = async <T,>(path: string, session?: string, init?: RequestInit): Promise<T> => {
  const headers = new Headers(init?.headers);
  headers.set('Content-Type', 'application/json');
  if (session) headers.set('X-Monergy-Platform-Session', session);
  const response = await fetch(path, {
    ...init,
    cache: 'no-store',
    redirect: 'error',
    headers,
  });
  if (!response.ok) {
    await response.text().catch(() => '');
    throw new Error('We could not complete that action right now. Please try again.');
  }
  return response.status === 204 ? undefined as T : await response.json() as T;
};

export default function PlatformAdministratorPortal() {
  const [session, setSession] = useState<PlatformSession | null>(null);
  const [context, setContext] = useState<PlatformContext | null>(null);
  const [topology, setTopology] = useState<Topology | null>(null);
  const [gates, setGates] = useState<Gate[]>([]);
  const [receipts, setReceipts] = useState<Receipt[]>([]);
  const [integrations, setIntegrations] = useState<Integrations | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [selected, setSelected] = useState<Service | null>(null);
  const [selectedProvider, setSelectedProvider] = useState<IntegrationProvider | null>(null);
  const [form] = Form.useForm<{ email: string; password: string }>();
  const [activationForm] = Form.useForm<{ password: string; confirm: string }>();
  const [integrationForm] = Form.useForm<{ enabled: boolean; scope: 'Platform' | 'Organization'; tenantId?: string; endpoint?: string; clientId?: string; callbackUri?: string; model?: string; secretReferences?: string; subscriptionStatus: string }>();
  const [postureForm] = Form.useForm<{ posture: Posture; note: string }>();

  const load = useCallback(async (current: PlatformSession) => {
    const [verified, services, readiness, history, providerConfiguration] = await Promise.all([
      request<PlatformContext>('/platform-api/local/v1/context', current.session),
      request<Topology>('/platform-api/local/v1/topology', current.session),
      request<{ gates: Gate[] }>('/platform-api/local/v1/readiness', current.session),
      request<{ items: Receipt[] }>('/platform-api/local/v1/audit', current.session),
      request<Integrations>('/platform-api/local/v1/integrations', current.session),
    ]);
    if (verified.authority !== 'PlatformAdministrator' || verified.tenantAccess ||
        verified.customerDataAccess || verified.breakGlassAvailable || verified.productionAccepted)
      throw new Error('Your administrator access could not be confirmed. Sign in again.');
    setContext(verified);
    setTopology(services);
    setGates(readiness.gates);
    setReceipts(history.items);
    setIntegrations(providerConfiguration);
  }, []);

  useEffect(() => {
    if (!session) return;
    const expires = Date.parse(session.expiresAt);
    const timer = window.setTimeout(() => {
      setSession(null); setContext(null); setTopology(null);
      setError('Your platform session has ended. Connect again to continue.');
    }, Math.max(0, Math.min(expires - Date.now(), 2_147_483_647)));
    return () => { window.clearTimeout(timer); };
  }, [session]);

  const signIn = async ({ email, password }: { email: string; password: string }) => {
    setBusy(true); setError(''); form.setFieldValue('password', '');
    try {
      const established = await request<PlatformSession>('/platform-api/local/v1/sessions', undefined, {
        method: 'POST', body: JSON.stringify({ email: email.trim(), password }),
      });
      await load(established);
      setSession(established);
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Platform connection failed.'); }
    finally { setBusy(false); }
  };

  const activate = async ({ password }: { password: string; confirm: string }) => {
    const ticket = new URLSearchParams(window.location.search).get('ticket');
    if (!ticket) return;
    setBusy(true); setError('');
    try {
      await request('/platform-api/local/v1/credentials/activation/confirm', undefined, {
        method: 'POST', body: JSON.stringify({ ticket, password }),
      });
      window.history.replaceState(null, '', '/platform');
      activationForm.resetFields();
      setError('Password created. Sign in to open Monergy Administration.');
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Account activation failed.'); }
    finally { setBusy(false); }
  };

  const signOut = async () => {
    if (session) await request<undefined>('/platform-api/local/v1/sessions/current', session.session, { method: 'DELETE' }).catch(() => undefined);
    setSession(null); setContext(null); setTopology(null); setGates([]); setReceipts([]);
  };

  const savePosture = async (values: { posture: Posture; note: string }) => {
    if (!session || !topology || !selected) return;
    setBusy(true); setError('');
    try {
      await request('/platform-api/local/v1/services/' + encodeURIComponent(selected.id) + '/posture', session.session, {
        method: 'POST',
        body: JSON.stringify({
          operationId: crypto.randomUUID().replaceAll('-', ''),
          expectedVersion: topology.operationsRevision,
          posture: values.posture,
          note: values.note.trim(),
        }),
      });
      await load(session);
      setSelected(null);
      postureForm.resetFields();
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'The posture could not be recorded.'); }
    finally { setBusy(false); }
  };

  const saveProvider = async (values: { enabled: boolean; scope: 'Platform' | 'Organization'; tenantId?: string; endpoint?: string; clientId?: string; callbackUri?: string; model?: string; secretReferences?: string; subscriptionStatus: string }) => {
    if (!session || !integrations || !selectedProvider) return;
    setBusy(true); setError('');
    try {
      await request('/platform-api/local/v1/integrations/' + encodeURIComponent(selectedProvider.code), session.session, {
        method: 'PUT', body: JSON.stringify({
          operationId: crypto.randomUUID().replaceAll('-', ''), expectedRevision: integrations.revision,
          enabled: values.enabled, scope: values.scope, endpoint: values.endpoint?.trim() ?? null,
          clientId: values.clientId?.trim() ?? null, callbackUri: values.callbackUri?.trim() ?? null,
          model: values.model?.trim() ?? null,
          options: values.scope === 'Organization' && values.tenantId ? { tenantId: values.tenantId.trim() } : {},
          secretReferences: values.secretReferences?.split(',').map(value => value.trim()).filter(Boolean) ?? [],
          subscriptionStatus: values.subscriptionStatus,
        }),
      });
      await load(session); setSelectedProvider(null); integrationForm.resetFields();
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'The provider configuration could not be saved.'); }
    finally { setBusy(false); }
  };

  if (!session || !context || !topology) return (
    <FoundationProvider appearance="midnight">
      <main className="access-entry">
        <header className="access-public-header"><span className="access-wordmark">monergy</span><Tag>Administration</Tag></header>
        <section className="access-public-body">
          <div className="access-public-intro">
            <p className="workspace-eyebrow">PLATFORM ADMINISTRATION</p>
            <h1>Understand Monergy.<br />Keep it ready.</h1>
            <p>Review system status and release blockers, then record an operational note. Organization and customer information is not available here.</p>
          </div>
          <Card className="access-connect-card" title={new URLSearchParams(window.location.search).has('ticket') ? 'Create your Platform Administrator password' : 'Open Monergy Administration'}>
            {error && <Alert type="error" showIcon title={error} className="access-notice" />}
            {new URLSearchParams(window.location.search).has('ticket') ? <Form form={activationForm} layout="vertical" onFinish={(values) => { void activate(values); }}>
              <Form.Item name="password" label="Password (15–128 characters)" rules={[{ required: true }, { min: 15 }, { max: 128 }]}><Input.Password autoComplete="new-password" disabled={busy} /></Form.Item>
              <Form.Item name="confirm" label="Confirm password" dependencies={['password']} rules={[{ required: true }, ({ getFieldValue }) => ({ validator(_, value) { return value === getFieldValue('password') ? Promise.resolve() : Promise.reject(new Error('Passwords do not match.')); } })]}><Input.Password autoComplete="new-password" disabled={busy} /></Form.Item>
              <Button type="primary" block htmlType="submit" loading={busy}>Create password</Button>
            </Form> : <Form form={form} layout="vertical" initialValues={{ email: 'a950.platform@example.test' }} onFinish={(values) => { void signIn(values); }}>
              <Form.Item name="email" label="Email" rules={[{ required: true }, { type: 'email' }]}><Input autoComplete="username" disabled={busy} /></Form.Item>
              <Form.Item name="password" label="Password" rules={[{ required: true }]}><Input.Password autoComplete="current-password" disabled={busy} /></Form.Item>
              <Button type="primary" block htmlType="submit" loading={busy}>Sign in</Button>
            </Form>}
            <p className="access-connect-footnote">Platform credentials cannot be used to enter an organization's workspace.</p>
          </Card>
        </section>
      </main>
    </FoundationProvider>
  );

  const postureByService = new Map(topology.postures.map(item => [item.serviceId, item]));
  return (
    <FoundationProvider appearance="midnight">
      <WorkspaceShell appearance="midnight" context="Platform Administrator" area="Administration"
        module={{ name: 'Monergy Administration', homeHref: '/platform', guide: <p>Review system status and operational notes. Customer and organization information is not available here.</p> }}
        destinations={[{ id: 'platform', label: 'System status', icon: 'layers', href: '/platform' }]}
        active="platform" onNavigate={() => { return undefined; }}
        actions={<Button type="text" onClick={() => { void signOut(); }}>Sign out</Button>}>
        <main className="access-page">
          <header className="access-page-header"><div><p className="workspace-eyebrow">MONERGY ADMINISTRATION · {context.environment.toUpperCase()}</p><h1>System status</h1><p>Availability, release blockers and operational notes.</p></div><Button onClick={() => { void load(session).catch((failure: unknown) => { setError(failure instanceof Error ? failure.message : 'We could not refresh the system status.'); }); }}>Refresh</Button></header>
          <Alert type="info" showIcon className="access-notice" title="Separate from customer work" description="This workspace cannot open organization or customer information, sign in as another person or change their access." />
          {error && <Alert type="error" showIcon title={error} className="access-notice" />}
          <Table<Service> rowKey="id" dataSource={topology.services} pagination={false} scroll={{ x: 820 }} columns={[
            { title: 'Area', dataIndex: 'label' },
            { title: 'Status', render: (_, row) => <Tag color={row.available ? 'success' : 'error'}>{row.available ? 'Available' : 'Needs attention'}</Tag> },
            { title: 'Operational note', render: (_, row) => { const posture = postureByService.get(row.id); return posture ? <Space orientation="vertical" size={0}><Tag>{posture.posture}</Tag><Typography.Text type="secondary">{posture.note}</Typography.Text></Space> : <Tag>Normal</Tag>; } },
            { title: 'Action', render: (_, row) => <Button size="small" onClick={() => { setSelected(row); postureForm.setFieldsValue({ posture: postureByService.get(row.id)?.posture ?? 'Normal', note: postureByService.get(row.id)?.note ?? '' }); }}>Add note</Button> },
          ]} />
          <section className="access-service-list"><h2>Release readiness</h2>{gates.map(gate => <Card key={gate.id} size="small"><Tag color="warning">{gate.state}</Tag><p>{gate.detail}</p></Card>)}</section>
          <section className="access-operation-page"><h2>Integrations &amp; Providers</h2><p>Configure non-secret provider metadata and protected secret-reference names. Readiness does not establish provider entitlement, legal approval or Production acceptance.</p>
            <Table<IntegrationProvider> rowKey="code" dataSource={integrations?.providers ?? []} pagination={false} columns={[
              { title: 'Provider', render: (_, row) => <><strong>{row.displayName}</strong><br /><Typography.Text type="secondary">{row.category}</Typography.Text></> },
              { title: 'Scope', dataIndex: 'scope' },
              { title: 'Readiness', render: (_, row) => <Tag color={row.enabled && row.readiness.startsWith('Configured') ? 'success' : 'default'}>{row.readiness}</Tag> },
              { title: 'Secret references', render: (_, row) => row.secretReferences.length ? row.secretReferences.join(', ') : 'None' },
              { title: 'Action', render: (_, row) => <Button size="small" onClick={() => { setSelectedProvider(row); integrationForm.setFieldsValue({ enabled: row.enabled, scope: row.scope, tenantId: row.options.tenantId, endpoint: row.endpoint, clientId: row.clientId, callbackUri: row.callbackUri, model: row.model, secretReferences: row.secretReferences.join(', '), subscriptionStatus: row.subscriptionStatus }); }}>Configure</Button> },
            ]} />
          </section>
          <section className="access-operation-page"><h2>Operational history</h2>{receipts.length ? <Timeline items={receipts.map(item => ({ key: item.operationId, title: `${topology.services.find(service => service.id === item.serviceId)?.label ?? 'Monergy area'} · ${item.posture}`, content: <p>{item.note}<br /><small>{new Date(item.recordedAt).toLocaleString()}</small></p> }))} /> : <p>No operational notes have been recorded.</p>}</section>
          <Modal open={selected !== null} title={`Add operational note · ${selected?.label ?? ''}`} onCancel={() => { setSelected(null); }} footer={null} destroyOnHidden>
            <p>This records an operational note. It does not restart anything, grant access or change customer information.</p>
            <Form form={postureForm} layout="vertical" onFinish={(values) => { void savePosture(values); }}>
              <Form.Item name="posture" label="Status" rules={[{ required: true }]}><Select options={['Normal', 'Investigating', 'Maintenance'].map(value => ({ value, label: value }))} /></Form.Item>
              <Form.Item name="note" label="Operational note" rules={[{ required: true, whitespace: true }, { max: 500 }]}><Input.TextArea rows={4} /></Form.Item>
              <Button type="primary" htmlType="submit" loading={busy}>Save note</Button>
            </Form>
          </Modal>
          <Modal open={selectedProvider !== null} title={`Configure ${selectedProvider?.displayName ?? ''}`} onCancel={() => { setSelectedProvider(null); }} footer={null} destroyOnHidden>
            <Alert type="info" showIcon title="Secrets stay outside responses and audit" description="Enter reference names only. Secret values remain in the protected runtime store and are never returned to this browser." />
            <Form form={integrationForm} layout="vertical" onFinish={(values) => { void saveProvider(values); }}>
              <Form.Item name="enabled" label="Enabled" valuePropName="checked"><Switch /></Form.Item>
              <Form.Item name="scope" label="Scope" rules={[{ required: true }]}><Select options={['Platform', 'Organization'].map(value => ({ value, label: value }))} /></Form.Item>
              <Form.Item name="tenantId" label="Organization identifier (organization scope only)"><Input /></Form.Item>
              <Form.Item name="endpoint" label="Endpoint"><Input /></Form.Item><Form.Item name="clientId" label="Safe client identifier"><Input /></Form.Item>
              <Form.Item name="callbackUri" label="Callback URL"><Input /></Form.Item><Form.Item name="model" label="Model"><Input /></Form.Item>
              <Form.Item name="secretReferences" label="Secret reference names (comma separated)"><Input autoComplete="off" /></Form.Item>
              <Form.Item name="subscriptionStatus" label="Subscription status" rules={[{ required: true }]}><Select options={['Not configured', 'Configured', 'Pending', 'Unavailable'].map(value => ({ value, label: value }))} /></Form.Item>
              <Button type="primary" htmlType="submit" loading={busy}>Save configuration</Button>
            </Form>
          </Modal>
        </main>
      </WorkspaceShell>
    </FoundationProvider>
  );
}
