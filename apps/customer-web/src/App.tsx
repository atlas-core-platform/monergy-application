import { Button, Card, Descriptions, Modal, Space, Tag, Typography } from 'antd';
import { lazy, Suspense, useEffect, useState } from 'react';
import type { ComponentProps } from 'react';
import { FoundationProvider, WorkspaceShell } from '@monergy/ui-foundation';

import { accessDestinations, accessModule } from './workspace/accessNavigation';
import { destinations, productModule } from './workspace/destinations';
import WorkspaceHome from './workspace/WorkspaceHome';
import './workspace/workspace-home.css';
import { localUat, useWorkspaceSession } from './access/workspaceSession';
import { WorkspaceSessionProvider } from './access/WorkspaceSessionProvider';
import { WorkspaceEntry } from './access/WorkspaceEntry';
import { PersonaWelcomeEntry } from './access/PersonaWelcomeEntry';
import { RequestAccessGate } from './access/RequestAccess';
const TenantSetupGate = lazy(() =>
  import('./access/TenantSetup').then((module) => ({ default: module.TenantSetupGate })),
);

const Vs02Experience = lazy(() => import('./vs02/Vs02Experience'));
const FinancialQaExperience = lazy(() => import('./product/FinancialQaExperience'));
const ProductSearchExperience = lazy(() => import('./product/ProductSearchExperience'));
const OneDriveExperience = lazy(() => import('./product/OneDriveExperience'));
const GoogleDriveExperience = lazy(() => import('./product/GoogleDriveExperience'));
const ConsentExperience = lazy(() => import('./product/ConsentExperience'));
const CustomerWorkspaceSetupExperience = lazy(() => import('./product/CustomerWorkspaceSetupExperience'));
const CustomerAccountAgreementsExperience = lazy(() => import('./product/CustomerAccountAgreementsExperience'));
const BasicKycExperience = lazy(() => import('./product/BasicKycExperience'));
const CustomerProfileExperience = lazy(() => import('./product/CustomerProfileExperience'));
const SelfSessionsExperience = lazy(() => import('./product/SelfSessionsExperience'));
const ProductReportsExperience = lazy(() => import('./product/ProductReportsExperience'));
const EvidenceExperience = lazy(() => import('./product/EvidenceExperience'));
const FinancialProfileExperience = lazy(() => import('./product/FinancialProfileExperience'));
const MonthlyCashflowExperience = lazy(() => import('./product/MonthlyCashflowExperience'));
const NetWorthExperience = lazy(() => import('./product/NetWorthExperience'));
const ProductActionCentreExperience = lazy(() => import('./product/ProductActionCentreExperience'));
const AccessExperience = lazy(() => import('./access/AccessExperience'));
const AdvisorWorkspace = lazy(() => import('./advisor/AdvisorWorkspace'));
const { Paragraph, Text, Title } = Typography;

function ToolchainFoundation() {
  const [detailsOpen, setDetailsOpen] = useState(false);

  return (
    <main className="min-h-screen bg-canvas px-4 py-8 font-sans text-ink sm:px-8 lg:px-12">
      <div className="mx-auto grid max-w-6xl gap-6 lg:grid-cols-[minmax(0,1.35fr)_minmax(20rem,0.65fr)]">
        <section
          aria-labelledby="shell-title"
          className="rounded-panel bg-surface p-6 shadow-panel sm:p-8"
        >
          <Tag color="blue">CONTROLLED IMPLEMENTATION · TOOLCHAIN EVIDENCE</Tag>
          <Title id="shell-title" level={1} className="mt-4">
            Monergy frontend foundation
          </Title>
          <Paragraph className="max-w-2xl text-base text-muted">
            This shell verifies the accepted frontend toolchain. The first bounded business
            implementation is available separately as provider-neutral VS-02 evidence.
          </Paragraph>

          <Space className="mt-8" wrap>
            <Button
              type="primary"
              size="large"
              onClick={() => {
                setDetailsOpen(true);
              }}
            >
              View toolchain evidence
            </Button>
            <Button size="large" href="/vs02">
              Open VS-02 reference flow
            </Button>
            <Button size="large" href="/search">
              Open authorized search
            </Button>
            <Button size="large" href="/reports">
              Open trusted reports
            </Button>
            <Text keyboard>Build {__BUILD_REVISION__.slice(0, 12)}</Text>
          </Space>
        </section>

        <aside aria-labelledby="status-title">
          <Card className="h-full" title={<span id="status-title">Foundation status</span>}>
            <Descriptions column={1} size="small">
              <Descriptions.Item label="Runtime">React 19.3 · Vite 8.3</Descriptions.Item>
              <Descriptions.Item label="Components">Ant Design 6.6.3</Descriptions.Item>
              <Descriptions.Item label="Composition">Tailwind CSS 4.3.3</Descriptions.Item>
              <Descriptions.Item label="State">
                <Tag color="success">TOOLCHAIN READY</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="VS-02">
                <Tag color="success">ACCEPTED · SIMULATOR</Tag>
              </Descriptions.Item>
            </Descriptions>
          </Card>
        </aside>
      </div>

      <Modal
        title="D02 frontend evidence"
        open={detailsOpen}
        destroyOnHidden
        onCancel={() => {
          setDetailsOpen(false);
        }}
        footer={
          <Button
            type="primary"
            onClick={() => {
              setDetailsOpen(false);
            }}
          >
            Close evidence
          </Button>
        }
      >
        <p>
          Ant Design owns this dialog interaction. Tailwind owns the responsive shell layout and
          spacing. Both consume the same governed semantic-token source.
        </p>
      </Modal>
    </main>
  );
}

