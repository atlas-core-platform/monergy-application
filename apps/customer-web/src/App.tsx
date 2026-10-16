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
const TenantSetupGate = lazy(() =>
  import('./access/TenantSetup').then((module) => ({ default: module.TenantSetupGate })),
);

const Vs02Experience = lazy(() => import('./vs02/Vs02Experience'));
const ProductSearchExperience = lazy(() => import('./product/ProductSearchExperience'));
const ProductReportsExperience = lazy(() => import('./product/ProductReportsExperience'));
const EvidenceExperience = lazy(() => import('./product/EvidenceExperience'));
const FinancialProfileExperience = lazy(() => import('./product/FinancialProfileExperience'));
const ProductActionCentreExperience = lazy(() => import('./product/ProductActionCentreExperience'));
const AccessExperience = lazy(() => import('./access/AccessExperience'));
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
        <EvidenceExperience />
      </Suspense>
    );
  if (window.location.pathname === '/finances')
    return (
      <Suspense
        fallback={
          <main className="access-loading" aria-busy="true">
            Loading financial information…
          </main>
        }
      >
        <FinancialProfileExperience />
      </Suspense>
    );
  if (window.location.pathname === '/actions')
    return (
      <Suspense
        fallback={
          <main className="access-loading" aria-busy="true">
            Loading Action Centre…
          </main>
        }
      >
        <ProductActionCentreExperience />
      </Suspense>
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
        <ProductReportsExperience />
      </Suspense>
    );
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
        <ProductSearchExperience />
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
  const area =
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
      context={product?.customerName ?? (session ? 'Organization Settings' : undefined)}
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
            '/actions',
            '/access',
            '/reports',
            '/search',
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
  const navigation = inAdministration ? accessDestinations : destinations;
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
  const productProtected = ['/', '/actions', '/evidence', '/finances', '/search', '/reports'].includes(path);
  const gated = inAdministration || productProtected || localUat;
  useEffect(() => {
    document.documentElement.dataset.monergyTheme = 'midnight';
  }, []);
  const shell = (
    <SessionShell
      appearance="midnight"
      destinations={navigation}
      module={inAdministration ? accessModule : productModule}
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
        <WorkspaceEntry area={inAdministration ? 'administration' : 'product'}>
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
