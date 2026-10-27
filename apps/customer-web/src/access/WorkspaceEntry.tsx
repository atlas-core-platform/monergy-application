import { Alert, Button, Form, Input, Tag } from 'antd';
import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { WorkspaceIcon } from '@monergy/ui-foundation';
import { useWorkspaceSession } from './workspaceSession';
import { CustomerRegistrationEntry } from './CustomerRegistrationEntry';
import { CustomerEmailEntry } from './CustomerEmailEntry';
import { PersonaCredentialEntry } from './PersonaCredentialEntry';
import './access.css';

export function WorkspaceEntry({
  children,
  area = 'administration',
}: {
  children: ReactNode;
  area?: 'administration' | 'product' | 'kyc-review' | 'advisor';
}) {
  const { status, session, message, connect, connectEmail, connectPassword, connectPersonaPassword, retry, cancel } = useWorkspaceSession();
  const [form] = Form.useForm<{ tenantId: string; key: string }>();
  const [emailAvailable, setEmailAvailable] = useState(false);
  const [method, setMethod] = useState<'auto' | 'password' | 'persona' | 'email' | 'local'>('auto');
  const [registrationAvailable, setRegistrationAvailable] = useState<boolean | null>(null);
  const [personaAvailable, setPersonaAvailable] = useState<boolean | null>(null);
  const [personaActivation, setPersonaActivation] = useState<'email' | 'tester-link'>('email');
  // Existing provisioned identities use the common credential entry.
  // Customer self-registration remains an explicit separate choice.
  const personaEntry = method === 'persona' ||
    method === 'auto' && personaAvailable === true;
  const registrationEntry = area === 'product' &&
    (method === 'password' || method === 'auto' &&
      personaAvailable !== true && registrationAvailable === true);
  const emailEntry = method === 'email';
  const loadingMethods = method === 'auto' &&
    (personaAvailable === null || area === 'product' && registrationAvailable === null);
  useEffect(() => {
    if (!connectPersonaPassword) return;
    const controller = new AbortController();
    void fetch('/identity-api/local/v1/persona-credentials', {
      cache: 'no-store', redirect: 'error', signal: controller.signal,
    }).then(async response => {
      const value: unknown = response.ok ? await response.json() : null;
      if (!controller.signal.aborted) {
        setPersonaAvailable(!!value && typeof value === 'object' && 'enabled' in value && value.enabled === true);
        setPersonaActivation(!!value && typeof value === 'object' && 'activation' in value &&
          value.activation === 'tester-link' ? 'tester-link' : 'email');
      }
    }).catch(() => {
      if (!controller.signal.aborted) setPersonaAvailable(false);
    });
    return () => { controller.abort(); };
  }, [connectPersonaPassword]);
  useEffect(() => { if (area !== 'product' || !connectPassword) return; const controller = new AbortController(); void fetch('/identity-api/local/v1/customer-registration', { cache: 'no-store', redirect: 'error', signal: controller.signal }).then(async r => { const v: unknown = r.ok ? await r.json() : null; if (!controller.signal.aborted) setRegistrationAvailable(!!v && typeof v === 'object' && 'enabled' in v && v.enabled === true); }).catch(() => { if (!controller.signal.aborted) setRegistrationAvailable(false); }); return () => { controller.abort(); }; }, [area, connectPassword]);
  useEffect(() => {
    if (area !== 'product' || !connectEmail) return;
    const controller = new AbortController();
    void fetch('/identity-api/local/v1/customer-email-access', {
      cache: 'no-store', redirect: 'error', signal: controller.signal,
    }).then(async (response) => {
      const value: unknown = response.ok ? await response.json() : null;
      if (!controller.signal.aborted && value && typeof value === 'object' && 'enabled' in value)
        setEmailAvailable(value.enabled === true);
    }).catch(() => undefined);
    return () => { controller.abort(); };
  }, [area, connectEmail]);
  if (status === 'authorized' && session) return children;
  const busy = status === 'connecting' || status === 'verifying';
  return (
    <div className="access-entry">
      <header className="access-public-header">
        <span className="access-wordmark">
          <svg viewBox="0 0 32 32" aria-hidden="true">
            <path d="M5 24V8l11 10L27 8v16" />
          </svg>
          monergy
        </span>
        <Tag>Test environment</Tag>
      </header>
      <main className="access-public-body">
        <section className="access-public-intro">
          <p className="workspace-eyebrow">
            {area === 'administration' ? 'ORGANIZATION SETTINGS' : area === 'advisor' ? 'FINANCIAL ADVISOR WORKSPACE' : 'FINANCIAL WORKSPACE'}
          </p>
          <h1>
            {area === 'administration' ? 'Clear access.' : area === 'advisor' ? 'Support clients.' : 'Financial clarity.'}
            <br />
            {area === 'administration' ? 'Confident decisions.' : area === 'advisor' ? 'Respect consent.' : 'Grounded in your information.'}
          </h1>
          <p>
            {area === 'administration'
              ? 'Manage the people, roles and access permissions that keep your organization in control.'
              : area === 'advisor' ? 'Review your own role and permitted assignments. Customer information remains private until access and consent are verified.' : 'Bring your financial information together in a private, traceable workspace.'}
          </p>
          <div className="access-public-assurance">
            <WorkspaceIcon name="shield" size={18} />
            <span>
              {area === 'administration'
                ? 'Administration becomes available after your identity and organization authority have been verified.'
                : area === 'advisor' ? 'Advisor access requires your active Financial Advisor role. Customer-specific work also requires assignment, permitted business capabilities and current consent.' : 'Your workspace becomes available after your identity, customer ownership and product access have been verified.'}
            </span>
          </div>
        </section>
        <section className="access-connect-card" aria-labelledby="access-connect-title">
          <span className="access-entry-icon">
            <WorkspaceIcon name="shield" size={20} />
          </span>
          <h2 id="access-connect-title">Connect to your workspace</h2>
          {!loadingMethods && !registrationEntry && !personaEntry && !emailEntry && <p>Enter the organization and local key provided by your administrator.</p>}
          {message && (
            <Alert
              showIcon
              type={status === 'disconnected' ? 'warning' : 'error'}
              title={message}
              className="access-notice"
            />
          )}
          {status === 'disconnected' ? (
            <>
              <p>
                {area === 'administration' ? 'Administration' : 'Your financial workspace'} is
                paused. Reconnecting checks your existing session with its owner.
              </p>
              <Button
                type="primary"
                block
                onClick={() => {
                  void retry();
                }}
              >
                Reconnect workspace
              </Button>
              <Button block type="text" onClick={cancel}>
                Use another connection
              </Button>
            </>
          ) : loadingMethods ? (
            <>
              <p role="status">Checking available sign-in methods…</p>
              <Button block type="text" onClick={() => { setMethod('local'); }}>
                Use a local test key
              </Button>
            </>
          ) : registrationEntry && connectPassword ? (
            <CustomerRegistrationEntry busy={busy} onSignIn={connectPassword} onCancel={() => { cancel(); setMethod('local'); }} />
          ) : personaEntry && connectPersonaPassword ? (
            <PersonaCredentialEntry busy={busy} onSignIn={connectPersonaPassword}
              activationMode={personaActivation}
              returnPath={area === 'administration' ? '/access' : area === 'advisor' ? '/advisor' : '/'}
              onCancel={() => { cancel(); setMethod('local'); }} />
          ) : emailEntry && connectEmail ? (
            <CustomerEmailEntry busy={busy} onVerify={connectEmail} onCancel={() => { cancel(); setMethod('local'); }} />
          ) : (
            <Form
              form={form}
              layout="vertical"
              initialValues={{ tenantId: 'T001' }}
              onFinish={(values) => {
                const key = values.key.trim();
                form.setFieldValue('key', '');
                void connect(values.tenantId.trim(), key);
              }}
            >
              <Form.Item
                name="tenantId"
                label="Organization"
                extra={
                  area === 'administration'
                    ? 'The organization you want to administer.'
                    : 'The organization for the financial workspace you want to open.'
                }
                rules={[
                  { required: true, message: 'Enter your organization.' },
                  {
                    pattern: /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$/,
                    message: 'Enter a valid organization identifier.',
                  },
                ]}
              >
                <Input autoComplete="organization" disabled={busy} />
              </Form.Item>
              <Form.Item
                name="key"
                label="Local access key"
                rules={[{ required: true, message: 'Enter your local access key.' }]}
              >
                <Input.Password autoComplete="off" disabled={busy} />
              </Form.Item>
              <Button
                type="primary"
                block
                htmlType="submit"
                loading={busy}
                disabled={busy}
                aria-label="Connect workspace"
                aria-busy={busy}
              >
                Connect workspace
              </Button>
              {busy && (
                <>
                  <p className="access-connection-status" role="status">
                    {status === 'connecting'
                      ? 'Verifying your identity…'
                      : area === 'administration'
                        ? 'Verifying Organization Administrator access…'
                        : 'Resolving your customer workspace and product access…'}
                  </p>
                  <Button block type="text" onClick={cancel}>
                    Cancel connection
                  </Button>
                </>
              )}
            </Form>
          )}
          {!registrationEntry && registrationAvailable === true && area === 'product' && status !== 'disconnected' &&
            <Button block type="link" disabled={busy} onClick={() => { cancel(); setMethod('password'); }}>Sign in or create an account</Button>}
          {!personaEntry && personaAvailable === true && status !== 'disconnected' &&
            <Button block type="link" disabled={busy} onClick={() => { cancel(); setMethod('persona'); }}>Sign in with an activated account</Button>}
          {!emailEntry && emailAvailable && area === 'product' && status !== 'disconnected' &&
            <Button block type="link" disabled={busy} onClick={() => { cancel(); setMethod('email'); }}>Use an email code</Button>}
          {(registrationEntry || personaEntry || emailEntry) && status !== 'disconnected' &&
            <Button block type="link" disabled={busy} onClick={() => { cancel(); setMethod('local'); }}>Use a local test key</Button>}
          {!loadingMethods && !registrationEntry && !personaEntry && !emailEntry && <p className="access-connect-footnote">
            Your key is cleared after use. Test-environment keys do not configure production
            sign-in.
          </p>}
        </section>
      </main>
      <footer className="access-public-footer">
        Monergy · {area === 'administration' ? 'Organization Settings' : 'Financial Workspace'}
        <span>Test environment connection</span>
      </footer>
    </div>
  );
}
