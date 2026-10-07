import { ConfigProvider, Drawer, Input, Modal } from 'antd';
import type { InputRef } from 'antd';
import { useEffect, useRef, useState } from 'react';
import type { PropsWithChildren, ReactNode } from 'react';

import { monergyTheme } from './theme';
import { WorkspaceIcon } from './WorkspaceIcon';
import type { WorkspaceIconName } from './WorkspaceIcon';

export interface WorkspaceDestination {
  id: string;
  label: string;
  icon: WorkspaceIconName;
  href?: string;
  group?: string;
}

interface WorkspaceShellProps extends PropsWithChildren {
  destinations: WorkspaceDestination[];
  active: string;
  area: string;
  context?: string;
  onNavigate?: (id: string) => void;
  actions?: ReactNode;
}

export function WorkspaceShell({
  destinations,
  active,
  area,
  context = 'Local workspace',
  onNavigate,
  actions,
  children,
}: WorkspaceShellProps) {
  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  const [commandOpen, setCommandOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [reducedMotion, setReducedMotion] = useState(
    () =>
      typeof window === 'undefined' ||
      window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  );
  const input = useRef<InputRef>(null);

  useEffect(() => {
    const media = window.matchMedia('(prefers-reduced-motion: reduce)');
    const update = () => {
      setReducedMotion(media.matches);
    };
    media.addEventListener('change', update);
    const keyboard = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setCommandOpen((value) => !value);
      }
    };
    window.addEventListener('keydown', keyboard);
    return () => {
      media.removeEventListener('change', update);
      window.removeEventListener('keydown', keyboard);
    };
  }, []);

  const navigate = (destination: WorkspaceDestination) => {
    setCommandOpen(false);
    setMobileOpen(false);
    if (onNavigate) onNavigate(destination.id);
    else if (destination.href) window.location.assign(destination.href);
  };
  const navigation = (
    <nav aria-label="Workspace navigation" className="mw-navigation">
      {destinations.map((destination, index) => (
        <div key={destination.id}>
          {destination.group && destination.group !== destinations[index - 1]?.group && (
            <div className="mw-nav-group">{destination.group}</div>
          )}
          {destination.href && !onNavigate ? (
            <a
              href={destination.href}
              className={`mw-nav-item ${active === destination.id ? 'is-active' : ''}`}
              aria-current={active === destination.id ? 'page' : undefined}
              title={collapsed ? destination.label : undefined}
            >
              <WorkspaceIcon name={destination.icon} />
              <span>{destination.label}</span>
              {active === destination.id && <span className="mw-nav-marker" />}
            </a>
          ) : (
            <button
              type="button"
              className={`mw-nav-item ${active === destination.id ? 'is-active' : ''}`}
              aria-current={active === destination.id ? 'page' : undefined}
              onClick={() => {
                navigate(destination);
              }}
              title={collapsed ? destination.label : undefined}
            >
              <WorkspaceIcon name={destination.icon} />
              <span>{destination.label}</span>
              {active === destination.id && <span className="mw-nav-marker" />}
            </button>
          )}
        </div>
      ))}
    </nav>
  );
  const brand = (
    <a className="mw-brand" href="/" aria-label="Monergy home">
      <span className="mw-mark" aria-hidden="true">
        <svg viewBox="0 0 32 32">
          <path
            d="M5 24V8l11 10L27 8v16"
            fill="none"
            stroke="currentColor"
            strokeWidth="4"
            strokeLinejoin="round"
          />
        </svg>
      </span>
      <span>
        monergy<span className="mw-brand-caption">CONNECTED WORKSPACE</span>
      </span>
    </a>
  );

  return (
    <ConfigProvider
      theme={{ ...monergyTheme, token: { ...monergyTheme.token, motion: !reducedMotion } }}
    >
      <div className={`mw-shell ${collapsed ? 'mw-collapsed' : ''}`}>
        <a href="#workspace-content" className="mw-skip">
          Skip to content
        </a>
        <aside className="mw-sidebar">
          {brand}
          <div className="mw-workspace-label">
            <span className="mw-workspace-avatar">M</span>
            <div>
              <strong>Monergy</strong>
              <small>Your connected workspace</small>
            </div>
          </div>
          {navigation}
          <div className="mw-sidebar-footer">
            <span className="mw-environment-dot" />
            <span>{context}</span>
          </div>
          <button
            className="mw-collapse"
            type="button"
            onClick={() => {
              setCollapsed((value) => !value);
            }}
            aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            aria-expanded={!collapsed}
          >
            <WorkspaceIcon name="menu" />
            <span>Collapse sidebar</span>
          </button>
        </aside>
        <div className="mw-body">
          <header className="mw-topbar">
            <button
              type="button"
              className="mw-icon-button mw-mobile-menu"
              aria-label="Open navigation"
              onClick={() => {
                setMobileOpen(true);
              }}
            >
              <WorkspaceIcon name="menu" />
            </button>
            <div className="mw-breadcrumb">
              <span>Workspace</span>
              <span aria-hidden="true">/</span>
              <strong>{area}</strong>
            </div>
            <div className="mw-top-actions">
              <button
                className="mw-search-trigger"
                type="button"
                onClick={() => {
                  setQuery('');
                  setCommandOpen(true);
                }}
              >
                <WorkspaceIcon name="search" size={17} />
                <span>Find a workspace</span>
                <kbd>Ctrl K</kbd>
              </button>
              {actions}
              <button
                className="mw-icon-button"
                type="button"
                aria-label="Workspace guide"
                onClick={() => {
                  setHelpOpen(true);
                }}
              >
                <WorkspaceIcon name="help" />
              </button>
              <span className="mw-environment">LOCAL</span>
            </div>
          </header>
          <div id="workspace-content" className="mw-content" tabIndex={-1}>
            {children}
          </div>
        </div>
        <Drawer
          title="Monergy workspace"
          placement="left"
          open={mobileOpen}
          onClose={() => {
            setMobileOpen(false);
          }}
          size={280}
          destroyOnHidden
        >
          {navigation}
        </Drawer>
        <Modal
          title="Jump to a workspace"
          open={commandOpen}
          onCancel={() => {
            setCommandOpen(false);
          }}
          footer={null}
          destroyOnHidden
          afterOpenChange={(open) => {
            if (open) input.current?.focus();
          }}
        >
          <Input
            ref={input}
            aria-label="Find a workspace"
            placeholder="Search pages…"
            value={query}
            onChange={(event) => {
              setQuery(event.target.value);
            }}
            size="large"
            prefix={<WorkspaceIcon name="search" />}
          />
          <div className="mw-command-results">
            {destinations
              .filter((destination) =>
                destination.label.toLowerCase().includes(query.toLowerCase()),
              )
              .map((destination) => (
                <button
                  type="button"
                  key={destination.id}
                  onClick={() => {
                    navigate(destination);
                  }}
                >
                  <span>
                    <WorkspaceIcon name={destination.icon} />
                    {destination.label}
                  </span>
                  <WorkspaceIcon name="arrow" size={16} />
                </button>
              ))}
            {!destinations.some((destination) =>
              destination.label.toLowerCase().includes(query.toLowerCase()),
            ) && <p>No matching workspace. Try another name.</p>}
          </div>
        </Modal>
        <Drawer
          title="A connected way to work"
          open={helpOpen}
          onClose={() => {
            setHelpOpen(false);
          }}
          size={420}
          destroyOnHidden
        >
          <div className="mw-guide">
            <div className="mw-guide-icon">
              <WorkspaceIcon name="layers" size={32} />
            </div>
            <h2>One workspace. Clear boundaries.</h2>
            <p>
              Move between financial workflows, access administration and System Expert using a
              consistent navigation and visual language.
            </p>
            <h3>Stay in context</h3>
            <p>
              Details and edits open in drawers, so the current list stays in view. Escape closes a
              drawer and returns focus to its trigger.
            </p>
            <h3>Make it yours</h3>
            <p>
              Collapse the sidebar for more room. Use Ctrl K or Command K to jump to a workspace.
              Motion follows your device’s reduced-motion preference.
            </p>
            <h3>Know where you are</h3>
            <p>
              This workspace uses local services. Access administration requires a current tenant
              session and server-verified administrator authority.
            </p>
          </div>
        </Drawer>
      </div>
    </ConfigProvider>
  );
}
