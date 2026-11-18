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
import { workspaceAreaForPath } from './workspaceAreaForPath';
const TenantSetupGate = lazy(() =>
  import('./access/TenantSetup').then((module) => ({ default: module.TenantSetupGate })),
);

const Vs02Experience = lazy(() => import('./vs02/Vs02Experience'));
const FinancialQaExperience = lazy(() => import('./product/FinancialQaExperience'));
const ProductSearchExperience = lazy(() => import('./product/ProductSearchExperience'));
const OneDriveExperience = lazy(() => import('./product/OneDriveExperience'));
const GoogleDriveExperience = lazy(() => import('./product/GoogleDriveExperience'));
const ConnectionsOverviewExperience = lazy(() => import('./product/ConnectionsOverviewExperience'));
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
const CustomerAssistanceCases = lazy(() => import('./cases/CustomerAssistanceCases'));
const DsaCaseWorkspace = lazy(() => import('./cases/DsaCaseWorkspace'));
const LenderWorkspace = lazy(() => import('./lender/LenderWorkspace'));
const FinancialProfessionalWorkspace = lazy(() => import('./financial-professional/FinancialProfessionalWorkspace'));
const PropertyValuerWorkspace = lazy(() => import('./property-valuer/PropertyValuerWorkspace'));
const LegalAdvisorWorkspace = lazy(() => import('./legal-advisor/LegalAdvisorWorkspace'));
const OperationsSupportWorkspace = lazy(() => import('./operations-support/OperationsSupportWorkspace'));
const PlatformAdministratorPortal = lazy(() => import('./platform-administration/PlatformAdministratorPortal'));
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
  if (window.location.pathname === '/dsa') return <Suspense fallback={<main aria-busy="true">Loading case workspace…</main>}><DsaCaseWorkspace /></Suspense>;
  if (window.location.pathname === '/lender') return <Suspense fallback={<main aria-busy="true">Loading lender workspace…</main>}><LenderWorkspace /></Suspense>;
  if (window.location.pathname === '/financial-professional') return <Suspense fallback={<main aria-busy="true">Loading Financial Professional workspace…</main>}><FinancialProfessionalWorkspace /></Suspense>;
  if (window.location.pathname === '/property-valuer') return <Suspense fallback={<main aria-busy="true">Loading Property Valuer workspace…</main>}><PropertyValuerWorkspace /></Suspense>;
  if (window.location.pathname === '/legal-advisor') return <Suspense fallback={<main aria-busy="true">Loading Legal Advisor workspace…</main>}><LegalAdvisorWorkspace /></Suspense>;
  if (window.location.pathname === '/operations-support') return <Suspense fallback={<main aria-busy="true">Loading Operations Support workspace…</main>}><OperationsSupportWorkspace /></Suspense>;
  if (window.location.pathname === '/cases') return <Suspense fallback={<main aria-busy="true">Loading assistance cases…</main>}><CustomerAssistanceCases /></Suspense>;
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

  if (window.location.pathname === '/connections/google-drive') return <RequestAccessGate target="customer.data-connections" requiredCapabilities={['integration-gateway.connection.manage']}><Suspense fallback={<main aria-busy="true">Loading connections…</main>}><GoogleDriveExperience /></Suspense></RequestAccessGate>;
  if (window.location.pathname === '/connections/onedrive') return <RequestAccessGate target="customer.data-connections" requiredCapabilities={['integration-gateway.connection.manage']}><Suspense fallback={<main aria-busy="true">Loading connections…</main>}><OneDriveExperience /></Suspense></RequestAccessGate>;
  if (window.location.pathname === '/connections') return <RequestAccessGate target="customer.data-connections" requiredCapabilities={['integration-gateway.connection.manage']}><Suspense fallback={<main aria-busy="true">Loading connections…</main>}><ConnectionsOverviewExperience /></Suspense></RequestAccessGate>;

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
  if (window.location.hostname === 'platform.localhost')
    return <Suspense fallback={<main aria-busy="true">Loading Platform Administration…</main>}><PlatformAdministratorPortal /></Suspense>;
  if (window.location.pathname === '/welcome')
    return <FoundationProvider appearance="midnight"><PersonaWelcomeEntry /></FoundationProvider>;
  if (window.location.pathname === '/platform' || window.location.pathname === '/operations')
    return <Suspense fallback={<main aria-busy="true">Loading Platform Administration…</main>}><PlatformAdministratorPortal /></Suspense>;
  const area = workspaceAreaForPath(window.location.pathname);
  return (
    <WorkspaceSessionProvider area={area}>
      <WorkspaceApplication />
    </WorkspaceSessionProvider>
  );
}

