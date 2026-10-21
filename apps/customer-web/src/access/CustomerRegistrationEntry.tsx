import { Alert, Button, Checkbox, Form, Input, Space } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { registrationRequest } from './registrationApi';
interface Publication { revision: string; documents: { kind: string; version: string; title: string; body: string; sha256: string }[] }
interface Challenge { challengeId: string; expiresAt: string; retryAfterSeconds: number }
interface Values { tenantId: string; email: string; password: string; displayName: string; code: string; terms: boolean; privacy: boolean; consent: boolean }
export function CustomerRegistrationEntry({ busy, onSignIn, onCancel }: { busy: boolean; onSignIn: (tenant: string, email: string, password: string) => Promise<void>; onCancel: () => void }) {
 const [form] = Form.useForm<Values>(); const [mode, setMode] = useState<'login' | 'register' | 'recover'>('login');
 const [challenge, setChallenge] = useState<(Challenge & { tenant: string; email: string }) | null>(null);
 const [publication, setPublication] = useState<Publication | null>(null); const [pending, setPending] = useState(false); const [message, setMessage] = useState('');
 const request = useRef<AbortController | null>(null); const epoch = useRef(0);
 useEffect(() => () => { epoch.current++; request.current?.abort(); }, []);
 function reset(next: typeof mode) { epoch.current++; request.current?.abort(); form.resetFields(); setMode(next); setChallenge(null); setPublication(null); setPending(false); setMessage(''); }
 async function submit(v: Values) {
  const id = ++epoch.current; request.current?.abort(); const controller = new AbortController(); request.current = controller; setPending(true); setMessage('');
  try {
   if (mode === 'login') { form.setFieldValue('password', ''); await onSignIn(v.tenantId.trim(), v.email.trim(), v.password); return; }
   if (!challenge) {
    const tenant = v.tenantId.trim(), email = v.email.trim(); let terms: Publication | null = null;
    if (mode === 'register') {
     terms = await registrationRequest<Publication>('/agreements?tenantId=' + encodeURIComponent(tenant), undefined, controller.signal);
     if (!/^[a-f0-9]{64}$/.test(terms.revision) || terms.documents.length !== 3 || new Set(terms.documents.map(d => d.kind)).size !== 3 || !terms.documents.every(d => ['TERMS', 'PRIVACY', 'CONSENT'].includes(d.kind) && d.title && d.body)) throw new Error('Invalid publication');
    }
    const c = await registrationRequest<Challenge>(mode === 'register' ? '/request' : '/recovery/request', { tenantId: tenant, email }, controller.signal);
    if (!/^[a-f0-9]{64}$/.test(c.challengeId) || !Number.isFinite(Date.parse(c.expiresAt)) || Date.parse(c.expiresAt) <= Date.now()) throw new Error('Invalid challenge');
    if (id !== epoch.current) return; setPublication(terms); setChallenge({ ...c, tenant, email });
    setMessage('If this email is eligible, a verification code will arrive shortly. It expires after five minutes.');
   } else {
    const password = v.password; form.setFieldsValue({ password: '', code: '' });
    const common = { tenantId: challenge.tenant, challengeId: challenge.challengeId, code: v.code };
    if (mode === 'register') {
     if (!publication || !v.terms || !v.privacy || !v.consent) throw new Error('Accept each agreement');
     const r = await registrationRequest<{ tenantId: string; actorId: string; customerId: string; active: boolean }>('/confirm', { ...common, displayName: v.displayName.trim(), password, agreementRevision: publication.revision, acceptTerms: v.terms, acknowledgePrivacy: v.privacy, consentToProcessing: v.consent }, controller.signal);
     if (r.tenantId !== challenge.tenant || !r.active || !r.actorId || !r.customerId) throw new Error('Invalid receipt');
    } else await registrationRequest('/recovery/confirm', { ...common, newPassword: password }, controller.signal);
    if (id !== epoch.current) return; reset('login'); setMessage(mode === 'register' ? 'Your account is ready. Sign in with your email and password.' : 'Your password has been changed. Sign in again; previous sessions have ended.');
   }
  } catch { if (id === epoch.current && !controller.signal.aborted) setMessage('Unable to complete this step. Check your details or request a new code. If account creation was interrupted, try signing in to resume it.'); }
  finally { if (id === epoch.current) setPending(false); }
 }
 return <><h2>{mode === 'login' ? 'Sign in with email' : mode === 'register' ? 'Create your account' : 'Reset your password'}</h2>{message && <Alert title={message} type="info" />}
 <Form form={form} layout="vertical" initialValues={{ tenantId: 'T001' }} onFinish={v => { void submit(v); }}>
 {!challenge && <><Form.Item name="tenantId" label="Organization" rules={[{ required: true }]}><Input maxLength={64} disabled={busy || pending} /></Form.Item><Form.Item name="email" label="Email" rules={[{ required: true }, { type: 'email' }]}><Input type="email" autoComplete="email" maxLength={320} disabled={busy || pending} /></Form.Item></>}
 {challenge && <><p>{challenge.email} · {challenge.tenant}</p><Form.Item name="code" label="Verification code" rules={[{ required: true }, { pattern: /^\d{6}$/ }]}><Input autoComplete="one-time-code" inputMode="numeric" maxLength={6} disabled={busy || pending} /></Form.Item></>}
 {challenge && mode === 'register' && <Form.Item name="displayName" label="Your name" rules={[{ required: true }]}><Input maxLength={120} disabled={busy || pending} /></Form.Item>}
 {(mode === 'login' || challenge) && <Form.Item name="password" label={mode === 'login' ? 'Password' : 'Create a password (15–128 characters)'} rules={[{ required: true }, ...(mode === 'login' ? [] : [{ min: 15, max: 128 }])]}><Input.Password autoComplete={mode === 'login' ? 'current-password' : 'new-password'} maxLength={128} disabled={busy || pending} /></Form.Item>}
 {publication && mode === 'register' && <>{publication.documents.map(d => <details key={d.kind}><summary>{d.title} · {d.version}</summary><p style={{ whiteSpace: 'pre-wrap' }}>{d.body}</p></details>)}{(['terms', 'privacy', 'consent'] as const).map((n, i) => <Form.Item key={n} name={n} valuePropName="checked" rules={[{ validator: (_, value: unknown) => value === true ? Promise.resolve() : Promise.reject(new Error('Review and accept to continue.')) }]}><Checkbox disabled={pending}>{['I accept the terms.', 'I acknowledge the privacy policy.', 'I consent to the stated data processing.'][i]}</Checkbox></Form.Item>)}</>}
 <Button type="primary" htmlType="submit" block loading={busy || pending}>{mode === 'login' ? 'Sign in' : challenge ? mode === 'register' ? 'Verify and create account' : 'Verify and reset password' : 'Send verification code'}</Button>
 </Form><Space wrap>{mode !== 'register' && <Button disabled={busy || pending} onClick={() => { reset('register'); }}>Create account</Button>}{mode !== 'recover' && <Button disabled={busy || pending} onClick={() => { reset('recover'); }}>Forgot password</Button>}{mode !== 'login' && <Button disabled={busy || pending} onClick={() => { reset('login'); }}>Back to sign in</Button>}<Button disabled={busy} onClick={() => { reset('login'); onCancel(); }}>Use another sign-in method</Button>{challenge && <Button disabled={busy || pending} onClick={() => { reset(mode); }}>Request a new code</Button>}</Space></>;
}
