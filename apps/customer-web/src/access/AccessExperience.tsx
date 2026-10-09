import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Empty, Input, Space, Table, Tag, Tooltip, Select } from 'antd';
import type { TableColumnsType } from 'antd';
import { useCallback, useEffect, useState } from 'react';

import { AccessApiError, displayError, recordId } from './accessApi';
import type { AccessApi, AccessArea, AccessPage, AccessRecord } from './accessApi';
import { AccessEditor } from './AccessEditor';
import { ImportDrawer } from './ImportDrawer';
import { AccessOperations, AccessOperationPage } from './AccessOperations';
import { useWorkspaceSession } from './workspaceSession';
import { WorkspaceSessionProvider } from './WorkspaceSessionProvider';
import { WorkspaceEntry } from './WorkspaceEntry';
import { AccessOverview } from './AccessOverview';
import './access.css';

const areas: { value: AccessArea; label: string; description: string }[] = [
  {
    value: 'members',
    label: 'Users',
    description: 'Create user accounts and manage their role, status and administrator authority.',
  },
  {
    value: 'roles',
    label: 'Roles',
    description: 'Define business roles and associate the permissions each role needs.',
  },
  {
    value: 'permissions',
    label: 'Permissions',
    description: 'Choose the capabilities each permission allows.',
  },
  {
    value: 'groups',
    label: 'Groups',
    description: 'Organize people without changing their access.',
  },
  {
    value: 'imports',
    label: 'Onboarding history',
    description: 'Review single-user and bulk onboarding requests, including incomplete requests.',
  },
  {
    value: 'resource-grants',
    label: 'Resource Access',
    description:
      'Assign specific resources to users whose role includes a resource-scoped capability.',
  },
];

export default function AccessExperience({
  path = '/access/users',
  embedded = false,
}: {
  path?: string;
  embedded?: boolean;
}) {
  return (
    <WorkspaceSessionProvider>
      <WorkspaceEntry>
        <AuthenticatedAccess path={path} embedded={embedded} />
      </WorkspaceEntry>
    </WorkspaceSessionProvider>
  );
}

