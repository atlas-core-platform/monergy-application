import { Alert, Button, Card, Space, Steps } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';
import CustomerProfileExperience from './CustomerProfileExperience';
import CustomerAccountAgreementsExperience from './CustomerAccountAgreementsExperience';
import ConsentExperience from './ConsentExperience';
import SelfSessionsExperience from './SelfSessionsExperience';
import './product.css';

export default function CustomerWorkspaceSetupExperience() {
  const { authorization, refreshContext, signOut } = useWorkspaceSession();
  const setup = authorization?.area === 'product' ? authorization.context.setup : undefined;
  const [page, setPage] = useState(window.location.pathname === '/settings/agreements' ? 'agreements' : 'profile');
  const [busy, setBusy] = useState(false); const [error, setError] = useState('');
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  if (!setup?.required || setup.ready) return null;
  const reload = async () => {
    if (busy) return; setBusy(true); setError('');
    try { await refreshContext?.(); }
    catch { if (mounted.current) setError('Your setup status could not be verified. Reconnect before continuing.'); }
    finally { if (mounted.current) setBusy(false); }
  };
  const Root = page === 'privacy' ? 'section' : 'main';
  return <Root className="product-page" aria-labelledby="customer-setup-title">
    <header className="product-page-header"><div><p className="workspace-eyebrow">WELCOME TO MONERGY</p><h1 id="customer-setup-title">Finish setting up your account</h1>
      <p>Verify your email, review your profile and record your agreement choices to open your financial workspace.</p></div>
      <Space><Button loading={busy} onClick={() => { void reload(); }}>Check setup status</Button><Button onClick={() => { void signOut(); }}>Sign out</Button></Space></header>
    {error && <Alert showIcon type="error" title={error} />}
    <Card><Steps items={[
      { title: 'Verify email', status: setup.emailVerified ? 'finish' : 'process' },
      { title: 'Review profile', status: setup.profileReviewed ? 'finish' : 'process' },
      { title: 'Review agreements', status: setup.agreementsAccepted ? 'finish' : 'process' },
    ]} /></Card>
    {!setup.emailVerified && <Alert showIcon type="info" title="Email verification is still required."
      description="Sign out, then sign in with the verification code sent to your registered email address." />}
    {!setup.agreementsPublished && <Alert showIcon type="info" title="Your organization has not published the required account agreements yet."
      description="You can review your profile while you wait for the documents to become available." />}
    <Space wrap>
      <Button type={page === 'profile' ? 'primary' : 'default'} onClick={() => { setPage('profile'); }}>Review your profile</Button>
      <Button type={page === 'agreements' ? 'primary' : 'default'} onClick={() => { setPage('agreements'); }}>Review account agreements</Button>
      <Button onClick={() => { setPage('privacy'); }}>Manage sharing permissions</Button>
      <Button onClick={() => { setPage('sessions'); }}>Manage sessions</Button>
    </Space>
    {page === 'profile' ? <CustomerProfileExperience /> : page === 'agreements' ? <CustomerAccountAgreementsExperience /> : page === 'privacy' ? <ConsentExperience /> : <SelfSessionsExperience />}
  </Root>;
}
