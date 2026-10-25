import { Alert, Button, Form, Input, Space } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { AccessApiError } from './accessApi';
import { PersonaTesterWelcomeRequestEntry } from './PersonaTesterWelcomeRequestEntry';

type Mode = 'login' | 'activate' | 'recover';
interface Values {
  tenantId: string; email: string; password: string; code: string;
}
interface Challenge {
  challengeId: string; expiresAt: string;
  tenantId: string; email: string;
}
async function personaRequest<T>(path: string, body: object, signal: AbortSignal): Promise<T> {
  const response = await fetch('/identity-api/local/v1/persona-credentials' + path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
    cache: 'no-store', redirect: 'error', signal,
  });
  if (!response.ok) throw new AccessApiError(response.status, 'PERSONA_ACTIVATION_FAILED');
  return await response.json() as T;
}

export function PersonaCredentialEntry({
  busy, onSignIn, onCancel, activationMode = 'email', returnPath = '/',
}: {
  busy: boolean;
  onSignIn: (tenant: string, email: string, password: string) => Promise<void>;
  onCancel: () => void;
  activationMode?: 'email' | 'tester-link';
  returnPath?: '/' | '/access' | '/advisor';
}) {
  const [form] = Form.useForm<Values>();
  const [mode, setMode] = useState<Mode>('login');
  const [testerWelcome, setTesterWelcome] = useState(false);
  const [challenge, setChallenge] = useState<Challenge | null>(null);
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState('');
  const request = useRef<AbortController | null>(null);
  const epoch = useRef(0);
  useEffect(() => () => {
    epoch.current++;
    request.current?.abort();
  }, []);

  function changeMode(next: Mode) {
    epoch.current++;
    request.current?.abort();
    setMode(next);
    setChallenge(null);
    setPending(false);
    setMessage('');
    form.resetFields();
  }

  async function submit(values: Values) {
    const id = ++epoch.current;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setPending(true);
    setMessage('');
    try {
      if (mode === 'login') {
        form.setFieldValue('password', '');
        await onSignIn(values.tenantId.trim(), values.email.trim(), values.password);
        return;
      }
      if (!challenge) {
        const tenantId = values.tenantId.trim();
        const email = values.email.trim();
        const response = await personaRequest<{ challengeId: string; expiresAt: string }>(
          mode === 'activate' ? '/activation/request' : '/recovery/request',
          { tenantId, email }, controller.signal,
        );
        if (!/^[0-9a-f]{64}$/.test(response.challengeId) ||
            !Number.isFinite(Date.parse(response.expiresAt)) ||
            Date.parse(response.expiresAt) <= Date.now())
          throw new Error('INVALID_EMAIL_CHALLENGE');
        if (id !== epoch.current) return;
        setChallenge({ ...response, tenantId, email });
        setMessage('If this account is eligible, an email verification code will arrive. It expires after five minutes.');
      } else {
        const password = values.password;
        form.setFieldsValue({ password: '', code: '' });
        const common = {
          tenantId: challenge.tenantId, challengeId: challenge.challengeId,
          code: values.code,
        };
        if (mode === 'activate') {
          const result = await personaRequest<{ activated: boolean }>(
            '/activation/confirm', { ...common, password }, controller.signal,
          );
          if (!result.activated) throw new Error('ACTIVATION_NOT_CONFIRMED');
        } else {
          const result = await personaRequest<{ recovered: boolean }>(
            '/recovery/confirm', { ...common, newPassword: password }, controller.signal,
          );
          if (!result.recovered) throw new Error('RECOVERY_NOT_CONFIRMED');
        }
        if (id !== epoch.current) return;
        const notice = mode === 'activate'
          ? 'Your password is active. Sign in with your email and password.'
          : 'Your password has been reset. Previous sessions have been revoked. Sign in again.';
        changeMode('login');
        setMessage(notice);
      }
    } catch {
      if (id === epoch.current && !controller.signal.aborted)
        setMessage('This step could not be confirmed. Verify your account details and code, or request another code.');
    } finally {
      if (id === epoch.current) setPending(false);
    }
  }

  if (activationMode === 'tester-link' && testerWelcome)
    return <PersonaTesterWelcomeRequestEntry returnPath={returnPath}
      onBack={() => { setTesterWelcome(false); }} />;

  return <>
    <h2>{mode === 'login' ? 'Sign in to Monergy' : mode === 'activate'
      ? 'Activate your account' : 'Reset your password'}</h2>
    {message && <Alert showIcon type="info" title={message} />}
    {activationMode === 'tester-link' && <Alert showIcon type="info"
      title="First time here? Your invited account needs activation."
      description="A protected LOCAL/UAT tester can open a simulated invitation. This does not prove mailbox ownership." />}
    {activationMode === 'tester-link' && <Button block
      onClick={() => { setTesterWelcome(true); }}>
      Activate your invited account (LOCAL/UAT)
    </Button>}
    <Form form={form} layout="vertical" initialValues={{ tenantId: 'T001' }}
      onFinish={(values) => { void submit(values); }}>
      {!challenge && <>
        <Form.Item label="Organization" name="tenantId" rules={[
          { required: true },
          { pattern: /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$/, message: 'Enter a valid organization.' },
        ]}>
          <Input autoComplete="organization" maxLength={64} disabled={busy || pending} />
        </Form.Item>
        <Form.Item label="Email address" name="email"
          rules={[{ required: true }, { type: 'email' }]}>
          <Input type="email" autoComplete="email" maxLength={320} disabled={busy || pending} />
        </Form.Item>
      </>}
      {challenge && <>
        <p>{challenge.email} · {challenge.tenantId}</p>
        <Form.Item name="code" label="Six-digit verification code" rules={[
          { required: true }, { pattern: /^\d{6}$/, message: 'Enter the six-digit email code.' },
        ]}>
          <Input autoComplete="one-time-code" inputMode="numeric" maxLength={6}
            disabled={busy || pending} />
        </Form.Item>
      </>}
      {(mode === 'login' || challenge) && <Form.Item name="password"
        label={mode === 'login' ? 'Password' : 'New password (15–128 characters)'}
        rules={[{ required: true }, ...(mode === 'login' ? [] : [{ min: 15, max: 128 }])]}>
        <Input.Password autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
          maxLength={128} disabled={busy || pending} />
      </Form.Item>}
      <Button htmlType="submit" type="primary" block loading={busy || pending}>
        {mode === 'login' ? 'Sign in' : challenge
          ? mode === 'activate' ? 'Verify and activate' : 'Verify and reset'
          : 'Send verification code'}
      </Button>
    </Form>
    <Space wrap>
      {activationMode === 'email' && mode !== 'activate' && <Button disabled={busy || pending}
        onClick={() => { changeMode('activate'); }}>Activate account</Button>}
      {activationMode === 'email' && mode !== 'recover' && <Button disabled={busy || pending}
        onClick={() => { changeMode('recover'); }}>Forgot password</Button>}
      {mode !== 'login' && <Button disabled={busy || pending}
        onClick={() => { changeMode('login'); }}>Back to sign in</Button>}
      <Button onClick={() => { changeMode('login'); onCancel(); }}>
        Use another sign-in method
      </Button>
      {challenge && <Button disabled={busy || pending} onClick={() => { changeMode(mode); }}>
        Request a new code
      </Button>}
    </Space>
  </>;
}
