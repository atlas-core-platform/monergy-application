import { Alert, Button, Form, Input, Tag } from 'antd';
import type { ReactNode } from 'react';
import { WorkspaceIcon } from '@monergy/ui-foundation';
import { useWorkspaceSession } from './workspaceSession';
import './access.css';

export function WorkspaceEntry({
  children,
  area = 'administration',
}: {
  children: ReactNode;
  area?: 'administration' | 'product';
}) {
  const { status, session, message, connect, retry, cancel } = useWorkspaceSession();
  const [form] = Form.useForm<{ tenantId: string; key: string }>();
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
            {area === 'administration' ? 'ORGANIZATION SETTINGS' : 'FINANCIAL WORKSPACE'}
          </p>
          <h1>
            {area === 'administration' ? 'Clear access.' : 'Financial clarity.'}
            <br />
            {area === 'administration' ? 'Confident decisions.' : 'Grounded in your information.'}
          </h1>
          <p>
            {area === 'administration'
              ? 'Manage the people, roles and access permissions that keep your organization in control.'
              : 'Bring your financial information together in a private, traceable workspace.'}
          </p>
          <div className="access-public-assurance">
            <WorkspaceIcon name="shield" size={18} />
            <span>
              {area === 'administration'
                ? 'Administration becomes available after your identity and organization authority have been verified.'
                : 'Your workspace becomes available after your identity, customer ownership and product access have been verified.'}
            </span>
          </div>
        </section>
        <section className="access-connect-card" aria-labelledby="access-connect-title">
          <span className="access-entry-icon">
            <WorkspaceIcon name="shield" size={20} />
          </span>
          <h2 id="access-connect-title">Connect to your workspace</h2>
          <p>Enter the organization and local key provided by your administrator.</p>
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
          ) : (
            <Form
              form={form}
              layout="vertical"
              initialValues={{ tenantId: 'T001' }}
              onFinish={(values) => {
                const key = values.key;
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
          <p className="access-connect-footnote">
            Your key is cleared after use. Test-environment keys do not configure production sign-in.
          </p>
        </section>
      </main>
      <footer className="access-public-footer">
        Monergy · {area === 'administration' ? 'Organization Settings' : 'Financial Workspace'}
        <span>Test environment connection</span>
      </footer>
    </div>
  );
}