function SessionShell(props: ComponentProps<typeof WorkspaceShell>) {
  const { session, authorization, workspaces = [], selectWorkspace, signOut } = useWorkspaceSession();
  const product = authorization?.area === 'product' ? authorization.context : null;
  const active = workspaces.find(workspace => workspace.area === authorization?.area);
  return (
    <WorkspaceShell
      {...props}
      context={product?.customerName ?? active?.label}
      actions={
        session ? (
          <Space wrap>
            {workspaces.length > 1 && <><span>Current workspace: {active?.label}</span>
              {workspaces.filter(workspace => workspace.area !== authorization?.area).map(workspace =>
                <Button key={workspace.area} type="link" onClick={() => { void selectWorkspace?.(workspace.area); }}>
                  Switch to {workspace.label}
                </Button>)}</>}
            <Button type="text" onClick={() => { void signOut(); }}>Sign out</Button>
          </Space>
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
            '/dsa',
            '/operations-support',
            '/cases',
            '/reports',
            '/search',
            '/financial-qa',
            '/consent', '/connections', '/connections/google-drive', '/connections/onedrive',
            '/profile',
            '/settings/agreements',
            '/vs02',
            '/foundation',
            '/operations', '/platform',
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
  const inAdministration = path === '/access' || path.startsWith('/access/');
  const inAdvisor = path === '/advisor';
  const inDsa = path === '/dsa';
  const inOperationsSupport = path === '/operations-support';
  const activeArea = workspaceAreaForPath(path);
  const advisorNavigation = [{id:'advisor-overview', label:'Overview', icon:'home' as const, href:'/advisor'}];
  const dsaNavigation = [{id:'dsa-cases', label:'Cases', icon:'changes' as const, href:'/dsa'}];
  const operationsSupportNavigation = [{id:'operational-recovery', label:'Tasks needing attention', icon:'changes' as const, href:'/operations-support'}];
  const roleNavigation = activeArea === 'lender' ? [{id:'lender',label:'Lender Workspace',icon:'home' as const,href:'/lender'}]
    : activeArea === 'financial-professional' ? [{id:'financial-professional',label:'Financial Professional',icon:'home' as const,href:'/financial-professional'}]
    : activeArea === 'property-valuer' ? [{id:'property-valuer',label:'Property Valuer',icon:'home' as const,href:'/property-valuer'}]
    : activeArea === 'legal-advisor' ? [{id:'legal-advisor',label:'Legal Advisor',icon:'home' as const,href:'/legal-advisor'}]
    : null;
  const navigation = roleNavigation ?? (inOperationsSupport ? operationsSupportNavigation : inDsa ? dsaNavigation : inAdvisor ? advisorNavigation : path==='/kyc/reviews' ? [{id:'kyc-reviews',label:'KYC reviews',icon:'shield' as const,href:'/kyc/reviews'}] : inAdministration ? accessDestinations : destinations);
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
  const productProtected = ['/', '/actions', '/cases', '/evidence', '/finances', '/net-worth', '/monthly-cashflow', '/financial-qa', '/search', '/reports', '/consent', '/connections', '/connections/google-drive', '/connections/onedrive', '/profile', '/kyc', '/kyc/reviews', '/settings/sessions', '/settings/agreements'].includes(path);
  const gated = inOperationsSupport || inAdvisor || inDsa || inAdministration || productProtected || localUat;
  useEffect(() => {
    document.documentElement.dataset.monergyTheme = 'midnight';
  }, []);
  const setup = authorization?.area === 'product' ? authorization.context.setup : undefined;
  useEffect(() => {
    if (!authorization) return;
    const requested = workspaceAreaForPath(path);
    if (requested !== authorization.area) {
      const target = authorization.area === 'product' ? '/' : authorization.area === 'administration' ? '/access' :
        authorization.area === 'kyc-review' ? '/kyc/reviews' : '/' + authorization.area;
      window.history.replaceState(null, '', target);
      queueMicrotask(() => { window.dispatchEvent(new PopStateEvent('popstate')); });
    }
  }, [authorization, path]);
  const needsSetup = !inAdministration && setup?.required && !setup.ready;
  const shell = needsSetup ? (
    <Suspense fallback={<main aria-busy="true" className="access-loading">Loading account setup…</main>}><CustomerWorkspaceSetupExperience key={JSON.stringify([session?.tenantId, session?.actorId, session?.authenticationContextId, session?.subjectVersion, setup.customerId])} /></Suspense>
  ) : (
    <SessionShell
      appearance="midnight"
      destinations={navigation}
      module={inOperationsSupport ? {
         name: 'Operations Support', homeHref: '/operations-support',
         guide: <p>Review tasks that need attention and safely try again or cancel eligible work. Customer financial information and professional decisions are not shown here.</p>,
       } : inDsa ? {
         name: 'Cases', homeHref: '/dsa',
         guide: <p>Accept a case when you are ready to take responsibility for it. Customer financial information remains separately protected.</p>,
       } : inAdvisor ? {
         name: 'Financial Advisor', homeHref: '/advisor',
         guide: <p>Customers appear here only while your role, assignment and their permission to share are current.</p>,
       } : roleNavigation?.[0] ? { name: roleNavigation[0].label, homeHref: roleNavigation[0].href,
         guide: <p>Only work assigned to your current professional role is available here.</p> } : inAdministration ? accessModule : productModule}
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
        <WorkspaceEntry area={activeArea}>
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
