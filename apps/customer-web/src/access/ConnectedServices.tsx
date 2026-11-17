import { Alert, Button, Card, Empty, Tag } from 'antd';
import { useEffect, useState } from 'react';
import type { TenantSession } from './accessApi';

interface ConnectedProvider {
  code: string;
  displayName: string;
  category: string;
  readiness: string;
  subscriptionStatus: string;
  updatedAt: string;
}

export function ConnectedServices({ session }: { session: TenantSession }) {
  const [providers, setProviders] = useState<ConnectedProvider[] | null>(null);
  const [error, setError] = useState('');
  const load = () => {
    setProviders(null); setError('');
    void fetch('/uat-api/v1/connected-services', {
      cache: 'no-store', redirect: 'error',
      headers: { 'X-Monergy-Tenant': session.tenantId, 'X-Monergy-Session': session.authenticationContextId },
    }).then(async response => {
      if (!response.ok) throw new Error('Connected services could not be loaded.');
      const value = await response.json() as { tenantId: string; providers: ConnectedProvider[] };
      if (value.tenantId !== session.tenantId || !Array.isArray(value.providers))
        throw new Error('Connected services returned an invalid organization context.');
      setProviders(value.providers);
    }).catch((failure: unknown) => { setError(failure instanceof Error ? failure.message : 'Connected services could not be loaded.'); });
  };
  useEffect(() => {
    const controller = new AbortController();
    void fetch('/uat-api/v1/connected-services', {
      cache: 'no-store', redirect: 'error', signal: controller.signal,
      headers: { 'X-Monergy-Tenant': session.tenantId, 'X-Monergy-Session': session.authenticationContextId },
    }).then(async response => {
      if (!response.ok) throw new Error('Connected services could not be loaded.');
      const value = await response.json() as { tenantId: string; providers: ConnectedProvider[] };
      if (value.tenantId !== session.tenantId || !Array.isArray(value.providers))
        throw new Error('Connected services returned an invalid organization context.');
      if (!controller.signal.aborted) setProviders(value.providers);
    }).catch((failure: unknown) => {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Connected services could not be loaded.');
    });
    return () => { controller.abort(); };
  }, [session.authenticationContextId, session.tenantId]);
  return <section className="access-page" aria-labelledby="connected-services-title">
    <header className="access-page-header"><div><h1 id="connected-services-title">Connected Services</h1><p>Review services approved for this organization and their operational readiness.</p></div><Button onClick={load}>Refresh</Button></header>
    <Alert type="info" showIcon title="Organization view only" description="Provider secrets, platform-wide settings and other organizations are never shown here. Readiness does not establish a provider subscription, legal approval or Production acceptance." />
    {error && <Alert type="error" showIcon title={error} />}
    {providers?.length === 0 && <Empty description="No connected services are enabled for this organization." />}
    <div className="access-service-list">{providers?.map(provider => <Card key={provider.code} title={provider.displayName} size="small">
      <Tag>{provider.category}</Tag><Tag color={provider.readiness.startsWith('Configured') ? 'success' : 'warning'}>{provider.readiness}</Tag>
      <p>Subscription: {provider.subscriptionStatus}</p><small>Last updated {new Date(provider.updatedAt).toLocaleString()}</small>
    </Card>)}</div>
  </section>;
}
