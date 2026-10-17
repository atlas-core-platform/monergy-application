import { Alert, Button, Form, Input, Space } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { AccessApiError } from './accessApi';
import { requestCustomerEmailCode } from './customerEmailApi';
import type { CustomerEmailChallenge } from './customerEmailApi';

export function CustomerEmailEntry({ busy, onVerify, onCancel }: {
  busy: boolean;
  onVerify: (tenant: string, challengeId: string, code: string) => Promise<void>;
  onCancel: () => void;
}) {
  const [form] = Form.useForm<{ tenantId: string; email: string; code: string }>();
  const [pending, setPending] = useState(false);
  const [challenge, setChallenge] = useState<(CustomerEmailChallenge & { tenantId: string; email: string; retryAt: number }) | null>(null);
  const [message, setMessage] = useState('');
  const [now, setNow] = useState(() => Date.now());
  const generation = useRef(0);
  const request = useRef<AbortController | null>(null);
  useEffect(() => () => { generation.current += 1; request.current?.abort(); }, []);
  useEffect(() => {
    if (!challenge) return;
    const timer = window.setInterval(() => { setNow(Date.now()); }, 1000);
    return () => { window.clearInterval(timer); };
  }, [challenge]);
  const reset = () => {
    generation.current += 1;
    request.current?.abort();
    setChallenge(null);
    setPending(false);
    setMessage('');
    form.setFieldValue('code', '');
  };
  const send = async (values: { tenantId: string; email: string }) => {
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    const id = ++generation.current;
    setPending(true);
    setMessage('');
    try {
      const tenant = values.tenantId.trim();
      const result = await requestCustomerEmailCode(tenant, values.email.trim(), controller.signal);
      if (id !== generation.current) return;
      const received = Date.now();
      setNow(received);
      setChallenge({ ...result, tenantId: tenant, email: values.email.trim(), retryAt: received + result.retryAfterSeconds * 1000 });
      form.setFieldValue('code', '');
    } catch (failure) {
      if (id !== generation.current || controller.signal.aborted) return;
      setMessage(failure instanceof AccessApiError && failure.status === 429
        ? 'Too many requests. Wait a minute before trying again.'
        : 'Email sign-in is unavailable. Contact your organization or try again later.');
    } finally { if (id === generation.current) setPending(false); }
  };
  const expired = challenge !== null && now >= Date.parse(challenge.expiresAt);
  return (
    <>
      <p>Use the email registered with your organization. Your existing customer access will be checked.</p>
      {message && <Alert type="error" showIcon title={message} />}
      {challenge && <Alert type="info" showIcon title="If your email is registered for a customer workspace, a code will arrive shortly. It expires five minutes after the request." />}
      {expired && <Alert type="warning" showIcon title="This code has expired. Request a new code to continue." />}
      <Form form={form} layout="vertical" initialValues={{ tenantId: 'T001' }} onFinish={(values) => {
        if (!challenge) { void send(values); return; }
        const code = values.code;
        form.setFieldValue('code', '');
        void onVerify(challenge.tenantId, challenge.challengeId, code);
      }}>
        {!challenge ? <>
          <Form.Item name="tenantId" label="Organization" rules={[{ required: true, message: 'Enter your organization.' }, { pattern: /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$/, message: 'Enter a valid organization identifier.' }]}>
            <Input autoComplete="organization" maxLength={64} disabled={pending || busy} />
          </Form.Item>
          <Form.Item name="email" label="Email" rules={[{ required: true, message: 'Enter your registered email.' }, { type: 'email', message: 'Enter a valid email.' }]}>
            <Input type="email" autoComplete="email" maxLength={320} disabled={pending || busy} />
          </Form.Item>
        </> : <Form.Item name="code" label="Verification code" rules={[{ required: true, message: 'Enter the code from your email.' }, { pattern: /^\d{6}$/, message: 'Enter the six-digit code.' }]}>
          <Input inputMode="numeric" autoComplete="one-time-code" maxLength={6} disabled={busy || pending || expired} />
        </Form.Item>}
        <Button type="primary" htmlType="submit" block loading={pending || busy} disabled={pending || busy || expired}>
          {challenge ? 'Verify and open workspace' : 'Send verification code'}
        </Button>
      </Form>
      <Space orientation="vertical" style={{ width: '100%' }}>
        {challenge && <Button block disabled={pending || busy || now < challenge.retryAt} onClick={() => { void send({ tenantId: challenge.tenantId, email: challenge.email }); }}>
          {now < challenge.retryAt ? `Request another code in ${String(Math.ceil((challenge.retryAt - now) / 1000))}s` : 'Request another code'}
        </Button>}
        <Button block type="text" onClick={() => { reset(); onCancel(); }}>Use another sign-in method</Button>
        {challenge && <Button block type="text" disabled={busy} onClick={reset}>Change organization or email</Button>}
      </Space>
    </>
  );
}
