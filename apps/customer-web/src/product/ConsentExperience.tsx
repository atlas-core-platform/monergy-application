import { Alert, Button, Skeleton, Tag } from 'antd';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import './product.css';

interface DelegatedConsent {
  id: string;
  customerId: string;
  actorId: string;
  purpose: string;
  capabilityIds: string[];
  expiresAt: string;
  revokedAt: string | null;
}
interface ConsentOverview {
  version: number;
  items: DelegatedConsent[];
}
interface ConsentViewState {
  api: AccessApi;
  scope: string;
  overview: ConsentOverview | null;
  busy: boolean;
  error: string;
}
function validOverview(value: unknown, customerId: string): value is ConsentOverview {
  if (!value || typeof value !== 'object') return false;
  const data = value as { version?: number; items?: unknown[] };
  return (
    Number.isSafeInteger(data.version) &&
    (data.version ?? 0) >= 1 &&
    Array.isArray(data.items) &&
    data.items.length <= 100 &&
    data.items.every((value) => {
      if (!value || typeof value !== 'object') return false;
      const item = value as Partial<DelegatedConsent>;
      return (
        typeof item.id === 'string' &&
        item.id.length > 0 &&
        item.customerId === customerId &&
        typeof item.actorId === 'string' &&
        item.actorId.length > 0 &&
        item.purpose === 'customer-advice' &&
        Array.isArray(item.capabilityIds) &&
        item.capabilityIds.every((capability) => typeof capability === 'string') &&
        typeof item.expiresAt === 'string' &&
        Number.isFinite(Date.parse(item.expiresAt)) &&
        (item.revokedAt === null ||
          (typeof item.revokedAt === 'string' && Number.isFinite(Date.parse(item.revokedAt))))
      );
    })
  );
}
function status(item: DelegatedConsent) {
  if (item.revokedAt !== null) return 'Revoked';
  return Date.parse(item.expiresAt) <= Date.now() ? 'Expired' : 'Unexpired record';
}
function formatted(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(
    new Date(value),
  );
}
export default function ConsentExperience() {
  const { api, authorization, session } = useWorkspaceSession();
  const customer = authorization?.area === 'product' ? authorization.context : null;
  const customerId = customer?.customerId ?? '';
  const scope = JSON.stringify([
    session?.tenantId,
    session?.actorId,
    session?.authenticationContextId,
    session?.subjectVersion,
    customerId,
  ]);
  const [state, setState] = useState<ConsentViewState | null>(null);
  const activeRequest = useRef<AbortController | null>(null);
  // Scope the rendered data as well as the request: effects run after rendering.
  const current = state?.api === api && state.scope === scope ? state : null;
  const overview = current?.overview ?? null;
  const busy = current?.busy ?? Boolean(api && customerId);
  const error = current?.error ?? '';

  const refresh = useCallback(async () => {
    activeRequest.current?.abort();
    if (!api || !customerId) return;
    const controller = new AbortController();
    activeRequest.current = controller;
    const pending = { api, scope, overview: null, busy: true, error: '' };
    try {
      const result = await api.customerConsent<ConsentOverview>(customerId, controller.signal);
      if (!validOverview(result, customerId)) throw new Error('INVALID_CONSENT_OVERVIEW');
      if (!controller.signal.aborted && activeRequest.current === controller) {
        setState({ ...pending, overview: result, busy: false });
      }
    } catch {
      if (!controller.signal.aborted && activeRequest.current === controller) {
        setState({
          ...pending,
          busy: false,
          error: "We couldn't retrieve your current sharing permissions. Their status is unknown.",
        });
      }
    }
  }, [api, customerId, scope]);

  useEffect(() => {
    const start = window.setTimeout(() => {
      void refresh();
    }, 0);
    return () => {
      window.clearTimeout(start);
      activeRequest.current?.abort();
    };
  }, [refresh]);

  return (
    <main className="access-page">
      <div className="workspace-page-heading">
        <div>
          <p className="workspace-eyebrow">CONSENT & PRIVACY</p>
          <h1>Sharing permissions</h1>
          <p className="workspace-subtitle">
            Review recorded permissions to share your financial information with another person.
            Only your currently authorized customer record can be shown.
          </p>
        </div>
        <Tag>TEST ENVIRONMENT · TEMPORARY DATA</Tag>
      </div>
      <Alert
        showIcon
        type="info"
        title="About these permissions"
        description="This view covers up to 100 recent delegated customer-advice grants. Unexpired does not confirm effective access: current customer, actor, owner version, and permissions are checked separately when protected information is requested. Provider connections are not shown here."
      />
      <div className="product-drawer-actions">
        <Button
          onClick={() => {
            setState(null);
            void refresh();
          }}
          disabled={!api || !customerId || busy}
        >
          Refresh permissions
        </Button>
      </div>
      {!customerId ? (
        <Alert showIcon type="warning" title="Customer ownership could not be established" />
      ) : busy && overview === null && !error ? (
        <Skeleton active paragraph={{ rows: 4 }} />
      ) : error ? (
        <Alert
          showIcon
          type="warning"
          title="Sharing permissions unavailable"
          description={error}
        />
      ) : overview?.items.length === 0 ? (
        <Alert
          showIcon
          type="info"
          title="No recorded delegated permissions"
          description="No delegated customer-advice permissions were returned in the current record. Other permissions and provider connections are not inferred."
        />
      ) : overview ? (
        <section aria-label="Recorded sharing permissions">
          <p>Showing {overview.items.length} most recent recorded permissions.</p>
          <div className="product-fact-list">
            {overview.items.map((item) => {
              const current = status(item);
              return (
                <article key={item.id} className="product-fact-card">
                  <div>
                    <span className="product-fact-tags">
                      <Tag color={current === 'Unexpired record' ? 'processing' : 'default'}>
                        {current}
                      </Tag>
                    </span>
                    <strong>Customer-advice permission</strong>
                    <p>
                      Recipient name is not available in this view. Permission is recorded for a
                      specific recipient, not everyone in an organization.
                    </p>
                    <p>
                      {item.capabilityIds.length} operations recorded in this grant; current access
                      is evaluated separately.
                    </p>
                    <p>Expires: {formatted(item.expiresAt)}</p>
                    {item.revokedAt && <p>Revoked: {formatted(item.revokedAt)}</p>}
                    <details>
                      <summary>Technical details</summary>
                      <p>Recipient reference: {item.actorId}</p>
                      <p>Grant reference: {item.id}</p>
                      <p>
                        Permission references: {item.capabilityIds.join(', ') || 'None recorded'}
                      </p>
                    </details>
                  </div>
                </article>
              );
            })}
          </div>
        </section>
      ) : null}
      <p>
        Granting, changing or revoking sharing permissions is not available in this view yet. Your
        existing permissions have not been changed.
      </p>
    </main>
  );
}
