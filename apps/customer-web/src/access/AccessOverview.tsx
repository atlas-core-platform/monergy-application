import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Skeleton } from 'antd';
import { useEffect, useState } from 'react';
import { AccessApiError, displayError } from './accessApi';
import type { AccessApi, AccessPage } from './accessApi';

export function AccessOverview({
  api,
  onExpired,
}: {
  api: AccessApi;
  onExpired: (failure: unknown) => void;
}) {
  const [snapshot, setSnapshot] = useState<AccessPage[] | null>(null);
  const [error, setError] = useState('');
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    void Promise.all(
      (['members', 'roles', 'permissions'] as const).map((area) =>
        api.list(area, undefined, abort.signal),
      ),
    )
      .then((pages) => {
        if (abort.signal.aborted) return;
        if (pages.some((page) => page.policyVersion !== pages[0]?.policyVersion))
          throw new AccessApiError(409, 'POLICY_VERSION_CONFLICT');
        setSnapshot(pages);
      })
      .catch((failure: unknown) => {
        if (!abort.signal.aborted) {
          setError(displayError(failure));
          onExpired(failure);
        }
      });
    return () => {
      abort.abort();
    };
  }, [api, revision, onExpired]);
  const users = snapshot?.[0];
  const unassigned = users?.items.filter((user) => !user.businessRoleId).length ?? 0;
  return (
    <>
      <header className="access-page-header">
        <div>
          <p className="workspace-eyebrow">YOUR ACCESS WORKSPACE</p>
          <h1>People. Permissions. Clarity.</h1>
          <p>Keep your team organized and make every access decision intentional.</p>
        </div>
        <Button href="/access/users" type="primary" icon={<WorkspaceIcon name="users" size={17} />}>
          Manage users
        </Button>
      </header>
      {error && (
        <Alert
          type="error"
          showIcon
          title={error}
          action={
            <Button
              onClick={() => {
                setError('');
                setSnapshot(null);
                setRevision((value) => value + 1);
              }}
            >
              Try again
            </Button>
          }
        />
      )}
      <section className="access-metrics" aria-label="Access snapshot">
        {(
          [
            {
              label: 'Users',
              href: '/access/users',
              icon: 'users',
              detail: 'People in this tenant',
            },
            {
              label: 'Roles',
              href: '/access/roles',
              icon: 'shield',
              detail: 'Reusable access assignments',
            },
            {
              label: 'Permissions',
              href: '/access/permissions',
              icon: 'check',
              detail: 'Defined actions and scope',
            },
          ] as const
        ).map((item, index) => (
          <a className="access-metric" href={item.href} key={item.label}>
            <div>
              <span>{item.label}</span>
              <WorkspaceIcon name={item.icon} size={20} />
            </div>
            <strong>
              {snapshot ? (
                `${String(snapshot[index]?.items.length ?? 0)}${snapshot[index]?.nextCursor ? '+' : ''}`
              ) : error ? (
                '—'
              ) : (
                <Skeleton.Input active size="small" />
              )}
            </strong>
            <small>{item.detail}</small>
            <WorkspaceIcon name="arrow" size={16} />
          </a>
        ))}
      </section>
      <p className="access-snapshot-note">
        Snapshot of up to 100 records per category. A + means more are available.{' '}
        <Button
          type="link"
          size="small"
          onClick={() => {
            setError('');
            setSnapshot(null);
            setRevision((value) => value + 1);
          }}
        >
          Refresh snapshot
        </Button>
      </p>
      <div className="access-overview-grid">
        <section className="access-task-panel">
          <div className="access-section-title">
            <span className="access-accent-icon">
              <WorkspaceIcon name="workflow" />
            </span>
            <div>
              <h2>Start with a clear purpose</h2>
              <p>Three steps to thoughtful access.</p>
            </div>
          </div>
          {(
            [
              [
                '01',
                'Define what a role can do',
                'Combine permissions into a role that reflects a responsibility.',
                '/access/roles',
                'Configure roles',
              ],
              [
                '02',
                'Bring your people in',
                'Create one user or import a team, then assign a business role.',
                '/access/users',
                'Open users',
              ],
              [
                '03',
                'Review what changed',
                'See who changed access and when the change was recorded.',
                '/access/activity',
                'Review activity',
              ],
            ] as const
          ).map(([number, title, description, href, action]) => (
            <div className="access-task-row" key={number}>
              <span>{number}</span>
              <div>
                <h3>{title}</h3>
                <p>{description}</p>
                <a href={href}>
                  {action} <WorkspaceIcon name="arrow" size={14} />
                </a>
              </div>
            </div>
          ))}
        </section>
        <aside className="access-insight-panel">
          <span className="access-insight-label">ACCESS AT A GLANCE</span>
          <h2>Keep access intentional.</h2>
          {users ? (
            <p>
              <strong>{unassigned}</strong> of the {users.items.length} users loaded have no
              business role. They receive no role-based business permissions.
            </p>
          ) : (
            <p>Review role assignments in Users to understand each person’s configured access.</p>
          )}
          <a href="/access/users">
            Review assignments <WorkspaceIcon name="arrow" size={15} />
          </a>
          <hr />
          <h3>Know the boundaries</h3>
          <p>
            <strong>One business role per user.</strong> Permissions come from that role and
            applicable resource assignments.
          </p>
          <p>
            <strong>Groups organize people.</strong> Joining a group does not grant access.
          </p>
          <p>
            <strong>Administration is separate.</strong> Tenant administrators manage access;
            business data still requires a role.
          </p>
        </aside>
      </div>
    </>
  );
}