function RouteContent() {
  if (window.location.pathname === '/advisor') return <Suspense fallback={<main aria-busy="true">Loading Advisor workspace…</main>}><AdvisorWorkspace /></Suspense>;
  if (window.location.pathname === '/financial-qa') return <RequestAccessGate target="customer.financial-qa" requiredCapabilities={['ai-intelligence.analysis.execute', 'search.query.execute', 'financial-profile.profile.read', 'evidence.document.read']}><Suspense fallback={<main aria-busy="true">Loading Q&A…</main>}><FinancialQaExperience /></Suspense></RequestAccessGate>;
  if (window.location.pathname === '/settings/sessions')
    return <Suspense fallback={<main aria-busy="true">Loading sessions…</main>}><SelfSessionsExperience /></Suspense>;
  if (window.location.pathname === '/') return <WorkspaceHome />;
  if (window.location.pathname === '/evidence')
    return (
      <Suspense
        fallback={
          <main className="access-loading" aria-busy="true">
            Loading evidence…
          </main>
        }
      >
        <RequestAccessGate target="customer.documents" requiredCapabilities={['evidence.document.read']}>
          <EvidenceExperience />
        </RequestAccessGate>
      </Suspense>
    );
  if (window.location.pathname === '/monthly-cashflow') return <RequestAccessGate target="customer.calculations" requiredCapabilities={['financial-rules.calculation.execute', 'financial-profile.profile.read']}><Suspense fallback={<main className="access-loading" aria-busy="true">Loading income and expenses…</main>}><MonthlyCashflowExperience /></Suspense></RequestAccessGate>;
  if (window.location.pathname === '/net-worth') return <RequestAccessGate target="customer.calculations" requiredCapabilities={['financial-rules.calculation.execute', 'financial-profile.profile.read']}><Suspense fallback={<main className="access-loading" aria-busy="true">Loading net worth…</main>}><NetWorthExperience /></Suspense></RequestAccessGate>;
  if (window.location.pathname === '/finances')
    return (
      <Suspense
        fallback={
          <main className="access-loading" aria-busy="true">
            Loading financial information…
          </main>
        }
      >
        <RequestAccessGate target="customer.financial-information" requiredCapabilities={['financial-profile.profile.read']}>
          <FinancialProfileExperience />
        </RequestAccessGate>
      </Suspense>
    );
  if (window.location.pathname === '/actions')
    return (
      <RequestAccessGate target="customer.processing-status" requiredCapabilities={['job-management.job.read']}>
        <Suspense
          fallback={
            <main className="access-loading" aria-busy="true">
              Loading Action Centre…
            </main>
          }
        >
          <ProductActionCentreExperience />
        </Suspense>
      </RequestAccessGate>
    );
  if (
    window.location.pathname === '/access' ||
    window.location.pathname.startsWith('/access/') ||
    window.location.pathname === '/operations'
  )
    return (
      <Suspense fallback={<main aria-busy="true">Loading Organization Settings…</main>}>
        <AccessExperience path={window.location.pathname} embedded />
      </Suspense>
    );
  if (window.location.pathname === '/reports') {
    return (
      <Suspense
        fallback={
          <main className="grid min-h-screen place-items-center bg-canvas p-6" aria-busy="true">
            <Text>Loading reports…</Text>
          </main>
        }
      >
        <RequestAccessGate target="customer.reports" requiredCapabilities={['reporting.report.read']}>
          <ProductReportsExperience />
        </RequestAccessGate>
      </Suspense>
    );
  }

  if (window.location.pathname === '/connections/onedrive') return <RequestAccessGate target="customer.data-connections" requiredCapabilities={['integration-gateway.connection.manage']}><Suspense fallback={<main aria-busy="true">Loading connections…</main>}><OneDriveExperience /></Suspense></RequestAccessGate>;
  if (window.location.pathname === '/connections') return <RequestAccessGate target="customer.data-connections" requiredCapabilities={['integration-gateway.connection.manage']}><Suspense fallback={<main aria-busy="true">Loading connections…</main>}><GoogleDriveExperience /></Suspense></RequestAccessGate>;

  if (window.location.pathname === '/consent') {
    return (
      <Suspense fallback={<main className="access-loading" aria-busy="true">Loading sharing permissions…</main>}>
        <ConsentExperience />
      </Suspense>
    );
  }

  if (window.location.pathname === '/settings/agreements') {
    return <Suspense fallback={<main className="access-loading" aria-busy="true">Loading account agreements…</main>}><CustomerAccountAgreementsExperience /></Suspense>;
  }
  if (window.location.pathname === '/kyc' || window.location.pathname === '/kyc/reviews') return <Suspense fallback={<main aria-busy="true">Loading KYC…</main>}><BasicKycExperience /></Suspense>;
  if (window.location.pathname === '/profile') {
    return <Suspense fallback={<main className="access-loading" aria-busy="true">Loading your profile…</main>}><CustomerProfileExperience /></Suspense>;
  }

  if (window.location.pathname === '/search') {
    return (
      <Suspense
        fallback={
          <main className="grid min-h-screen place-items-center bg-canvas p-6" aria-busy="true">
            <Text>Loading search…</Text>
          </main>
        }
      >
        <RequestAccessGate target="customer.search" requiredCapabilities={['search.query.execute']}>
          <ProductSearchExperience />
        </RequestAccessGate>
      </Suspense>
    );
  }

  if (window.location.pathname === '/vs02' && localUat)
    return (
      <main className="access-page">
        <Card title="Document processing is available from Evidence">
          <p>
            Open a structured text document in your Evidence library to schedule processing and
            review candidate source facts. The existing VS-02 simulator remains separately verified.
          </p>
          <Button href="/evidence">Open Evidence</Button>
        </Card>
      </main>
    );
  if (window.location.pathname === '/vs02') {
    return (
      <Suspense
        fallback={
          <main className="grid min-h-screen place-items-center bg-canvas p-6" aria-busy="true">
            <Text>Loading VS-02…</Text>
          </main>
        }
      >
        <Vs02Experience />
      </Suspense>
    );
  }

  return <ToolchainFoundation />;
}

