import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Card, Empty, Form, Input, Space, Table, Tag, Tooltip } from 'antd';
import type { TableColumnsType } from 'antd';
import { useCallback, useEffect, useMemo, useState } from 'react';

import { AccessApi, AccessApiError, displayError, establishSession, recordId } from './accessApi';
import type { AccessArea, AccessPage, AccessRecord, TenantSession } from './accessApi';
import { AccessEditor } from './AccessEditor';
import { ImportDrawer } from './ImportDrawer';
import { AccessOperations, AccessOperationPage } from './AccessOperations';
import { useWorkspaceSession } from './workspaceSession';
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
    description: 'Build clear responsibilities from reusable permissions.',
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
    description: 'Limit resource-scoped capabilities to specific resources.',
  },
];

export default function AccessExperience({ path = '/access/users' }: { path?: string }) {
  const [session, setSession] = useWorkspaceSession();
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [form] = Form.useForm<{ tenantId: string; key: string }>();
  const connect = async (values: { tenantId: string; key: string }) => {
    setBusy(true);
    setError('');
    try {
      const current = await establishSession(values.tenantId.trim(), values.key);
      form.setFieldValue('key', '');
      await new AccessApi(current).list('members');
      setSession(current);
    } catch (failure) {
      setError(displayError(failure));
    } finally {
      form.setFieldValue('key', '');
      setBusy(false);
    }
  };
  const end = useCallback(
    (message: string) => {
      setSession(null);
      setError(message);
    },
    [setSession],
  );
  if (session)
    return (
      <AuthenticatedAccess
        key={session.authenticationContextId}
        path={path}
        session={session}
        onEnd={end}
      />
    );
  return (
    <main className="access-page">
      <header className="access-page-header">
        <div>
          <p className="workspace-eyebrow">MONERGY · ACCESS MANAGEMENT</p>
          <h1>Access starts here.</h1>
          <p>Manage who belongs to your tenant and what they can do.</p>
        </div>
      </header>
      <div className="access-connect-grid">
        <section className="access-connect-story">
          <WorkspaceIcon name="access" size={32} />
          <h2>
            One place for people
            <br />
            and permissions.
          </h2>
          <p>
            Create users. Define roles. Review changes.
            <br />
            Every action stays within your tenant.
          </p>
          <ol className="access-start-steps">
            <li>
              <strong>Connect securely</strong>
              <span>Use your administrator’s local UAT access key.</span>
            </li>
            <li>
              <strong>Set up your team</strong>
              <span>Add one user or onboard a team together.</span>
            </li>
            <li>
              <strong>Assign intentional access</strong>
              <span>Choose a business role and review before saving.</span>
            </li>
          </ol>
        </section>
        <Card className="access-connect-card">
          <span className="workspace-eyebrow">YOUR LOCAL WORKSPACE</span>
          <h2>Connect to your tenant</h2>
          <p>Use the local access key configured for your administrator account.</p>
          {error && <Alert type="error" showIcon title={error} className="access-notice" />}
          <Form
            form={form}
            layout="vertical"
            onFinish={(values) => {
              void connect(values);
            }}
            initialValues={{ tenantId: 'T001' }}
          >
            <Form.Item
              name="tenantId"
              label="Tenant"
              rules={[
                { required: true, message: 'Enter your tenant.' },
                {
                  pattern: /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$/,
                  message: 'Enter a valid tenant identifier.',
                },
              ]}
            >
              <Input size="large" autoComplete="organization" disabled={busy} />
            </Form.Item>
            <Form.Item
              name="key"
              label="Local access key"
              rules={[{ required: true, message: 'Enter your local access key.' }]}
            >
              <Input.Password size="large" autoComplete="off" disabled={busy} />
            </Form.Item>
            <Button type="primary" size="large" block htmlType="submit" loading={busy}>
              Connect workspace
            </Button>
          </Form>
          <div className="access-connect-footnote">
            <WorkspaceIcon name="shield" size={15} />
            <span>
              Your key is cleared after use. This local connection does not configure Production
              sign-in.
            </span>
          </div>
        </Card>
      </div>
    </main>
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
function AuthenticatedAccess({
  path,
  session,
  onEnd,
}: {
  path: string;
  session: TenantSession;
  onEnd: (message: string) => void;
}) {
  const api = useMemo(() => new AccessApi(session), [session]);
  const [signingOut, setSigningOut] = useState(false);
  const fail = useCallback(
    (failure: unknown) => {
      if (failure instanceof AccessApiError && (failure.status === 401 || failure.status === 403))
        onEnd(displayError(failure));
    },
    [onEnd],
  );
  const area = routes[path];
  return (
    <main className="access-page">
      <div className="access-context-bar">
        <span>
          <WorkspaceIcon name="shield" size={15} /> Tenant <strong>{session.tenantId}</strong>
          <span className="access-context-divider">/</span>Administrator
        </span>
        <Button
          size="small"
          type="text"
          loading={signingOut}
          onClick={() => {
            setSigningOut(true);
            void api
              .endSession()
              .then(() => {
                onEnd('');
              })
              .catch((failure: unknown) => {
                onEnd(displayError(failure));
              });
          }}
        >
          Sign out
        </Button>
      </div>
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
    page?.items.filter((record) =>
      [
        record.normalizedEmail,
        record.label,
        record.code,
        record.status,
        record.resourceId,
        record.actorId,
      ].some((value) => value?.toLowerCase().includes(query.toLowerCase())),
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
            <small>
              {record.code ??
                (area === 'members'
                  ? record.businessRoleId
                    ? (roleNames[record.businessRoleId] ?? 'Business role assigned')
                    : 'No business role'
                  : (record.capabilityId ?? ''))}
            </small>
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
            title: 'Authority',
            key: 'authority',
            render: (_: unknown, record: AccessRecord) => (
              <span className="access-secondary">
                {record.tenantAdmin ? 'Tenant administrator' : 'Member'}
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
      width: 52,
      render: (_, record) => (
        <Tooltip title="Open details">
          <Button
            type="text"
            aria-label={`Open ${record.normalizedEmail ?? record.label ?? record.id ?? 'details'}`}
            icon={<WorkspaceIcon name="arrow" size={17} />}
            onClick={() => {
              if (area === 'imports') setImportId(record.id);
              else setEditor({ record });
            }}
          />
        </Tooltip>
      ),
    },
  ];
  return (
    <>
      <header className="access-page-header">
        <div>
          <p className="workspace-eyebrow">ACCESS MANAGEMENT</p>
          <h1>{selected?.label}</h1>
          <p>{selected?.description}</p>
        </div>
        <Space wrap>
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
      <section className="access-panel" aria-label={selected?.label}>
        <div className="access-list-toolbar">
          <span>{area === 'members' ? 'User directory' : selected?.label}</span>
          <Space wrap>
            {area === 'members' && (
              <>
                <Button type="text" href="/access/users/history">
                  Onboarding history
                </Button>
                <Button
                  icon={<WorkspaceIcon name="upload" size={16} />}
                  onClick={() => {
                    setSingle(false);
                    setImportId(null);
                  }}
                >
                  Import users
                </Button>
              </>
            )}
            <Button onClick={refresh} disabled={busy}>
              Refresh
            </Button>
          </Space>
        </div>
        <div className="access-search-row">
          <Input
            allowClear
            prefix={<WorkspaceIcon name="search" size={17} />}
            aria-label="Filter loaded records"
            placeholder="Filter loaded records…"
            value={query}
            onChange={(event) => {
              setQuery(event.target.value);
            }}
          />
          <span>{page ? `${String(page.items.length)} loaded` : 'Loading…'}</span>
        </div>
        <Table<AccessRecord>
          columns={columns}
          dataSource={filtered}
          rowKey={recordId}
          pagination={false}
          loading={!page && !error}
          scroll={filtered.length ? { x: 620 } : undefined}
          locale={{
            emptyText: (
              <Empty
                description={
                  error
                    ? 'Records could not be loaded. Refresh to try again.'
                    : query
                      ? 'No loaded records match. Clear the filter or load more records.'
                      : `No ${selected?.label.toLowerCase() ?? 'records'} yet. Use the action above to get started.`
                }
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            ),
          }}
        />
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
      <div className="access-bottom-note">
        <WorkspaceIcon name="shield" size={17} />
        <p>
          Tenant administrators manage access. Business permissions come from the person’s assigned
          role and applicable resource grants.
        </p>
      </div>
      {editor && page && (
        <AccessEditor
          key={`${area}:${editor.record ? recordId(editor.record) : 'new'}`}
          area={area}
          record={editor.record}
          api={api}
          policyVersion={page.policyVersion}
          onClose={() => {
            setEditor(null);
          }}
          onSaved={(message) => {
            setEditor(null);
            changed(message);
          }}
          onExpired={fail}
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
