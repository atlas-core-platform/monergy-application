import { Alert, Button, Card, Space, Tag } from 'antd';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import './product.css';

type ConnectionState = 'Not configured' | 'Ready to connect' | 'Setup in progress' | 'Connected' | 'Needs attention' | 'Unavailable';
type PermissionState = 'Not requested' | 'Pending' | 'Current' | 'Review required' | 'Unavailable';
type HealthState = 'Not configured' | 'Status available' | 'Attention required' | 'Status unavailable';
interface ConnectionSummary { providerCode: string; provider: string; state: ConnectionState; permissionState: PermissionState; healthState: HealthState; managePath: string; checkedAt: string; lastConfirmedAt: string | null }
interface Overview { tenantId: string; customerId: string; generatedAt: string; connections: ConnectionSummary[] }
interface State { api: AccessApi; scope: string; overview: Overview | null; busy: boolean; error: string }

const expected = new Map([
  ['google-drive', { provider: 'Google Drive', path: '/connections/google-drive' }],
  ['onedrive', { provider: 'OneDrive', path: '/connections/onedrive' }],
]);
const states = new Set<ConnectionState>(['Not configured', 'Ready to connect', 'Setup in progress', 'Connected', 'Needs attention', 'Unavailable']);
const permissions: Record<ConnectionState, PermissionState> = {
  'Not configured': 'Not requested', 'Ready to connect': 'Not requested', 'Setup in progress': 'Pending',
  Connected: 'Current', 'Needs attention': 'Review required', Unavailable: 'Unavailable',
};
const health: Record<ConnectionState, HealthState> = {
  'Not configured': 'Not configured', 'Ready to connect': 'Status available', 'Setup in progress': 'Status available',
  Connected: 'Status available', 'Needs attention': 'Attention required', Unavailable: 'Status unavailable',
};
function validOverview(value: Overview | null | undefined, tenant: string, customer: string) {
  if (value?.tenantId !== tenant || value.customerId !== customer || !Number.isFinite(Date.parse(value.generatedAt)) ||
      !Array.isArray(value.connections) || value.connections.length !== expected.size) return false;
  const seen = new Set<string>();
  for (const item of value.connections) {
    const definition = expected.get(item.providerCode);
    if (!definition || seen.has(item.providerCode) || definition.provider !== item.provider || definition.path !== item.managePath ||
        !states.has(item.state) || permissions[item.state] !== item.permissionState || health[item.state] !== item.healthState ||
        !Number.isFinite(Date.parse(item.checkedAt)) ||
        item.lastConfirmedAt !== null && !Number.isFinite(Date.parse(item.lastConfirmedAt)) ||
        item.state !== 'Connected' && item.lastConfirmedAt !== null) return false;
    seen.add(item.providerCode);
  }
  return seen.size === expected.size;
}
const color = (state: ConnectionState) => state === 'Connected' ? 'success' : state === 'Needs attention' || state === 'Unavailable' ? 'warning' : 'default';
const permissionColor = (state: PermissionState) => state === 'Current' ? 'success' : state === 'Review required' || state === 'Unavailable' ? 'warning' : 'default';
const healthColor = (state: HealthState) => state === 'Status available' ? 'success' : state === 'Attention required' || state === 'Status unavailable' ? 'warning' : 'default';

