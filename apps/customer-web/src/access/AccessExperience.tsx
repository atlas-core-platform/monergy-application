import { WorkspaceIcon } from '@monergy/ui-foundation';
import {
  Alert,
  Button,
  Card,
  Collapse,
  Empty,
  Form,
  Input,
  Segmented,
  Space,
  Table,
  Tag,
  Tooltip,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useCallback, useEffect, useMemo, useState } from 'react';

import { AccessApi, AccessApiError, displayError, establishSession, recordId } from './accessApi';
import type { AccessArea, AccessPage, AccessRecord, TenantSession } from './accessApi';
import { AccessEditor } from './AccessEditor';
import { ImportDrawer } from './ImportDrawer';
import './access.css';

const areas: { value: AccessArea; label: string; description: string }[] = [
  { value: 'members', label: 'People', description: 'Give every teammate the access they need.' },
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
    label: 'Imports',
    description: 'Bring people in together, with every row accounted for.',
  },
  {
    value: 'resource-grants',
    label: 'Resource access',
    description: 'Limit resource-scoped capabilities to specific resources.',
  },
];

export default function AccessExperience() {
  const [session, setSession] = useState<TenantSession | null>(null);
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
  if (session)
    return (
      <Administration
        key={session.authenticationContextId}
        session={session}
        onEnd={(message) => {
          setSession(null);
          setError(message);
        }}
      />
    );
  return (
    <main className="access-page">
      <div className="workspace-page-heading">
        <div>
          <p className="workspace-eyebrow">WORKSPACE ADMINISTRATION</p>
          <h1>Good work starts with the right access.</h1>
          <p className="workspace-subtitle">
            People, roles and permissions, thoughtfully connected.
          </p>
        </div>
      </div>
      <div className="access-connect-grid">
        <section className="access-connect-story">
          <div className="access-large-icon">
            <WorkspaceIcon name="access" size={40} />
          </div>
          <h2>
            Bring your people
            <br />
            into focus.
          </h2>
          <p>A clear view of who belongs, what they can do, and how your workspace is organized.</p>
          <div className="access-benefit">
            <WorkspaceIcon name="users" />
            <div>
              <strong>Onboard with confidence</strong>
              <span>Preview every row before bringing your team in.</span>
            </div>
          </div>
          <div className="access-benefit">
            <WorkspaceIcon name="shield" />
            <div>
              <strong>Make access intentional</strong>
              <span>Keep administrative authority and business access distinct.</span>
            </div>
          </div>
          <div className="access-benefit">
            <WorkspaceIcon name="layers" />
            <div>
              <strong>Stay in your flow</strong>
              <span>Edit in context without losing your place.</span>
            </div>
          </div>
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

function Administration({
  session,
  onEnd,
}: {
  session: TenantSession;
  onEnd: (message: string) => void;
}) {
  const api = useMemo(() => new AccessApi(session), [session]);
  const [area, setArea] = useState<AccessArea>('members');
  const [page, setPage] = useState<AccessPage | null>(null);
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [editor, setEditor] = useState<{ record?: AccessRecord } | null>(null);
  const [importId, setImportId] = useState<string | null | undefined>(undefined);
  const [revision, setRevision] = useState(0);
  const fail = useCallback(
    (failure: unknown) => {
      if (failure instanceof AccessApiError && (failure.status === 401 || failure.status === 403)) {
        onEnd(displayError(failure));
        return;
      }
      setError(displayError(failure));
    },
    [onEnd],
  );
  // A new tenant/session mounts a new Administration component. Cancelled loads
  // cannot repopulate the next tenant's view or replace a newer selected page.
  useEffect(() => {
    const controller = new AbortController();
    void api
      .list(area, undefined, controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) {
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
  const disconnect = async () => {
    setBusy(true);
    try {
      await api.endSession();
      onEnd('');
    } catch (failure) {
      onEnd(displayError(failure));
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
          ? 'Person'
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
              {record.code ?? (area === 'members' ? record.actorId : (record.capabilityId ?? ''))}
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
                {!record.businessRoleId && (
                  <small className="access-no-role">No business role</small>
                )}
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
    <main className="access-page">
      <div className="workspace-page-heading">
        <div>
          <p className="workspace-eyebrow">WORKSPACE ADMINISTRATION</p>
          <h1>People & access</h1>
          <p className="workspace-subtitle">
            A clear view of your team. Intentional access at every level.
          </p>
        </div>
        <div className="access-session-context">
          <span className="access-tenant-pill">
            <WorkspaceIcon name="shield" size={14} />
            {session.tenantId}
          </span>
          <Button
            size="small"
            onClick={() => {
              void disconnect();
            }}
            disabled={busy}
          >
            Sign out
          </Button>
        </div>
      </div>
      <div className="access-overview-strip">
        <div>
          <span className="access-strip-icon">
            <WorkspaceIcon name="users" size={22} />
          </span>
          <div>
            <strong>Your people, connected</strong>
            <span>One tenant. Clear responsibilities.</span>
          </div>
        </div>
        <div>
          <span className="access-strip-icon">
            <WorkspaceIcon name="shield" size={22} />
          </span>
          <div>
            <strong>Server-verified access</strong>
            <span>Every change checks current authority.</span>
          </div>
        </div>
        <div>
          <span className="access-strip-icon">
            <WorkspaceIcon name="layers" size={22} />
          </span>
          <div>
            <strong>All-or-none onboarding</strong>
            <span>Every identity ready before access begins.</span>
          </div>
        </div>
      </div>
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
      <section className="access-panel">
        <div className="access-tabs">
          <Segmented
            disabled={busy}
            options={areas.map((item) => ({ value: item.value, label: item.label }))}
            value={area}
            onChange={(value) => {
              setArea(value);
              setPage(null);
              setQuery('');
              setNotice('');
            }}
          />
        </div>
        <div className="access-list-heading">
          <div>
            <h2>{selected?.label}</h2>
            <p>{selected?.description}</p>
          </div>
          <Space>
            <Button onClick={refresh} disabled={busy}>
              Refresh
            </Button>
            {area === 'members' || area === 'imports' ? (
              <Button
                type="primary"
                icon={<WorkspaceIcon name="upload" size={16} />}
                onClick={() => {
                  setImportId(null);
                }}
              >
                Import people
              </Button>
            ) : (
              <Button
                type="primary"
                onClick={() => {
                  setEditor({});
                }}
                disabled={!page}
              >
                Create {area === 'resource-grants' ? 'resource access' : area.slice(0, -1)}
              </Button>
            )}
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
          loading={!page}
          scroll={{ x: 620 }}
          locale={{
            emptyText: (
              <Empty
                description={
                  query
                    ? 'No loaded records match your search.'
                    : `No ${selected?.label.toLowerCase() ?? 'records'} yet.`
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
      <Collapse
        className="access-explainer"
        ghost
        items={[
          {
            key: 'guide',
            label: 'How roles, permissions and groups work together',
            children: (
              <div className="access-explainer-grid">
                <p>
                  <strong>Roles</strong> bring permissions together. A person has at most one
                  business role.
                </p>
                <p>
                  <strong>Permissions</strong> connect service capabilities to tenant-wide or
                  resource-specific access.
                </p>
                <p>
                  <strong>Groups</strong> organize people. Group membership does not grant access.
                </p>
              </div>
            ),
          },
        ]}
      />
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
          importId={importId}
          onClose={() => {
            setImportId(undefined);
            refresh();
          }}
          onChanged={changed}
          onExpired={fail}
        />
      )}
    </main>
  );
}
