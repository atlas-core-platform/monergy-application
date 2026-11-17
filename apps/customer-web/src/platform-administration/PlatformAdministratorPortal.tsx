import { Alert, Button, Card, Form, Input, Modal, Select, Space, Table, Tag, Timeline, Typography } from 'antd';
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
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [selected, setSelected] = useState<Service | null>(null);
  const [form] = Form.useForm<{ key: string }>();
  const [postureForm] = Form.useForm<{ posture: Posture; note: string }>();

  const load = useCallback(async (current: PlatformSession) => {
    const [verified, services, readiness, history] = await Promise.all([
      request<PlatformContext>('/platform-api/local/v1/context', current.session),
      request<Topology>('/platform-api/local/v1/topology', current.session),
      request<{ gates: Gate[] }>('/platform-api/local/v1/readiness', current.session),
      request<{ items: Receipt[] }>('/platform-api/local/v1/audit', current.session),
    ]);
    if (verified.authority !== 'PlatformAdministrator' || verified.tenantAccess ||
        verified.customerDataAccess || verified.breakGlassAvailable || verified.productionAccepted)
      throw new Error('Your administrator access could not be confirmed. Sign in again.');
    setContext(verified);
    setTopology(services);
    setGates(readiness.gates);
    setReceipts(history.items);
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

  const signIn = async ({ key }: { key: string }) => {
    setBusy(true); setError(''); form.setFieldValue('key', '');
    try {
      const established = await request<PlatformSession>('/platform-api/local/v1/sessions', undefined, {
        method: 'POST', body: JSON.stringify({ key: key.trim() }),
      });
      await load(established);
      setSession(established);
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Platform connection failed.'); }
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
          <Card className="access-connect-card" title="Open Monergy Administration">
            {error && <Alert type="error" showIcon title={error} className="access-notice" />}
            <Form form={form} layout="vertical" onFinish={(values) => { void signIn(values); }}>
              <Form.Item name="key" label="Platform Administrator key" rules={[{ required: true, message: 'Enter the platform key.' }]}>
                <Input.Password autoComplete="off" disabled={busy} />
              </Form.Item>
              <Button type="primary" block htmlType="submit" loading={busy}>Open administration</Button>
            </Form>
            <p className="access-connect-footnote">The key is cleared after use and cannot be used to enter an organization's workspace.</p>
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
          <section className="access-operation-page"><h2>Operational history</h2>{receipts.length ? <Timeline items={receipts.map(item => ({ key: item.operationId, title: `${topology.services.find(service => service.id === item.serviceId)?.label ?? 'Monergy area'} · ${item.posture}`, content: <p>{item.note}<br /><small>{new Date(item.recordedAt).toLocaleString()}</small></p> }))} /> : <p>No operational notes have been recorded.</p>}</section>
          <Modal open={selected !== null} title={`Add operational note · ${selected?.label ?? ''}`} onCancel={() => { setSelected(null); }} footer={null} destroyOnHidden>
            <p>This records an operational note. It does not restart anything, grant access or change customer information.</p>
            <Form form={postureForm} layout="vertical" onFinish={(values) => { void savePosture(values); }}>
              <Form.Item name="posture" label="Status" rules={[{ required: true }]}><Select options={['Normal', 'Investigating', 'Maintenance'].map(value => ({ value, label: value }))} /></Form.Item>
              <Form.Item name="note" label="Operational note" rules={[{ required: true, whitespace: true }, { max: 500 }]}><Input.TextArea rows={4} /></Form.Item>
              <Button type="primary" htmlType="submit" loading={busy}>Save note</Button>
            </Form>
          </Modal>
        </main>
      </WorkspaceShell>
    </FoundationProvider>
  );
}
