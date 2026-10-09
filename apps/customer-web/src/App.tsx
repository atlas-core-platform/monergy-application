import { Button, Card, Descriptions, Modal, Space, Tag, Typography } from 'antd';
import { lazy, Suspense, useEffect, useState } from 'react';
import type { ComponentProps } from 'react';
import { FoundationProvider, WorkspaceShell } from '@monergy/ui-foundation';

import { accessDestinations, accessModule } from './workspace/accessNavigation';
import { destinations } from './workspace/destinations';
import WorkspaceHome from './workspace/WorkspaceHome';
import './workspace/workspace-home.css';
import { localUat, useWorkspaceSession } from './access/workspaceSession';
import { WorkspaceSessionProvider } from './access/WorkspaceSessionProvider';
import { WorkspaceEntry } from './access/WorkspaceEntry';

const Vs02Experience = lazy(() => import('./vs02/Vs02Experience'));
const SearchExperience = lazy(() => import('./search/SearchExperience'));
const ReportsExperience = lazy(() => import('./reports/ReportsExperience'));
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
  if (
    window.location.pathname === '/access' ||
    window.location.pathname.startsWith('/access/') ||
    window.location.pathname === '/operations'
  )
    return (
      <Suspense fallback={<main aria-busy="true">Loading access management…</main>}>
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
        <ReportsExperience />
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
        <SearchExperience />
      </Suspense>
    );
  }

  if (window.location.pathname === '/vs02' && localUat)
    return (
      <main className="access-page">
        <Card title="Evidence journey: integration pending">
          <p>
            Document processing and job consumers are not connected in this Docker UAT profile. Test
            administration, sessions, revocation, audit and service boundaries in People & access.
            The existing VS-02 simulator remains separately verified.
          </p>
          <Button href="/access">Open People & access</Button>
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
  return (
    <WorkspaceSessionProvider>
      <WorkspaceApplication />
    </WorkspaceSessionProvider>
  );
}
function SessionShell(props: ComponentProps<typeof WorkspaceShell>) {
  const { session, signOut } = useWorkspaceSession();
  return (
    <WorkspaceShell
      {...props}
      context={session ? `Tenant ${session.tenantId}` : undefined}
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
  const { status } = useWorkspaceSession();
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
          ['/', '/access', '/reports', '/search', '/vs02', '/foundation', '/operations'].includes(
            url.pathname,
          ) || url.pathname.startsWith('/access/')
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
  const inAccess = path === '/access' || path.startsWith('/access/');
  const navigation = inAccess ? accessDestinations : destinations;
  const current =
    navigation.find(
      (destination) =>
        destination.href === path ||
        (path === '/access/users/history' && destination.id === 'users'),
    ) ?? navigation[0];
  useEffect(() => {
    document.title = inAccess
      ? `${current?.label ?? 'Overview'} · Access Management · Monergy`
      : 'Monergy Workspace';
  }, [inAccess, current?.label]);
  const gated = inAccess || path === '/operations' || localUat;
  const midnight = inAccess || path === '/operations' || (localUat && status !== 'authorized');
  useEffect(() => {
    document.documentElement.dataset.monergyTheme = midnight ? 'midnight' : 'light';
  }, [midnight]);
  const shell = (
    <SessionShell
      appearance={midnight ? 'midnight' : 'light'}
      destinations={navigation}
      module={inAccess ? accessModule : undefined}
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
    <FoundationProvider appearance={midnight ? 'midnight' : 'light'}>
      {gated ? <WorkspaceEntry>{shell}</WorkspaceEntry> : shell}
    </FoundationProvider>
  );
}
