import { Alert, Button, Checkbox, Modal, Skeleton, Tag } from 'antd';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import { validOverview, validRevocationReceipt } from './consentModels';
import type { ConsentOverview, DelegatedConsent } from './consentModels';
import { CustomerSharingDialog } from './CustomerSharingDialog';
import './product.css';

interface RevokeIntent {
  api: AccessApi;
  scope: string;
  grantId: string;
  actorId: string;
  recipientLabel?: string | null;
}
interface ConsentViewState {
  api: AccessApi;
  scope: string;
  overview: ConsentOverview | null;
  busy: boolean;
  error: string;
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
  const activeMutation = useRef<AbortController | null>(null);
  const [revokeIntent, setRevokeIntent] = useState<RevokeIntent | null>(null);
  const [revokeConfirmed, setRevokeConfirmed] = useState(false);
  const [revokeActivity, setRevokeActivity] = useState<{ api: AccessApi; scope: string } | null>(null);
  const revokeBusy = revokeActivity?.api === api && revokeActivity.scope === scope;
  const [grantIntent, setGrantIntent] = useState<{ api: AccessApi; scope: string; grant?: DelegatedConsent } | null>(null);
  const [grantOutcome, setGrantOutcome] = useState<{ api: AccessApi; scope: string; warning: boolean; text: string } | null>(null);
  const [revokeOutcome, setRevokeOutcome] = useState<{
    api: AccessApi; scope: string; level: 'info' | 'warning'; text: string;
  } | null>(null);
  const currentContext = useRef({ api, scope });
  useLayoutEffect(() => { currentContext.current = { api, scope }; }, [api, scope]);
  const revokeMessage = revokeOutcome?.api === api && revokeOutcome.scope === scope &&
    revokeOutcome.level === 'info' ? revokeOutcome.text : '';
  const revokeError = revokeOutcome?.api === api && revokeOutcome.scope === scope &&
    revokeOutcome.level === 'warning' ? revokeOutcome.text : '';
  // Scope the rendered data as well as the request: effects run after rendering.
  const current = state?.api === api && state.scope === scope ? state : null;
  const overview = current?.overview ?? null;
  const busy = current?.busy ?? Boolean(api && customerId);
  const error = current?.error ?? '';
  const selectedRevoke = revokeIntent?.api === api && revokeIntent.scope === scope
    ? revokeIntent : null;
  const sharing = grantIntent?.api === api && grantIntent.scope === scope;
  const sharingOutcome = grantOutcome?.api === api && grantOutcome.scope === scope ? grantOutcome : null;

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
      activeMutation.current?.abort();
    };
  }, [refresh]);

  const revoke = async () => {
    if (!api || !selectedRevoke || !revokeConfirmed || !overview || busy || revokeBusy || sharing) return;
    const grant = overview.items.find((item) =>
      item.id === selectedRevoke.grantId && item.actorId === selectedRevoke.actorId);
    if (!grant || grant.revokedAt || Date.parse(grant.expiresAt) <= Date.now()) return;
    const controller = new AbortController();
    activeMutation.current = controller;
    setRevokeActivity({ api, scope });
    setRevokeOutcome(null);
    const operationId = crypto.randomUUID().replaceAll('-', '');
    try {
      const receipt = await api.revokeCustomerConsent<unknown>(
        customerId, grant.id, overview.version, operationId, controller.signal,
      );
      if (controller.signal.aborted || activeMutation.current !== controller ||
          currentContext.current.api !== api || currentContext.current.scope !== scope) return;
      if (!validRevocationReceipt(receipt) || receipt.id !== grant.id || receipt.customerId !== customerId ||
          receipt.actorId !== grant.actorId || receipt.version !== overview.version + 1) {
        throw new Error('CONSENT_RECEIPT_INVALID');
      }
      setRevokeIntent(null);
      setRevokeConfirmed(false);
      setState({ api, scope, overview: null, busy: true, error: '' });
      setRevokeOutcome({ api, scope, level: 'info', text: 'The Consent Service confirmed your revocation. Refreshing recorded permissions.' });
      await refresh();
    } catch {
      if (!controller.signal.aborted && activeMutation.current === controller &&
          currentContext.current.api === api && currentContext.current.scope === scope) {
        // A lost response can follow a committed mutation. Never guess or
        // automatically submit a new revocation operation.
        setRevokeIntent(null);
        setRevokeConfirmed(false);
        setRevokeOutcome({ api, scope, level: 'warning', text: "The revocation outcome couldn't be confirmed. Refresh permissions before attempting anything else." });
        setState({ api, scope, overview: null, busy: false, error: '' });
      }
    } finally {
      if (activeMutation.current === controller) {
        activeMutation.current = null;
        setRevokeActivity(null);
      }
    }
  };

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
      <p><a href="/connections/google-drive">Manage Google Drive permission and connection</a></p>
      <Alert
        showIcon
        type="info"
        title="About these permissions"
        description="This view covers up to 100 recent delegated customer-advice grants. Unexpired does not confirm effective access: current customer, actor, owner version, and permissions are checked separately when protected information is requested. Google Drive permission is managed in Connections."
      />
      <div className="product-drawer-actions">
        <Button
          onClick={() => {
            setGrantOutcome(null);
            setState(null);
            void refresh();
          }}
          disabled={!api || !customerId || busy || revokeBusy || sharing}
        >
          Refresh permissions
        </Button>
        <Button type="primary" disabled={!api || !overview || busy || revokeBusy || sharing || !!selectedRevoke}
          onClick={() => { if (api) { setGrantOutcome(null); setRevokeOutcome(null); setGrantIntent({ api, scope }); } }}>Share information</Button>
      </div>
      {sharingOutcome && <Alert showIcon type={sharingOutcome.warning ? 'warning' : 'success'} title={sharingOutcome.warning ? 'Sharing needs verification' : 'Sharing permission recorded'} description={sharingOutcome.text} />}
      {revokeMessage && <Alert showIcon type="info" title={revokeMessage} />}
      {revokeError && <Alert showIcon type="warning" title="Revocation needs verification" description={revokeError} />}
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
                    {item.recipientLabel ? (
                      <p>Recipient: <strong>{item.recipientLabel}</strong></p>
                    ) : (
                      <p>Recipient label not available. Permission is recorded for one exact recipient, not the organization.</p>
                    )}
                    <p>
                      {item.capabilityIds.length} operations recorded in this grant; current access
                      is evaluated separately.
                    </p>
                    <p>Expires: {formatted(item.expiresAt)}</p>
                    {item.revokedAt && <p>Revoked: {formatted(item.revokedAt)}</p>}
                    {item.revokedAt === null && <Button disabled={busy || revokeBusy || sharing || !!selectedRevoke} onClick={() => {
                      if (api) { setGrantOutcome(null); setRevokeOutcome(null); setGrantIntent({ api, scope, grant: item }); }
                    }}>{current === 'Expired' ? 'Renew sharing' : 'Change sharing'}</Button>}
                    {current === 'Unexpired record' && (
                      <Button danger disabled={busy || revokeBusy || sharing} onClick={() => {
                        if (!api) return;
                        setGrantOutcome(null);
                        setRevokeIntent({ api, scope, grantId: item.id, actorId: item.actorId, recipientLabel: item.recipientLabel });
                        setRevokeConfirmed(false);
                        setRevokeOutcome(null);
                      }}>Stop sharing</Button>
                    )}
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
      <Modal
        title="Confirm stopping a sharing permission"
        destroyOnHidden
        open={Boolean(selectedRevoke)}
        okText="Stop sharing"
        okButtonProps={{ danger: true, disabled: !revokeConfirmed || revokeBusy }}
        confirmLoading={revokeBusy}
        onOk={() => { void revoke(); }}
        onCancel={() => { if (!revokeBusy) { setRevokeIntent(null); setRevokeConfirmed(false); } }}
        closable={!revokeBusy}
        mask={{ closable: !revokeBusy }}
      >
        {selectedRevoke?.recipientLabel ? (
          <p>Recipient: <strong>{selectedRevoke.recipientLabel}</strong></p>
        ) : (
          <Alert showIcon type="warning" title="Recipient label not available"
            description="Monergy only has a recipient reference for this recorded grant. Do not proceed unless you recognize the exact recipient and grant below." />
        )}
        <p>Recipient reference: <code>{selectedRevoke?.actorId}</code></p>
        <p>Grant reference: <code>{selectedRevoke?.grantId}</code></p>
        <p>Re-authorization requires a separate customer action.</p>
        <Checkbox checked={revokeConfirmed} onChange={(event) => { setRevokeConfirmed(event.target.checked); }}>
          I recognize this recipient reference and confirm that I want to stop sharing.
        </Checkbox>
      </Modal>
      {sharing && <CustomerSharingDialog replacement={grantIntent.grant} api={api} tenant={session?.tenantId ?? ''} actor={session?.actorId ?? ''} customer={customerId}
        onClose={() => { setGrantIntent(null); }}
        onVerified={(result, email) => {
          setGrantIntent(null); setState({ api, scope, overview: result, busy: false, error: '' });
          setGrantOutcome({ api, scope, warning: false, text: `Your sharing permission for ${email} is recorded. Current organization access is evaluated separately.` });
        }}
        onUncertain={() => {
          setGrantIntent(null); setState({ api, scope, overview: null, busy: false, error: '' });
          setGrantOutcome({ api, scope, warning: true, text: 'The sharing outcome could not be confirmed. Refresh permissions before attempting another change.' });
        }} />}
      <p>
        Sharing permissions are purpose-bound. You can record a new permission for an existing
        organization recipient, change its scope or expiry, renew an expired permission, or stop sharing. Changes and renewals replace the selected record after your explicit confirmation.
      </p>
    </main>
  );
}
