import { Alert, Button, Card, Form, Input, Typography } from 'antd';
import { useEffect, useLayoutEffect, useState } from 'react';

interface Invitation {
  tenantId: string;
  ticket: string;
  mode: 'activation' | 'recovery';
  organization?: string;
  email?: string;
  returnPath: '/' | '/access' | '/advisor' | '/dsa' | '/lender' | '/financial-professional' | '/property-valuer' | '/legal-advisor' | '/operations-support';
}
interface Values { password: string; confirmation: string }
const containsControlCharacter = (value: string) =>
  Array.from(value).some((character) => {
    const codePoint = character.codePointAt(0);
    return codePoint !== undefined && codePoint < 32;
  });

function readInvitation(): Invitation | null {
  const values = new URLSearchParams(window.location.hash.slice(1));
  const tenantId = values.get('tenant') ?? '';
  const ticket = values.get('ticket') ?? '';
  const returnPath = values.get('return') ?? '';
  const mode = values.get('mode') === 'recovery' ? 'recovery' : 'activation';
  const organization = values.get('organization') ?? undefined;
  const email = values.get('email') ?? undefined;
  if (!/^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$/.test(tenantId) ||
      !/^[0-9a-f]{64}$/.test(ticket) ||
      !['/', '/access', '/advisor', '/dsa', '/lender', '/financial-professional', '/property-valuer', '/legal-advisor', '/operations-support'].includes(returnPath)) return null;
  if (organization && (organization.length > 200 || containsControlCharacter(organization))) return null;
  if (email && (email.length > 320 || !email.includes('@') || /\s/.test(email))) return null;
  return { tenantId, ticket, mode, returnPath: returnPath as Invitation['returnPath'], organization, email };
}

export function PersonaWelcomeEntry() {
  const [invitation, setInvitation] = useState(readInvitation);
  const [destination, setDestination] = useState<Invitation['returnPath'] | null>(null);
  const [signInContext, setSignInContext] = useState<{ email?: string } | null>(null);
  const [entry, setEntry] = useState<{ tenantId: string; displayName: string } | null>(null);
  const [entryChecked, setEntryChecked] = useState(false);
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
      setSignInContext(null);
      setInvitation(next);
    };
    window.addEventListener('hashchange', consumeNextLink);
    return () => { window.removeEventListener('hashchange', consumeNextLink); };
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    void fetch('/entry-api/local/v1/context', { cache: 'no-store', redirect: 'error', signal: controller.signal })
      .then(async response => {
        const value = response.ok ? await response.json() as { kind?: unknown; tenantId?: unknown; displayName?: unknown } : null;
        if (!controller.signal.aborted && value?.kind === 'tenant' && typeof value.tenantId === 'string' && typeof value.displayName === 'string')
          setEntry({ tenantId: value.tenantId, displayName: value.displayName });
      }).catch(() => undefined).finally(() => { if (!controller.signal.aborted) setEntryChecked(true); });
    return () => { controller.abort(); };
  }, []);

  async function activate(values: Values) {
    if (invitation?.tenantId !== entry?.tenantId || invitation === null || entry === null || pending) return;
    setPending(true);
    setMessage('');
    try {
      const recoveryOperation = invitation.mode === 'recovery'
        ? crypto.randomUUID().replaceAll('-', '') : undefined;
      const target = invitation.mode === 'recovery'
        ? '/identity-api/local/v1/persona-credentials/tester/recovery/confirm'
        : '/identity-api/local/v1/persona-credentials/tester/activation/confirm';
      const options: RequestInit = {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ tenantId: invitation.tenantId, ticket: invitation.ticket,
          [invitation.mode === 'recovery' ? 'newPassword' : 'password']: values.password,
          ...(recoveryOperation ? { operationId: recoveryOperation } : {}) }),
        cache: 'no-store', redirect: 'error',
      };
      const send = () => fetch(target, options);
      let response: Response;
      try { response = await send(); }
      catch (error) {
        if (!recoveryOperation) throw error;
        response = await send();
      }
      if (recoveryOperation && response.status === 503) response = await send();
      if (!response.ok) {
        setMessage(response.status === 401
          ? 'This welcome link is invalid, expired, or has already been used. Ask your administrator for a new link.'
          : 'Account activation is temporarily unavailable. Try again or ask your administrator for help.');
        return;
      }
      setDestination(invitation.returnPath);
      setSignInContext({ email: invitation.email });
      setInvitation(null);
    } catch {
      setMessage('Account activation is temporarily unavailable. Try again or ask your administrator for help.');
    } finally {
      setPending(false);
    }
  }

  return <main className="access-page">
    <Card className="access-connect-card">
      <Typography.Title level={1}>Welcome to Monergy</Typography.Title>
      {!entryChecked && <p role="status">Checking the organization address…</p>}
      {entryChecked && (!entry || invitation && invitation.tenantId !== entry.tenantId) && !destination && <Alert showIcon type="error"
        title="This welcome link belongs to a different organization address. Ask your administrator for a new link." />}
      {entryChecked && entry && !invitation && !destination && <Alert showIcon type="error"
        title="This welcome link is invalid or incomplete. Ask your administrator to create a new link." />}
      {message && <Alert showIcon type="error" title={message} />}
      {destination ? <>
        <Alert showIcon type="success"
          title="Your account is ready. Continue to sign in." />
        <Button type="primary" href={destination + (signInContext?.email ? '?email=' + encodeURIComponent(signInContext.email) : '')}>Continue to sign in</Button>
      </> : invitation !== null && entry !== null && invitation.tenantId === entry.tenantId && <Form<Values> layout="vertical" onFinish={(values) => { void activate(values); }}>
        <p>Your organization is <strong>{entry.displayName}</strong>{invitation.email ? <> and this invitation is for <strong>{invitation.email}</strong></> : null}. {invitation.mode === 'recovery'
          ? 'Choose a new password. This ends your previous sessions; your administrator cannot see the password or sign in for you.'
          : 'Choose your own password; your administrator cannot see it or sign in for you.'}</p>
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