export function App() {
  if (window.location.pathname === '/welcome')
    return <FoundationProvider appearance="midnight"><PersonaWelcomeEntry /></FoundationProvider>;
  const area = window.location.pathname === '/advisor' ? 'advisor' :
    window.location.pathname === '/kyc/reviews' ? 'kyc-review' :
    window.location.pathname === '/access' ||
    window.location.pathname.startsWith('/access/') ||
    window.location.pathname === '/operations'
      ? 'administration'
      : 'product';
  return (
    <WorkspaceSessionProvider area={area} key={area}>
      <WorkspaceApplication />
    </WorkspaceSessionProvider>
  );
}
function SessionShell(props: ComponentProps<typeof WorkspaceShell>) {
  const { session, authorization, signOut } = useWorkspaceSession();
  const product = authorization?.area === 'product' ? authorization.context : null;
  return (
    <WorkspaceShell
      {...props}
      context={product?.customerName ?? (authorization?.area === 'advisor' ? 'Financial Advisor' : session ? 'Organization Settings' : undefined)}
      actions={
        session ? (
          <Button
            type="text"
            onClick={() => {
              void signOut();
            }}
          >
            Sign out
          </Button>
        ) : undefined
      }
    />
  );
}
function WorkspaceApplication() {
  const { authorization, session } = useWorkspaceSession();
  const [path, setPath] = useState(window.location.pathname);
  useEffect(() => {
    const pop = () => {
      setPath(window.location.pathname);
    };
    const navigate = (event: MouseEvent) => {
      if (
        event.defaultPrevented ||
        event.button !== 0 ||
        event.ctrlKey ||
        event.metaKey ||
        event.shiftKey ||
        event.altKey
      )
        return;
      const link = event.target instanceof Element ? event.target.closest('a') : null;
      if (!link || link.target || link.hasAttribute('download')) return;
      const url = new URL(link.href);
      // Crossing into/out of the Advisor security area must remount the
      // WorkspaceSessionProvider; a client-side history push would retain
      // the previous area's verified authorization until a full reload.
      if (url.origin === window.location.origin &&
          (window.location.pathname === '/advisor') !== (url.pathname === '/advisor'))
        return;
      if (
        url.origin !== window.location.origin ||
        !(
          [
            '/',
            '/evidence',
            '/finances',
            '/net-worth', '/monthly-cashflow',
            '/actions',
            '/access',
            '/advisor',
            '/reports',
            '/search',
            '/financial-qa',
            '/consent', '/connections',
            '/profile',
            '/settings/agreements',
            '/vs02',
            '/foundation',
            '/operations',
          ].includes(url.pathname) || url.pathname.startsWith('/access/')
        )
      )
        return;
      event.preventDefault();
      window.history.pushState(null, '', url.pathname + url.search + url.hash);
      setPath(url.pathname);
      window.scrollTo({ top: 0 });
    };
    window.addEventListener('popstate', pop);
    document.addEventListener('click', navigate);
    return () => {
      window.removeEventListener('popstate', pop);
      document.removeEventListener('click', navigate);
    };
  }, []);
  const inAdministration =
    path === '/access' || path.startsWith('/access/') || path === '/operations';
  const inAdvisor = path === '/advisor';
  const advisorNavigation = [{id:'advisor-overview', label:'Overview', icon:'home' as const, href:'/advisor'}];
  const navigation = inAdvisor ? advisorNavigation : path==='/kyc/reviews' ? [{id:'kyc-reviews',label:'KYC reviews',icon:'shield' as const,href:'/kyc/reviews'}] : inAdministration ? accessDestinations : destinations;
  const current =
    navigation.find(
      (destination) =>
        destination.href === path ||
        (path === '/access/users/history' && destination.id === 'users'),
    ) ?? navigation[0];
  useEffect(() => {
    document.title = inAdministration
      ? `${current?.label ?? 'Overview'} · Organization Settings · Monergy`
      : `${current?.label ?? 'Overview'} · Monergy`;
  }, [inAdministration, current?.label]);
  const productProtected = ['/', '/actions', '/evidence', '/finances', '/net-worth', '/monthly-cashflow', '/financial-qa', '/search', '/reports', '/consent', '/connections', '/profile', '/kyc', '/kyc/reviews', '/settings/sessions', '/settings/agreements'].includes(path);
  const gated = inAdvisor || inAdministration || productProtected || localUat;
  useEffect(() => {
    document.documentElement.dataset.monergyTheme = 'midnight';
  }, []);
  const setup = authorization?.area === 'product' ? authorization.context.setup : undefined;
  const needsSetup = !inAdministration && setup?.required && !setup.ready;
  const shell = needsSetup ? (
    <Suspense fallback={<main aria-busy="true" className="access-loading">Loading account setup…</main>}><CustomerWorkspaceSetupExperience key={JSON.stringify([session?.tenantId, session?.actorId, session?.authenticationContextId, session?.subjectVersion, setup.customerId])} /></Suspense>
  ) : (
    <SessionShell
      appearance="midnight"
      destinations={navigation}
      module={inAdvisor ? {
         name: 'Financial Advisor', homeHref: '/advisor',
         guide: <p>Advisor work requires an active role, permitted capability, named customer assignment and current customer consent. Workspace entry does not grant financial access.</p>,
       } : inAdministration ? accessModule : productModule}
      onNavigate={(id) => {
        const href = navigation.find((item) => item.id === id)?.href;
        if (!href) return;
        if (!href.startsWith('/')) {
          window.location.assign(href);
          return;
        }
        window.history.pushState(null, '', href);
        setPath(href);
        window.scrollTo({ top: 0 });
      }}
      active={current?.id ?? 'home'}
      area={current?.label ?? 'Overview'}
    >
      <RouteContent />
    </SessionShell>
  );
  return (
    <FoundationProvider appearance="midnight">
      {gated ? (
        <WorkspaceEntry area={inAdvisor ? 'advisor' : path==='/kyc/reviews'?'kyc-review':inAdministration ? 'administration' : 'product'}>
          {inAdministration ? (
            <Suspense
              fallback={
                <main aria-busy="true" className="access-loading">
                  Checking workspace setup…
                </main>
              }
            >
              <TenantSetupGate>{shell}</TenantSetupGate>
            </Suspense>
          ) : (
            shell
          )}
        </WorkspaceEntry>
      ) : (
        shell
      )}
    </FoundationProvider>
  );
}
