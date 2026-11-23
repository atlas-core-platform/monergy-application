import { Alert, Button, Card, Form, Input, Modal, Select, Space, Steps, Switch, Table, Tag, Timeline, Typography } from 'antd';
import { useCallback, useEffect, useState } from 'react';
import { FoundationProvider, WorkspaceShell } from '@monergy/ui-foundation';
import '../access/access.css';
import { prepareAdministratorWelcomeUrl } from './administratorWelcome';
import AskMonergyAction from '../assistant/AskMonergyAction';

type Posture = 'Normal' | 'Investigating' | 'Maintenance';
interface PlatformSession { actorId: string; authority: string; session: string; expiresAt: string }
interface PlatformContext { actorId: string; authority: string; environment: string; tenantAccess: boolean; customerDataAccess: boolean; breakGlassAvailable: boolean; productionAccepted: boolean }
interface Service { id: string; label: string; implementation: string; available: boolean }
interface ServicePosture { serviceId: string; posture: Posture; note: string; changedBy: string; changedAt: string; version: number }
interface Gate { id: string; state: string; detail: string }
interface Receipt { operationId: string; serviceId: string; posture: Posture; note: string; actorId: string; recordedAt: string; version: number }
interface Topology { services: Service[]; operationsRevision: number; postures: ServicePosture[] }
interface IntegrationProvider { code: string; displayName: string; category: string; enabled: boolean; scope: 'Platform' | 'Organization'; endpoint?: string; clientId?: string; callbackUri?: string; model?: string; options: Record<string, string>; secretConfigured: boolean; secretUpdatedAt?: string; subscriptionStatus: string; readiness: string; updatedAt: string }
interface Integrations { revision: number; providers: IntegrationProvider[] }
interface Organization { organizationCode: string; organizationId: string; organizationName: string; displayName: string; tenantSlug: string; hostname: string; tenantUrl: string; endpointStatus: string; country: string; timeZone: string; initialAdministrator: string; status: 'Requested' | 'Provisioning' | 'ProvisioningFailed' | 'ReadyForAdmin' | 'Active'; created: string; updated: string; readiness: string; attention?: string; completedSteps: number[] }
interface Organizations { items: Organization[] }
interface WelcomeReceipt { welcomeUrl: string; expiresAt: string; email: string }
interface DiagnosticSessionView { id: string; reason: string; startedBy: string; startedAt: string; expiresAt: string; stoppedAt?: string; active: boolean; evidenceCount: number }

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
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [active, setActive] = useState<'platform' | 'organizations' | 'diagnostics'>('platform');
  const [diagnostics, setDiagnostics] = useState<DiagnosticSessionView[]>([]);
  const [diagnosis, setDiagnosis] = useState<Record<string, unknown> | null>(null);
  const [diagnosticForm] = Form.useForm<{ reason: string; durationMinutes: number }>();
  const [createOrganization, setCreateOrganization] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [selected, setSelected] = useState<Service | null>(null);
  const [selectedProvider, setSelectedProvider] = useState<IntegrationProvider | null>(null);
  const [form] = Form.useForm<{ email: string; password: string }>();
  const [activationForm] = Form.useForm<{ password: string; confirm: string }>();
  const [integrationForm] = Form.useForm<{ enabled: boolean; scope: 'Platform' | 'Organization'; tenantId?: string; endpoint?: string; clientId?: string; callbackUri?: string; model?: string; providerLabel?: string; notice?: string; secretValue?: string; clearSecret: boolean; subscriptionStatus: string }>();
  const [postureForm] = Form.useForm<{ posture: Posture; note: string }>();
  const [organizationForm] = Form.useForm<{ organizationName: string; displayName: string; tenantSlug?: string; country: string; timeZone: string; initialAdministratorEmail: string; placement: 'Shared' }>();
  const endAssistantSession = useCallback(() => {
    setSession(null); setContext(null); setTopology(null); setGates([]); setReceipts([]);
    setError('Your platform session has ended. Sign in again to continue.');
  }, []);

  const load = useCallback(async (current: PlatformSession) => {
    const [verified, services, readiness, history, providerConfiguration, organizationDirectory] = await Promise.all([
      request<PlatformContext>('/platform-api/local/v1/context', current.session),
      request<Topology>('/platform-api/local/v1/topology', current.session),
      request<{ gates: Gate[] }>('/platform-api/local/v1/readiness', current.session),
      request<{ items: Receipt[] }>('/platform-api/local/v1/audit', current.session),
      request<Integrations>('/platform-api/local/v1/integrations', current.session),
      request<Organizations>('/platform-api/local/v1/organizations', current.session),
    ]);
    if (verified.authority !== 'PlatformAdministrator' || verified.tenantAccess ||
        verified.customerDataAccess || verified.breakGlassAvailable || verified.productionAccepted)
      throw new Error('Your administrator access could not be confirmed. Sign in again.');
    setContext(verified);
    setTopology(services);
    setGates(readiness.gates);
    setReceipts(history.items);
    setIntegrations(providerConfiguration);
    setOrganizations(organizationDirectory.items);
  }, []);

  useEffect(() => {
    if (!session || active !== 'diagnostics') return;
    void request<{ items: DiagnosticSessionView[] }>('/platform-api/local/v1/diagnostics/sessions', session.session)
      .then(value => setDiagnostics(value.items))
      .catch(() => setError('Diagnostics could not be loaded.'));
  }, [session, active]);

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
      window.history.replaceState(null, '', '/');
      activationForm.resetFields();
      setError('Password created. Sign in to open Monergy Administration.');
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Account activation failed.'); }
    finally { setBusy(false); }
  };

  const signOut = async () => {
    if (session) await request<undefined>('/platform-api/local/v1/sessions/current', session.session, { method: 'DELETE' }).catch(() => undefined);
    setSession(null); setContext(null); setTopology(null); setGates([]); setReceipts([]);
  };

  const refreshDiagnostics = async () => {
    if (!session) return;
    const value = await request<{ items: DiagnosticSessionView[] }>('/platform-api/local/v1/diagnostics/sessions', session.session);
    setDiagnostics(value.items);
  };

  const startDiagnostic = async (values: { reason: string; durationMinutes: number }) => {
    if (!session) return;
    setBusy(true); setError(''); setDiagnosis(null);
    try {
      await request('/platform-api/local/v1/diagnostics/sessions', session.session, {
        method: 'POST', body: JSON.stringify({ reason: values.reason.trim(), durationMinutes: values.durationMinutes }),
      });
      diagnosticForm.resetFields();
      await refreshDiagnostics();
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Diagnostic mode could not be started.'); }
    finally { setBusy(false); }
  };

  const stopDiagnostic = async (id: string) => {
    if (!session) return;
    setBusy(true); setError('');
    try {
      await request('/platform-api/local/v1/diagnostics/sessions/' + encodeURIComponent(id) + '/stop', session.session, { method: 'POST' });
      await refreshDiagnostics();
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Diagnostic mode could not be stopped.'); }
    finally { setBusy(false); }
  };

  const diagnose = async (id: string) => {
    if (!session) return;
    setBusy(true); setError('');
    try { setDiagnosis(await request<Record<string, unknown>>('/platform-api/local/v1/diagnostics/sessions/' + encodeURIComponent(id) + '/diagnosis', session.session)); }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'Diagnosis is unavailable.'); }
    finally { setBusy(false); }
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

  const saveProvider = async (values: { enabled: boolean; scope: 'Platform' | 'Organization'; tenantId?: string; endpoint?: string; clientId?: string; callbackUri?: string; model?: string; providerLabel?: string; notice?: string; secretValue?: string; clearSecret: boolean; subscriptionStatus: string }) => {
    if (!session || !integrations || !selectedProvider) return;
    setBusy(true); setError('');
    try {
      await request('/platform-api/local/v1/integrations/' + encodeURIComponent(selectedProvider.code), session.session, {
        method: 'PUT', body: JSON.stringify({
          operationId: crypto.randomUUID().replaceAll('-', ''), expectedRevision: integrations.revision,
          enabled: values.enabled, scope: values.scope, endpoint: values.endpoint?.trim() ?? null,
          clientId: values.clientId?.trim() ?? null, callbackUri: values.callbackUri?.trim() ?? null,
          model: values.model?.trim() ?? null,
          options: {
            ...(values.scope === 'Organization' && values.tenantId ? { tenantId: values.tenantId.trim() } : {}),
            ...(selectedProvider.code === 'ai' && values.providerLabel?.trim() ? { providerLabel: values.providerLabel.trim() } : {}),
            ...(selectedProvider.code === 'ai' && values.notice?.trim() ? { notice: values.notice.trim() } : {}),
          },
          secretValue: values.secretValue ?? null, clearSecret: values.clearSecret,
          subscriptionStatus: values.subscriptionStatus,
        }),
      });
      await load(session); setSelectedProvider(null); integrationForm.resetFields();
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'The provider configuration could not be saved.'); }
    finally { setBusy(false); }
  };

  const submitOrganization = async (values: { organizationName: string; displayName: string; tenantSlug?: string; country: string; timeZone: string; initialAdministratorEmail: string; placement: 'Shared' }) => {
    if (!session) return;
    setBusy(true); setError('');
    try {
      await request('/platform-api/local/v1/organizations', session.session, {
        method: 'POST', body: JSON.stringify({
          requestId: crypto.randomUUID().replaceAll('-', ''),
          organizationName: values.organizationName.trim(), displayName: values.displayName.trim(),
          tenantSlug: values.tenantSlug?.trim() ?? null,
          country: values.country, timeZone: values.timeZone,
          initialAdministratorEmail: values.initialAdministratorEmail.trim().toLowerCase(), placement: values.placement,
        }),
      });
      await load(session); setCreateOrganization(false); organizationForm.resetFields(); setActive('organizations');
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'The organization could not be created.'); }
    finally { setBusy(false); }
  };

  const openAdministratorSetup = async (organization: Organization) => {
    if (!session) return;
    setBusy(true); setError('');
    try {
      const receipt = await request<WelcomeReceipt>('/platform-api/local/v1/organizations/' +
        encodeURIComponent(organization.organizationCode) + '/administrator-welcome', session.session, { method: 'POST' });
      const welcome = prepareAdministratorWelcomeUrl(receipt, organization, window.location);
      window.location.assign(welcome);
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'The administrator setup page is unavailable.'); }
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
  const onboardingOrganization =
    organizations.find((item) => item.status !== 'Active') ?? organizations[0];
  return (
    <FoundationProvider appearance="midnight">
      <WorkspaceShell appearance="midnight" context="Platform Administrator" area="Administration"
        module={{ name: 'Monergy Administration', homeHref: '/platform', guide: <p>Review system status and operational notes. Customer and organization information is not available here.</p> }}
        destinations={[{ id: 'platform', label: 'System status', icon: 'layers', href: '/' }, { id: 'diagnostics', label: 'Diagnostics', icon: 'warning', href: '/' }, { id: 'organizations', label: 'Organizations', icon: 'users', href: '/' }]}
        active={active} onNavigate={(id) => { if (id === 'platform' || id === 'organizations' || id === 'diagnostics') setActive(id); }}
        actions={<Space><AskMonergyAction key={session.session + ':' + JSON.stringify(context) + ':' + active}
          session={{ audience: 'platform', credential: session.session, expiresAt: session.expiresAt }}
          onSessionEnded={endAssistantSession} />
          <Button type="text" onClick={() => { void signOut(); }}>Sign out</Button></Space>}>
        <main className="access-page">
          {active === 'organizations' ? <>
            <header className="access-page-header"><div><p className="workspace-eyebrow">MONERGY ADMINISTRATION · {context.environment.toUpperCase()}</p><h1>Organizations</h1><p>Create an organization, follow secure provisioning and hand account setup to its first administrator.</p></div><Space><Button onClick={() => { void load(session).catch((failure: unknown) => { setError(failure instanceof Error ? failure.message : 'We could not refresh organizations.'); }); }}>Refresh</Button><Button type="primary" onClick={() => { setCreateOrganization(true); organizationForm.setFieldsValue({ country: 'IN', timeZone: 'Asia/Calcutta', placement: 'Shared' }); }}>Create organization</Button></Space></header>
            <Alert type="info" showIcon className="access-notice" title="Platform authority stays separate" description="You can provision an organization and invite its first administrator. You cannot set their password, enter Organization Settings, impersonate them or open customer information." />
            {error && <Alert type="error" showIcon title={error} className="access-notice" />}
            <Table<Organization> rowKey="organizationCode" dataSource={organizations} pagination={false} scroll={{ x: 1050 }} columns={[
              { title: 'Organization', render: (_, row) => <><strong>{row.organizationName}</strong><br /><Typography.Text type="secondary">Organization code: {row.organizationCode}</Typography.Text><br /><a href={row.tenantUrl}>{row.hostname}</a></> },
              { title: 'Location', render: (_, row) => <>{row.country}<br /><Typography.Text type="secondary">{row.timeZone}</Typography.Text></> },
              { title: 'Initial administrator', dataIndex: 'initialAdministrator' },
              { title: 'Created', render: (_, row) => new Date(row.created).toLocaleString() },
              { title: 'Status', render: (_, row) => <Space orientation="vertical" size={0}><Tag color={row.status === 'Active' ? 'success' : row.status === 'ProvisioningFailed' ? 'error' : row.status === 'ReadyForAdmin' ? 'processing' : 'warning'}>{row.status === 'ReadyForAdmin' ? 'Ready for Administrator' : row.status}</Tag><Typography.Text type="secondary">{row.status === 'ReadyForAdmin' ? 'Waiting for the Organization Administrator to complete setup' : row.readiness}</Typography.Text>{row.attention && <Typography.Text type="danger">Needs attention</Typography.Text>}</Space> },
              { title: 'Action', render: (_, row) => row.status === 'ReadyForAdmin' ? <Button size="small" type="primary" loading={busy} onClick={() => { void openAdministratorSetup(row); }}>Open administrator setup page</Button> : row.status === 'ProvisioningFailed' ? <Button size="small" loading={busy} onClick={() => { void request('/platform-api/local/v1/organizations/' + encodeURIComponent(row.organizationCode) + '/resume', session.session, { method: 'POST' }).then(() => load(session)).catch((failure: unknown) => { setError(failure instanceof Error ? failure.message : 'Provisioning could not resume.'); }); }}>Try again</Button> : null },
            ]} />
            <section className="access-operation-page"><h2>Provisioning stages{onboardingOrganization ? ` · ${onboardingOrganization.organizationName}` : ''}</h2><Steps orientation="vertical" size="small" items={[
              { title: 'Preparing organization', step: 1 }, { title: 'Registering organization address', step: 7 },
              { title: 'Creating secure data stores', step: 2 }, { title: 'Applying required setup', step: 3 },
              { title: 'Creating the first administrator', step: 4 }, { title: 'Setting up organization access', step: 5 },
              { title: 'Checking readiness', step: 6 },
            ].map(item => ({ title: item.title, status: onboardingOrganization?.completedSteps.includes(item.step) ? 'finish' : 'wait' }))} /></section>
          </> : active === 'diagnostics' ? <>
            <header className="access-page-header"><div><p className="workspace-eyebrow">MONERGY ADMINISTRATION · {context.environment.toUpperCase()}</p><h1>Diagnostics</h1><p>Open a short, audited diagnostic window and analyze only sanitized operational evidence.</p></div><Button onClick={() => { void refreshDiagnostics(); }}>Refresh</Button></header>
            <Alert type="warning" showIcon className="access-notice" title="Diagnostic Mode does not grant customer access" description="Diagnostic sessions are Platform Administrator-only, expire automatically, and accept bounded telemetry metadata only. Credentials, tokens, document payloads and unnecessary financial data are not diagnostic evidence." />
            {error && <Alert type="error" showIcon title={error} className="access-notice" />}
            <Card title="Start Diagnostic Mode">
              <Form form={diagnosticForm} layout="vertical" initialValues={{ durationMinutes: 15 }} onFinish={(values) => { void startDiagnostic(values); }}>
                <Form.Item name="reason" label="Reason" rules={[{ required: true, whitespace: true }, { min: 8 }, { max: 256 }]}><Input placeholder="Example: Investigate repeated provider timeout" /></Form.Item>
                <Form.Item name="durationMinutes" label="Window" rules={[{ required: true }]}><Select options={[5, 10, 15, 20, 30].map(value => ({ value, label: value + ' minutes' }))} /></Form.Item>
                <Button type="primary" htmlType="submit" loading={busy}>Start Diagnostic Mode</Button>
              </Form>
            </Card>
            <Table<DiagnosticSessionView> rowKey="id" dataSource={diagnostics} pagination={false} columns={[
              { title: 'Session', render: (_, row) => <><strong>{row.id}</strong><br /><Typography.Text type="secondary">{row.reason}</Typography.Text></> },
              { title: 'Window', render: (_, row) => <>{new Date(row.startedAt).toLocaleString()}<br /><Typography.Text type="secondary">Expires {new Date(row.expiresAt).toLocaleString()}</Typography.Text></> },
              { title: 'Evidence', render: (_, row) => row.evidenceCount },
              { title: 'State', render: (_, row) => <Tag color={row.active ? 'processing' : 'default'}>{row.active ? 'Active' : 'Closed'}</Tag> },
              { title: 'Action', render: (_, row) => <Space><Button size="small" onClick={() => { void diagnose(row.id); }}>Analyze</Button>{row.active && <Button size="small" danger onClick={() => { void stopDiagnostic(row.id); }}>Stop</Button>}</Space> },
            ]} />
            {diagnosis && <Card title="System Expert diagnosis"><pre style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{JSON.stringify(diagnosis, null, 2)}</pre></Card>}
          </> : <>
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
          <section className="access-operation-page"><h2>Integrations &amp; Providers</h2><p>Configure provider metadata and one-way protected credentials. Ready means the actual LOCAL owner resolved and validated this configuration; it does not establish provider entitlement, legal approval or Production acceptance.</p>
            <Table<IntegrationProvider> rowKey="code" dataSource={integrations?.providers ?? []} pagination={false} columns={[
              { title: 'Provider', render: (_, row) => <><strong>{row.displayName}</strong><br /><Typography.Text type="secondary">{row.category}</Typography.Text></> },
              { title: 'Scope', dataIndex: 'scope' },
              { title: 'Readiness', render: (_, row) => <Tag color={row.readiness === 'Ready' ? 'success' : row.readiness === 'Needs attention' ? 'error' : 'warning'}>{row.readiness}</Tag> },
              { title: 'Credential', render: (_, row) => row.secretConfigured ? 'Secret configured' : 'Not configured' },
              { title: 'Action', render: (_, row) => <Button size="small" onClick={() => { setSelectedProvider(row); integrationForm.setFieldsValue({ enabled: row.enabled, scope: row.scope, tenantId: row.options.tenantId, endpoint: row.endpoint, clientId: row.clientId, callbackUri: row.callbackUri, model: row.model, providerLabel: row.options.providerLabel, notice: row.options.notice, secretValue: undefined, clearSecret: false, subscriptionStatus: row.subscriptionStatus }); }}>Configure</Button> },
            ]} />
          </section>
          <section className="access-operation-page"><h2>Operational history</h2>{receipts.length ? <Timeline items={receipts.map(item => ({ key: item.operationId, title: `${topology.services.find(service => service.id === item.serviceId)?.label ?? 'Monergy area'} · ${item.posture}`, content: <p>{item.note}<br /><small>{new Date(item.recordedAt).toLocaleString()}</small></p> }))} /> : <p>No operational notes have been recorded.</p>}</section>
          </>}
          <Modal open={createOrganization} title="Create organization" onCancel={() => { setCreateOrganization(false); }} footer={null} destroyOnHidden>
            <Alert type="info" showIcon title="The administrator creates their own password" description="Monergy will prepare the organization and its first administrator. You cannot set or see their password, sign in for them or grant yourself access." />
            <Form form={organizationForm} layout="vertical" onFinish={(values) => { void submitOrganization(values); }}>
              <Form.Item name="organizationName" label="Organization name" rules={[{ required: true, whitespace: true }, { max: 200 }]}><Input /></Form.Item>
              <Form.Item name="displayName" label="Display name" rules={[{ required: true, whitespace: true }, { max: 200 }]}><Input /></Form.Item>
              <Form.Item label="Organization URL name (optional)" htmlFor="tenantSlug" extra="Lowercase letters, numbers and hyphens. If omitted, Monergy derives it from the display name.">
                <Space.Compact block>
                  <Form.Item name="tenantSlug" noStyle rules={[{ validator: (_, value: unknown) => {
                    if (value === undefined || value === '') return Promise.resolve();
                    if (typeof value === 'string' && value.length >= 3 && value.length <= 48 &&
                        !value.startsWith('-') && !value.endsWith('-') && /^[a-z0-9-]+$/u.test(value))
                      return Promise.resolve();
                    return Promise.reject(new Error('Use 3–48 lowercase letters, numbers or hyphens, without a leading or trailing hyphen.'));
                  } }]}><Input maxLength={48} /></Form.Item>
                  <Input value=".localhost" disabled aria-label="Organization address suffix" style={{ width: 120 }} />
                </Space.Compact>
              </Form.Item>
              <Form.Item name="country" label="Country" rules={[{ required: true }, { len: 2 }]}><Select options={[{ value: 'IN', label: 'India' }, { value: 'US', label: 'United States' }, { value: 'GB', label: 'United Kingdom' }]} /></Form.Item>
              <Form.Item name="timeZone" label="Time zone" rules={[{ required: true }]}><Select options={[{ value: 'Asia/Calcutta', label: 'India Standard Time' }, { value: 'UTC', label: 'UTC' }, { value: 'America/New_York', label: 'Eastern Time' }]} /></Form.Item>
              <Form.Item name="initialAdministratorEmail" label="Initial Organization Administrator email" rules={[{ required: true }, { type: 'email' }, { max: 320 }]}><Input autoComplete="off" /></Form.Item>
              <Form.Item name="placement" label="Placement" rules={[{ required: true }]}><Select options={[{ value: 'Shared', label: 'Shared' }, { value: 'Dedicated', label: 'Dedicated — coming later', disabled: true }]} /></Form.Item>
              <Button type="primary" htmlType="submit" loading={busy}>Create organization</Button>
            </Form>
          </Modal>
          <Modal open={selected !== null} title={`Add operational note · ${selected?.label ?? ''}`} onCancel={() => { setSelected(null); }} footer={null} destroyOnHidden>
            <p>This records an operational note. It does not restart anything, grant access or change customer information.</p>
            <Form form={postureForm} layout="vertical" onFinish={(values) => { void savePosture(values); }}>
              <Form.Item name="posture" label="Status" rules={[{ required: true }]}><Select options={['Normal', 'Investigating', 'Maintenance'].map(value => ({ value, label: value }))} /></Form.Item>
              <Form.Item name="note" label="Operational note" rules={[{ required: true, whitespace: true }, { max: 500 }]}><Input.TextArea rows={4} /></Form.Item>
              <Button type="primary" htmlType="submit" loading={busy}>Save note</Button>
            </Form>
          </Modal>
          <Modal open={selectedProvider !== null} title={`Configure ${selectedProvider?.displayName ?? ''}`} onCancel={() => { setSelectedProvider(null); }} footer={null} destroyOnHidden>
            <Alert type="info" showIcon title="Secrets are one-way" description="Set or replace the provider credential here. It is encrypted by the retained LOCAL profile key, is never returned to this browser and will not be repopulated after save." />
            <Form form={integrationForm} layout="vertical" onFinish={(values) => { void saveProvider(values); }}>
              <Form.Item name="enabled" label="Enabled" valuePropName="checked"><Switch /></Form.Item>
              <Form.Item name="scope" label="Scope" rules={[{ required: true }]}><Select options={['Platform', 'Organization'].map(value => ({ value, label: value }))} /></Form.Item>
              <Form.Item name="tenantId" label="Organization identifier (organization scope only)" dependencies={['scope']} rules={[({ getFieldValue }) => ({ validator(_, value) { return getFieldValue('scope') !== 'Organization' || value === 'T001' || value === 'T002' ? Promise.resolve() : Promise.reject(new Error('Choose T001 or T002 for organization scope.')); } })]}><Input /></Form.Item>
              <Form.Item name="endpoint" label="Endpoint"><Input /></Form.Item><Form.Item name="clientId" label="Safe client identifier"><Input /></Form.Item>
              <Form.Item name="callbackUri" label="Callback URL"><Input /></Form.Item><Form.Item name="model" label="Model"><Input /></Form.Item>
              {selectedProvider?.code === 'ai' && <><Form.Item name="providerLabel" label="Provider label"><Input /></Form.Item><Form.Item name="notice" label="Customer notice"><Input.TextArea rows={3} /></Form.Item></>}
              <Form.Item name="secretValue" label="Set or replace secret"><Input.Password autoComplete="new-password" /></Form.Item>
              <Form.Item name="clearSecret" label="Clear stored secret" valuePropName="checked"><Switch /></Form.Item>
              <Form.Item name="subscriptionStatus" label="Subscription status" rules={[{ required: true }]}><Select options={['Not configured', 'Configured', 'Pending', 'Unavailable'].map(value => ({ value, label: value }))} /></Form.Item>
              <Button type="primary" htmlType="submit" loading={busy}>Save configuration</Button>
            </Form>
          </Modal>
        </main>
      </WorkspaceShell>
    </FoundationProvider>
  );
}