const routes: Record<string, AccessArea> = {
  '/access/users': 'members',
  '/access/users/history': 'imports',
  '/access/roles': 'roles',
  '/access/permissions': 'permissions',
  '/access/groups': 'groups',
  '/access/resources': 'resource-grants',
};
function AuthenticatedAccess({ path, embedded }: { path: string; embedded: boolean }) {
  const { session, api, signOut, recovery } = useWorkspaceSession();
  const [recovering, setRecovering] = useState(false);
  // Lifecycle failures are handled once by the provider-owned API, above every shell/portal.
  const fail = useCallback(() => undefined, []);
  if (!session || !api) return null;
  const area = routes[path];
  return (
    <main className="access-page">
      {!embedded && (
        <div className="access-context-bar">
          <span>
            <WorkspaceIcon name="shield" size={15} /> Tenant <strong>{session.tenantId}</strong>
            <span className="access-context-divider">/</span>Administrator
          </span>
          <Button
            size="small"
            type="text"
            onClick={() => {
              void signOut();
            }}
          >
            Sign out
          </Button>
        </div>
      )}
      {recovery && (
        <Alert
          type="warning"
          showIcon
          className="access-notice"
          title="Review the interrupted onboarding request"
          description="Check its recorded status before starting another request. Reconnecting does not repeat the operation."
          action={
            <Button
              onClick={() => {
                setRecovering(true);
              }}
            >
              Review request
            </Button>
          }
        />
      )}
      {recovering && recovery && (
        <ImportDrawer
          api={api}
          importId={recovery.operationId}
          onClose={() => {
            setRecovering(false);
            api.forgetOperation();
          }}
          onChanged={() => undefined}
          onExpired={fail}
        />
      )}
      {path === '/access' ? (
        <AccessOverview api={api} onExpired={fail} />
      ) : area ? (
        <Administration key={area} area={area} api={api} onExpired={fail} />
      ) : path === '/access/activity' || path === '/access/sessions' ? (
        <AccessOperationPage
          key={path}
          view={path.endsWith('activity') ? 'audit' : 'sessions'}
          api={api}
          onExpired={fail}
        />
      ) : path === '/operations' ? (
        <>
          <header className="access-page-header">
            <div>
              <p className="workspace-eyebrow">LOCAL UAT · OPERATOR TOOLS</p>
              <h1>Environment operations</h1>
              <p>Inspect local service connections and the remaining release gates.</p>
            </div>
          </header>
          <AccessOperations api={api} onExpired={fail} views={['services', 'readiness']} />
        </>
      ) : (
        <Empty description="This access management page does not exist.">
          <Button href="/access">Return to overview</Button>
        </Empty>
      )}
    </main>
  );
}
function Administration({
  area,
  api,
  onExpired,
}: {
  area: AccessArea;
  api: AccessApi;
  onExpired: (failure: unknown) => void;
}) {
  const [roleNames, setRoleNames] = useState<Record<string, string>>({});
  const [page, setPage] = useState<AccessPage | null>(null);
  const [query, setQuery] = useState('');
  const [statusFilter, setStatusFilter] = useState('all');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [editor, setEditor] = useState<{ record?: AccessRecord } | null>(null);
  const [importId, setImportId] = useState<string | null | undefined>(undefined);
  const [single, setSingle] = useState(false);
  const [revision, setRevision] = useState(0);
  const fail = useCallback(
    (failure: unknown) => {
      if (failure instanceof AccessApiError && (failure.status === 401 || failure.status === 403)) {
        onExpired(failure);
        return;
      }
      setError(displayError(failure));
    },
    [onExpired],
  );
  // A new tenant/session mounts a new Administration component. Cancelled loads
  // cannot repopulate the next tenant's view or replace a newer selected page.
  useEffect(() => {
    const controller = new AbortController();
    void api
      .list(area, undefined, controller.signal)
      .then(async (result) => {
        const roles =
          area === 'members' ? await api.all('roles', controller.signal, result.policyVersion) : [];
        if (!controller.signal.aborted) {
          setRoleNames(
            roles.reduce<Record<string, string>>((names, role) => {
              if (role.id) names[role.id] = role.label ?? role.code ?? role.id;
              return names;
            }, {}),
          );
          setPage(result);
          setError('');
        }
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) fail(failure);
      });
    return () => {
      controller.abort();
    };
  }, [api, area, revision, fail]);
  const refresh = () => {
    setPage(null);
    setRevision((value) => value + 1);
  };
  const changed = (message: string) => {
    setNotice(message);
    refresh();
  };
  const loadMore = async () => {
    if (!page?.nextCursor) return;
    setBusy(true);
    try {
      const next = await api.list(area, page.nextCursor);
      if (next.policyVersion !== page.policyVersion)
        throw new AccessApiError(409, 'POLICY_VERSION_CONFLICT');
      setPage({ ...next, items: [...page.items, ...next.items] });
    } catch (failure) {
      fail(failure);
    } finally {
      setBusy(false);
    }
  };
  const selected = areas.find((item) => item.value === area) ?? areas[0];
  const filtered =
    page?.items.filter(
      (record) =>
        [
          record.normalizedEmail,
          record.label,
          record.code,
          record.status,
          record.resourceId,
          record.actorId,
          record.businessRoleId ? roleNames[record.businessRoleId] : undefined,
        ].some((value) => value?.toLowerCase().includes(query.toLowerCase())) &&
        (statusFilter === 'all' || record.status === statusFilter),
    ) ?? [];
  const columns: TableColumnsType<AccessRecord> = [
    {
      title:
        area === 'members'
          ? 'User'
          : area === 'imports'
            ? 'Import'
            : area === 'resource-grants'
              ? 'Resource'
              : 'Name',
      key: 'name',
      render: (_, record) => (
        <button
          className="access-record-button"
          type="button"
          onClick={() => {
            if (area === 'imports') setImportId(record.id);
            else setEditor({ record });
          }}
        >
          <span className={`access-record-avatar ${area === 'members' ? '' : 'is-object'}`}>
            {area === 'members' ? (
              (record.normalizedEmail ?? '?').slice(0, 2).toUpperCase()
            ) : (
              <WorkspaceIcon name={area === 'groups' ? 'users' : 'shield'} size={18} />
            )}
          </span>
          <span>
            <strong>
              {record.normalizedEmail ?? record.label ?? record.resourceId ?? record.id}
            </strong>
            <small>{record.code ?? (area === 'members' ? '' : (record.capabilityId ?? ''))}</small>
          </span>
        </button>
      ),
    },
    ...(area === 'members'
      ? [
          {
            title: 'Status',
            key: 'status',
            render: (_: unknown, record: AccessRecord) => (
              <Tag color={record.status === 'Active' ? 'success' : 'default'}>{record.status}</Tag>
            ),
          },
          {
            title: 'Business role',
            key: 'role',
            render: (_: unknown, record: AccessRecord) =>
              record.businessRoleId ? (
                (roleNames[record.businessRoleId] ?? 'Business role assigned')
              ) : (
                <span className="access-secondary">None assigned</span>
              ),
          },
          {
            title: 'Administrator',
            key: 'authority',
            render: (_: unknown, record: AccessRecord) => (
              <span className="access-secondary">
                {record.tenantAdmin ? 'Tenant administrator' : 'No'}
              </span>
            ),
          },
        ]
      : area === 'imports'
        ? [
            {
              title: 'State',
              dataIndex: 'status',
              key: 'status',
              render: (value: string) => (
                <Tag
                  color={
                    value === 'Activated' ? 'success' : value === 'Failed' ? 'error' : 'default'
                  }
                >
                  {value}
                </Tag>
              ),
            },
            { title: 'People', dataIndex: 'rowCount', key: 'rows' },
          ]
        : [
            {
              title: 'Includes',
              key: 'includes',
              render: (_: unknown, record: AccessRecord) => (
                <span className="access-secondary">
                  {area === 'permissions'
                    ? `${String(record.grants?.length ?? 0)} capabilities`
                    : area === 'roles'
                      ? `${String(record.permissionIds?.length ?? 0)} permissions`
                      : area === 'groups'
                        ? `${String(record.memberCount ?? 0)} people`
                        : record.actorId}
                </span>
              ),
            },
          ]),
    {
      title: <span className="sr-only">Actions</span>,
      key: 'open',
      width: 108,
      render: (_, record) => (
        <Tooltip title="Open details">
          <Button
            type="text"
            aria-label={`Open ${record.normalizedEmail ?? record.label ?? record.id ?? 'details'}`}
            icon={<WorkspaceIcon name="arrow" size={15} />}
            iconPlacement="end"
            onClick={() => {
              if (area === 'imports') setImportId(record.id);
              else setEditor({ record });
            }}
          >
            Manage
          </Button>
        </Tooltip>
      ),
    },
  ];
  return (
    <>
      <header className="access-page-header">
        <div>
          <h1>{selected?.label}</h1>
          <p>{selected?.description}</p>
        </div>
        <Space wrap>
          {area === 'members' && (
            <Button
              disabled={!page}
              icon={<WorkspaceIcon name="upload" size={16} />}
              onClick={() => {
                setSingle(false);
                setImportId(null);
              }}
            >
              Import users
            </Button>
          )}
          {area === 'imports' ? <Button href="/access/users">Back to users</Button> : null}
          <Button
            type="primary"
            disabled={!page}
            icon={<WorkspaceIcon name={area === 'members' ? 'users' : 'access'} size={16} />}
            onClick={() => {
              if (area === 'members' || area === 'imports') {
                setSingle(area === 'members');
                setImportId(null);
              } else setEditor({});
            }}
          >
            {area === 'members'
              ? 'Create user'
              : area === 'imports'
                ? 'Import users'
                : `Create ${area === 'resource-grants' ? 'resource access' : area.slice(0, -1)}`}
          </Button>
        </Space>
      </header>
      {error && (
        <Alert
          type="error"
          showIcon
          title={error}
          action={
            <Button size="small" onClick={refresh}>
              Refresh
            </Button>
          }
          className="access-notice"
        />
      )}
      {notice && (
        <Alert
          type="success"
          showIcon
          title={notice}
          closable={{
            onClose: () => {
              setNotice('');
            },
          }}
          className="access-notice"
        />
      )}
      <section className="access-panel" aria-label={selected?.label} aria-busy={!page && !error}>
        {!page && !error && (
          <span className="sr-only" role="status">
            Loading records…
          </span>
        )}
        <div className="access-search-row">
          <Input
            allowClear
            prefix={<WorkspaceIcon name="search" size={17} />}
            aria-label="Filter loaded records"
            placeholder={
              area === 'members' ? 'Filter loaded users by email or role' : 'Filter loaded records…'
            }
            value={query}
            onChange={(event) => {
              setQuery(event.target.value);
            }}
          />
          {area === 'members' && (
            <Select
              className="access-status-filter"
              aria-label="Filter loaded users by status"
              value={statusFilter}
              onChange={setStatusFilter}
              options={[
                { value: 'all', label: 'All statuses' },
                { value: 'Active', label: 'Active' },
                { value: 'Disabled', label: 'Disabled' },
                { value: 'PendingIdentity', label: 'Pending identity' },
              ]}
            />
          )}
          <Button className="access-refresh" onClick={refresh} disabled={busy || (!page && !error)}>
            Refresh
          </Button>
        </div>
        <Table<AccessRecord>
          columns={columns}
          dataSource={filtered}
          rowKey={recordId}
          pagination={false}
          loading={!page && !error}
          scroll={filtered.length ? { x: area === 'members' ? 850 : 620 } : undefined}
          locale={{
            emptyText: (
              <Empty
                description={
                  error
                    ? 'Records could not be loaded. Refresh to try again.'
                    : !page
                      ? 'Loading records…'
                      : query || statusFilter !== 'all'
                        ? 'No loaded records match. Clear the filter or load more records.'
                        : `No ${selected?.label.toLowerCase() ?? 'records'} yet. Use the action above to get started.`
                }
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              >
                {page && (query || statusFilter !== 'all') && (
                  <Button
                    onClick={() => {
                      setQuery('');
                      setStatusFilter('all');
                    }}
                  >
                    Clear filters
                  </Button>
                )}
              </Empty>
            ),
          }}
        />
        {page && (
          <div className="access-table-footer" role="status">
            <span>
              {filtered.length} of {page.items.length} loaded records · Filters apply only to loaded
              records
            </span>
            <span>{page.nextCursor ? 'More records available' : 'No more records to load'}</span>
          </div>
        )}
        {page?.nextCursor && (
          <div className="access-load-more">
            <Button
              onClick={() => {
                void loadMore();
              }}
              loading={busy}
            >
              Load more
            </Button>
          </div>
        )}
      </section>
      <div className="access-directory-support">
        <div className="access-bottom-note">
          <WorkspaceIcon name="shield" size={17} />
          <p>
            Tenant administrators manage access. Business permissions come from the person’s
            assigned role and applicable resource grants.
          </p>
        </div>
        {area === 'members' && (
          <Button type="text" href="/access/users/history">
            Onboarding history
          </Button>
        )}
      </div>
      {editor && page && (
        <AccessEditor
          key={`${area}:${editor.record ? recordId(editor.record) : 'new'}`}
          area={area}
          record={editor.record}
          api={api}
          policyVersion={page.policyVersion}
          onClose={(reload) => {
            setEditor(null);
            if (reload) refresh();
          }}
          onSaved={(message) => {
            setEditor(null);
            changed(message);
          }}
          onExpired={fail}
          onReload={async () => {
            // Complete a revision-consistent read before enabling another edit. No mutation replay.
            const first = await api.list(area);
            const items = first.nextCursor
              ? await api.all(area, undefined, first.policyVersion)
              : first.items;
            const roles =
              area === 'members' ? await api.all('roles', undefined, first.policyVersion) : [];
            setRoleNames(
              Object.fromEntries(
                roles.map((role) => [role.id ?? '', role.label ?? role.code ?? role.id ?? '']),
              ),
            );
            setPage({ ...first, items, nextCursor: null });
            setEditor(null);
            setNotice(
              'Current records reloaded from the service. Check the saved values before making another change.',
            );
          }}
        />
      )}
      {importId !== undefined && (
        <ImportDrawer
          api={api}
          single={single && !importId}
          importId={importId}
          onClose={() => {
            setImportId(undefined);
            refresh();
          }}
          onChanged={changed}
          onExpired={fail}
        />
      )}
    </>
  );
}