export default function ConnectionsOverviewExperience() {
  const { api, session, authorization } = useWorkspaceSession();
  const customer = authorization?.area === 'product' ? authorization.context : null;
  const allowed = customer?.capabilities.includes('integration-gateway.connection.manage');
  const scope = JSON.stringify([session?.tenantId, session?.actorId, session?.authenticationContextId, session?.subjectVersion, customer?.customerId]);
  const [state, setState] = useState<State | null>(null);
  const active = useRef<AbortController | null>(null);
  const context = useRef({ api, scope });
  useLayoutEffect(() => { context.current = { api, scope }; }, [api, scope]);
  const current = state?.api === api && state.scope === scope ? state : null;
  const load = () => {
    if (!api || !session || !customer || !allowed) return;
    active.current?.abort(); const controller = new AbortController(); active.current = controller;
    setState({ api, scope, overview: null, busy: true, error: '' });
    void api.connectionsOverview<Overview>(controller.signal).then(value => {
      if (controller.signal.aborted || context.current.api !== api || context.current.scope !== scope) return;
      if (!validOverview(value, session.tenantId, customer.customerId)) throw new Error('INVALID_CONNECTION_OVERVIEW');
      setState({ api, scope, overview: value, busy: false, error: '' });
    }).catch(() => {
      if (!controller.signal.aborted && context.current.api === api && context.current.scope === scope)
        setState({ api, scope, overview: null, busy: false, error: 'Connection status is unavailable. Refresh to try again.' });
    });
  };
  useEffect(() => {
    const timer = window.setTimeout(load, 0);
    return () => { window.clearTimeout(timer); active.current?.abort(); };
  // load is intentionally rebound to the exact verified session scope.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, allowed, scope]);

  if (!customer || !api) return <main className="product-page"><h1>Connections</h1><p>Open your customer workspace to review connections.</p></main>;
  if (!allowed) return <main className="product-page"><h1>Connections</h1><p>Your current access does not allow managing connections. Ask your organization administrator for connection access.</p></main>;
  return <main className="product-page" aria-busy={current?.busy ?? true}>
    <h1>Connections</h1>
    <p>Review data sources configured for your organization. Opening a provider does not grant permission or import files.</p>
    <p>Operational status reflects the connection owner's latest response. It is not a live provider availability test and does not confirm permission or a completed import.</p>
    {current?.error && <Alert type="warning" showIcon title={current.error} />}
    <Button disabled={current?.busy ?? true} onClick={load}>Refresh connections</Button>
    {current?.overview && <Space orientation="vertical" size="middle" style={{ width: '100%', marginTop: 16 }}>
      {current.overview.connections.map(item => <Card key={item.providerCode} title={item.provider} extra={<Tag color={color(item.state)}>{item.state}</Tag>}>
        <p><strong>Permission:</strong> <Tag color={permissionColor(item.permissionState)}>{item.permissionState}</Tag></p>
        <p><strong>Operational status:</strong> <Tag color={healthColor(item.healthState)}>{item.healthState}</Tag></p>
        <p>Status checked {new Date(item.checkedAt).toLocaleString()}.</p>
        {item.state === 'Not configured' && <p>Your organization has not configured this provider. Its settings can be supplied during onboarding.</p>}
        {item.state === 'Ready to connect' && <p>The provider is configured and ready for you to review its permission before connecting.</p>}
        {item.state === 'Setup in progress' && <p>Connection setup has started. Open the provider to continue or review the selected folder.</p>}
        {item.state === 'Connected' && <p>{item.lastConfirmedAt ? `Last confirmed ${new Date(item.lastConfirmedAt).toLocaleString()}.` : 'The provider reports a current connection.'}</p>}
        {item.state === 'Needs attention' && <p>Open the provider to review current permission, configuration and folder access.</p>}
        {item.state === 'Unavailable' && <p>Current provider status could not be confirmed. No connection result has been inferred.</p>}
        {item.healthState === 'Attention required' && <p>The connection owner reported that this connection needs review.</p>}
        {item.healthState === 'Status unavailable' && <p>No provider outage, permission state or successful connection has been inferred.</p>}
        {item.permissionState === 'Current' && <p>Current permission was confirmed by the connection owner. It is checked again before provider operations.</p>}
        {item.permissionState === 'Review required' && <p>Current permission could not be confirmed. Open the provider to review it; this page cannot grant permission.</p>}
        <Button href={item.managePath}>Manage {item.provider}</Button>
      </Card>)}
    </Space>}
  </main>;
}
