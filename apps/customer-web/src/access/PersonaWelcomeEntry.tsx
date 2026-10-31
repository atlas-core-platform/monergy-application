import { Alert, Button, Card, Form, Input, Typography } from 'antd';
import { useLayoutEffect, useState } from 'react';

interface Invitation {
  tenantId: string;
  ticket: string;
  returnPath: '/' | '/access' | '/advisor' | '/dsa';
}
interface Values { password: string; confirmation: string }

function readInvitation(): Invitation | null {
  const values = new URLSearchParams(window.location.hash.slice(1));
  const tenantId = values.get('tenant') ?? '';
  const ticket = values.get('ticket') ?? '';
  const returnPath = values.get('return') ?? '';
  if (!/^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$/.test(tenantId) ||
      !/^[0-9a-f]{64}$/.test(ticket) ||
      !['/', '/access', '/advisor', '/dsa'].includes(returnPath)) return null;
  return { tenantId, ticket, returnPath: returnPath as Invitation['returnPath'] };
}

export function PersonaWelcomeEntry() {
  const [invitation, setInvitation] = useState(readInvitation);
  const [destination, setDestination] = useState<Invitation['returnPath'] | null>(null);
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState('');
  // A fragment is not sent to the server; clear it before the browser paints
  // so the single-use ticket is not left visible or retained in navigation.
  useLayoutEffect(() => {
    window.history.replaceState(null, '', '/welcome');
    const consumeNextLink = () => {
      const next = readInvitation();
      window.history.replaceState(null, '', '/welcome');
      setMessage('');
      setDestination(null);
      setInvitation(next);
    };
    window.addEventListener('hashchange', consumeNextLink);
    return () => { window.removeEventListener('hashchange', consumeNextLink); };
  }, []);

  async function activate(values: Values) {
    if (!invitation || pending) return;
    setPending(true);
    setMessage('');
    try {
      const response = await fetch('/identity-api/local/v1/persona-credentials/tester/activation/confirm', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          tenantId: invitation.tenantId,
          ticket: invitation.ticket,
          password: values.password,
        }),
        cache: 'no-store',
        redirect: 'error',
      });
      if (!response.ok) {
        setMessage(response.status === 401
          ? 'This welcome link is invalid, expired, or has already been used. Ask your tester to create a new link.'
          : 'Account activation is temporarily unavailable. Try again or ask your tester for help.');
        return;
      }
      setDestination(invitation.returnPath);
      setInvitation(null);
    } catch {
      setMessage('Account activation is temporarily unavailable. Try again or ask your tester for help.');
    } finally {
      setPending(false);
    }
  }

  return <main className="access-page">
    <Card className="access-connect-card">
      <Typography.Title level={1}>Welcome to Monergy</Typography.Title>
      {!invitation && !destination && <Alert showIcon type="error"
        title="This welcome link is invalid or incomplete. Ask your tester to create a new link." />}
      {message && <Alert showIcon type="error" title={message} />}
      {destination ? <>
        <Alert showIcon type="success"
          title="Your password is set. Sign in normally to verify your current role and workspace access." />
        <Button type="primary" href={destination}>Continue to sign in</Button>
      </> : invitation && <Form<Values> layout="vertical" onFinish={(values) => { void activate(values); }}>
        <p>Your organization is <strong>{invitation.tenantId}</strong>. Choose your own password; your administrator cannot see it or sign in for you.</p>
        <Form.Item name="password" label="Password (15–128 characters)"
          rules={[{ required: true }, { min: 15, max: 128 }]}>
          <Input.Password autoComplete="new-password" maxLength={128} disabled={pending} />
        </Form.Item>
        <Form.Item name="confirmation" label="Confirm password" dependencies={['password']}
          rules={[{ required: true }, ({ getFieldValue }) => ({
            validator: (_, value: unknown) => value === getFieldValue('password')
              ? Promise.resolve() : Promise.reject(new Error('Passwords must match.')),
          })]}>
          <Input.Password autoComplete="new-password" maxLength={128} disabled={pending} />
        </Form.Item>
        <Button htmlType="submit" type="primary" loading={pending} block>Set password</Button>
      </Form>}
    </Card>
  </main>;
}
