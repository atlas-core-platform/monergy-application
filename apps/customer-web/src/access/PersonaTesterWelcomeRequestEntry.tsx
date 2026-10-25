import { Alert, Button, Form, Input } from 'antd';
import { useEffect, useRef, useState } from 'react';

// This is a deliberately separate LOCAL/UAT tester action, not a public
// email-ownership lookup. A trusted operator supplies a protected tester
// capability locally; it is not persisted, logged, or passed in a URL.
interface Values { tenantId: string; email: string; testerSecret: string }
interface Receipt { welcomePath: string; expiresAt: string }
const validDestination = (value: string) => value.startsWith('/welcome#tenant=') &&
  !value.includes('://') && !value.includes('\\') && value.length < 512;

export function PersonaTesterWelcomeRequestEntry({
  returnPath, onBack,
}: {
  returnPath: '/' | '/access' | '/advisor';
  onBack: () => void;
}) {
  const [form] = Form.useForm<Values>();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [welcome, setWelcome] = useState<string | null>(null);
  const active = useRef<AbortController | null>(null);
  useEffect(() => () => { active.current?.abort(); }, []);
  async function requestWelcome(values: Values) {
    active.current?.abort();
    const controller = new AbortController();
    active.current = controller;
    setPending(true);
    setError('');
    setWelcome(null);
    const secret = values.testerSecret;
    form.setFieldValue('testerSecret', '');
    try {
      const response = await fetch('/identity-api/local/v1/persona-credentials/tester/welcome-link', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Monergy-Tester-Activation': secret,
        },
        body: JSON.stringify({
          tenantId: values.tenantId.trim(),
          email: values.email.trim(),
          returnPath,
        }),
        cache: 'no-store', redirect: 'error', signal: controller.signal,
      });
      if (!response.ok) throw new Error('WELCOME_UNAVAILABLE');
      const receipt = await response.json() as Receipt;
      if (!validDestination(receipt.welcomePath) ||
          !Number.isFinite(Date.parse(receipt.expiresAt)) ||
          Date.parse(receipt.expiresAt) <= Date.now())
        throw new Error('WELCOME_INVALID');
      if (!controller.signal.aborted) setWelcome(receipt.welcomePath);
    } catch {
      if (!controller.signal.aborted)
        setError('The protected tester welcome link is unavailable. Confirm your LOCAL tester authorization and the pre-provisioned account, or ask the test operator.');
    } finally {
      if (!controller.signal.aborted) setPending(false);
    }
  }
  return <>
    <h2>Open your LOCAL welcome page</h2>
    <Alert showIcon type="info"
      title="Development tester access only"
      description="This simulated invitation is for an authorized LOCAL/UAT tester. It does not verify control of an email inbox. Your Organization Administrator does not set your password and cannot sign in as you." />
    {error && <Alert showIcon type="warning" title={error} />}
    {welcome ? <>
      <Alert showIcon type="success" title="Protected welcome page is ready"
        description="Open the welcome page to choose your own password. This link is single-use and expires after five minutes." />
      <Button block type="primary" onClick={() => { window.location.assign(welcome); }}>
        Open activation page
      </Button>
      <Button block onClick={() => { setWelcome(null); }}>Request another link</Button>
    </> : <Form<Values> form={form} layout="vertical" initialValues={{ tenantId: 'T001' }}
      onFinish={values => { void requestWelcome(values); }}>
      <Form.Item name="tenantId" label="Organization"
        rules={[{ required: true }, { pattern: /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$/ }]}>
        <Input autoComplete="organization" maxLength={64} disabled={pending} />
      </Form.Item>
      <Form.Item name="email" label="Your invited email address"
        rules={[{ required: true }, { type: 'email' }]}>
        <Input type="email" autoComplete="email" maxLength={320} disabled={pending} />
      </Form.Item>
      <Form.Item name="testerSecret" label="Protected LOCAL tester authorization"
        extra="Available only from the authorized local test operator. Never enter your Monergy password here."
        rules={[{ required: true }]}>
        <Input.Password autoComplete="off" disabled={pending} />
      </Form.Item>
      <Button type="primary" block htmlType="submit" loading={pending}>
        Request protected welcome link
      </Button>
    </Form>}
    <Button block type="text" disabled={pending} onClick={() => {
      active.current?.abort();
      setWelcome(null);
      onBack();
    }}>Back to sign in</Button>
  </>;
}
