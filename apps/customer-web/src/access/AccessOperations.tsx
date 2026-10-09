import { WorkspaceIcon } from '@monergy/ui-foundation';
import {
  Alert,
  Button,
  Card,
  Collapse,
  Drawer,
  Empty,
  Modal,
  Space,
  Spin,
  Table,
  Tag,
  Timeline,
} from 'antd';
import { useEffect, useState } from 'react';
import { displayError } from './accessApi';
import type { AccessApi } from './accessApi';

type View = 'sessions' | 'audit' | 'services' | 'readiness';
interface SessionRow {
  sessionReference: string;
  actorId: string;
  subjectVersion: number;
  establishedAt: string;
  expiresAt: string;
  revoked: boolean;
}
interface AuditRow {
  evidenceReference: string;
  recordedAt: string;
  event: {
    eventId: string;
    operation: string;
    initiatorActorId: string;
    subjectActorId: string | null;
    occurredAt: string;
    policyVersion: number;
    correlationId: string;
  };
}
interface ServiceRow {
  id: string;
  label: string;
  implementation: string;
  available: boolean;
}
interface Gate {
  id: string;
  state: string;
  detail: string;
}
interface Page<T> {
  items: T[];
  nextCursor: string | null;
}
const headings: Record<View, string> = {
  sessions: 'Security & Sessions',
  audit: 'Access Activity',
  services: 'Connected services',
  readiness: 'Release readiness',
};
const activityLabels: Record<string, string> = {
  'permission.saved': 'Permission saved',
  'role.saved': 'Role saved',
  'member.created': 'User added',
  'member.updated': 'User access updated',
  'member.sessions-revoked': 'All user sessions revoked',
  'member.imported': 'User onboarded',
  'resource-grant.created': 'Resource access assigned',
  'group.saved': 'Group saved',
  'group.members-replaced': 'Group membership updated',
  'import.staged': 'Onboarding request saved',
  'import.cancelled': 'Onboarding cancelled',
  'import.attempt-started': 'Onboarding started',
  'import.activated': 'User access activated',
  'import.failed': 'Onboarding needs attention',
};
const dates = (value: string) =>
  new Date(
    value.endsWith('Z') || /[+-]\d\d:\d\d$/.test(value) ? value : value + 'Z',
  ).toLocaleString();

export function AccessOperations({
  api,
  onExpired,
  views = ['sessions', 'audit', 'services', 'readiness'],
}: {
  views?: View[];
  api: AccessApi;
  onExpired: (failure: unknown) => void;
}) {
  const [view, setView] = useState<View | null>(null);
  return (
    <>
      <section className="access-operations-grid" aria-label="Administration operations">
        {views.map((key) => (
          <button
            type="button"
            className="access-operation-card"
            key={key}
            onClick={() => {
              setView(key);
            }}
          >
            <WorkspaceIcon
              name={
                key === 'sessions'
                  ? 'shield'
                  : key === 'audit'
                    ? 'evidence'
                    : key === 'services'
                      ? 'layers'
                      : 'check'
              }
              size={22}
            />
            <span>
              <strong>{headings[key]}</strong>
              <small>
                {key === 'sessions'
                  ? 'Review and revoke access'
                  : key === 'audit'
                    ? 'Trace changes to their owner'
                    : key === 'services'
                      ? 'Health, scope and limitations'
                      : 'See what remains before release'}
              </small>
            </span>
            <WorkspaceIcon name="arrow" size={16} />
          </button>
        ))}
      </section>
      {view && (
        <OperationsDrawer
          key={view}
          view={view}
          api={api}
          onExpired={onExpired}
          onClose={() => {
            setView(null);
          }}
        />
      )}
    </>
  );
}

export function AccessOperationPage(props: {
  view: 'sessions' | 'audit';
  api: AccessApi;
  onExpired: (failure: unknown) => void;
}) {
  return <OperationsDrawer {...props} page onClose={() => undefined} />;
}
function OperationsDrawer({
  view,
  api,
  onClose,
  onExpired,
  page = false,
}: {
  page?: boolean;
  view: View;
  api: AccessApi;
  onClose: () => void;
  onExpired: (failure: unknown) => void;
}) {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [sessions, setSessions] = useState<SessionRow[]>([]);
  const [events, setEvents] = useState<AuditRow[]>([]);
  const [services, setServices] = useState<ServiceRow[]>([]);
  const [gates, setGates] = useState<Gate[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);
  const [revoke, setRevoke] = useState<{ actor: string; version: number } | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    async function load() {
      setLoading(true);
      setError('');
      setCursor(null);
      setSessions([]);
      setEvents([]);
      setServices([]);
      setGates([]);
      try {
        if (view === 'sessions') {
          const page = await api.owner<Page<SessionRow>>(
            'identity',
            'local/v1/administration/sessions',
            abort.signal,
          );
          setSessions(page.items);
          setCursor(page.nextCursor);
        }
        if (view === 'audit') {
          const page = await api.owner<Page<AuditRow>>(
            'audit',
            'local/v1/administration/access-events',
            abort.signal,
          );
          setEvents(page.items);
          setCursor(page.nextCursor);
        }
        if (view === 'services') {
          const result = await api.owner<{ services: ServiceRow[] }>(
            'uat',
            'v1/topology',
            abort.signal,
          );
          setServices(result.services);
        }
        if (view === 'readiness') {
          const result = await api.owner<{ gates: Gate[] }>('uat', 'v1/readiness', abort.signal);
          setGates(result.gates);
        }
      } catch (failure) {
        if (!abort.signal.aborted) {
          setError(displayError(failure));
          onExpired(failure);
        }
      } finally {
        if (!abort.signal.aborted) setLoading(false);
      }
    }
    void load();
    return () => {
      abort.abort();
    };
  }, [api, view, revision, onExpired]);
  const more = async () => {
    if (!cursor) return;
    setLoading(true);
    try {
      if (view === 'sessions') {
        const page = await api.owner<Page<SessionRow>>(
          'identity',
          'local/v1/administration/sessions?after=' + encodeURIComponent(cursor),
        );
        setSessions((previous) => [...previous, ...page.items]);
        setCursor(page.nextCursor);
      } else {
        const page = await api.owner<Page<AuditRow>>(
          'audit',
          'local/v1/administration/access-events?after=' + encodeURIComponent(cursor),
        );
        setEvents((previous) => [...previous, ...page.items]);
        setCursor(page.nextCursor);
      }
    } catch (failure) {
      setError(displayError(failure));
      onExpired(failure);
    } finally {
      setLoading(false);
    }
  };
  const prepareRevocation = async (actor: string) => {
    setLoading(true);
    setError('');
    try {
      const context = await api.request<{ policyVersion: number }>('administration/context');
      setRevoke({ actor, version: context.policyVersion });
    } catch (failure) {
      setError(displayError(failure));
      onExpired(failure);
    } finally {
      setLoading(false);
    }
  };
  const commitRevocation = async () => {
    if (!revoke) return;
    setLoading(true);
    setError('');
    try {
      await api.request(
        'administration/members/' + encodeURIComponent(revoke.actor) + '/revoke-sessions',
        'POST',
        { expectedPolicyVersion: revoke.version },
      );
      setRevoke(null);
      setNotice(
        'Session revocation committed. Existing sessions are invalid immediately; the activity feed updates after durable delivery.',
      );
      setRevision((value) => value + 1);
    } catch (failure) {
      setError(displayError(failure));
      setRevoke(null);
      onExpired(failure);
    } finally {
      setLoading(false);
    }
  };
  const refreshButton = (
    <Button
      disabled={loading}
      onClick={() => {
        setRevision((value) => value + 1);
      }}
    >
      Refresh view
    </Button>
  );
  const content = (
    <>
      {error && <Alert type="error" showIcon title={error} className="access-notice" />}
      {notice && <Alert type="success" showIcon title={notice} className="access-notice" />}
      <Spin spinning={loading}>
        {view === 'sessions' && (
          <>
            <p className="workspace-subtitle">
              Review issued session records and revoke all sessions for a user. An issued record
              does not guarantee current access: each request also checks the user’s current status
              and permissions.
            </p>
            <Table<SessionRow>
              dataSource={sessions}
              rowKey="sessionReference"
              pagination={false}
              scroll={{ x: 590 }}
              columns={[
                { title: 'User reference', dataIndex: 'actorId' },
                { title: 'Issued', render: (_, row) => dates(row.establishedAt) },
                {
                  title: 'Session record',
                  render: (_, row) => (
                    <Tag
                      color={
                        row.revoked || new Date(row.expiresAt).getTime() < Date.now()
                          ? 'default'
                          : 'blue'
                      }
                    >
                      {row.revoked
                        ? 'Revoked'
                        : new Date(row.expiresAt).getTime() < Date.now()
                          ? 'Expired'
                          : 'Issued'}
                    </Tag>
                  ),
                },
                {
                  title: 'Action',
                  render: (_, row) => (
                    <Button
                      danger
                      size="small"
                      disabled={loading}
                      onClick={() => {
                        void prepareRevocation(row.actorId);
                      }}
                    >
                      Revoke all for {row.actorId}
                    </Button>
                  ),
                },
              ]}
            />
          </>
        )}
        {view === 'audit' && (
          <>
            <p className="workspace-subtitle">
              Review access changes, newest first. Recent changes may take a moment to appear;
              refresh to check for updates.
            </p>
            {events.length ? (
              <Timeline
                items={events.map((item) => ({
                  key: item.evidenceReference,
                  title:
                    activityLabels[item.event.operation] ??
                    item.event.operation.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[._-]/g, ' '),
                  content: (
                    <div>
                      <p>
                        {item.event.initiatorActorId}{' '}
                        {item.event.subjectActorId ? '→ ' + item.event.subjectActorId : ''} · Policy{' '}
                        {item.event.policyVersion}
                      </p>
                      <small>{dates(item.event.occurredAt)}</small>
                      <Collapse
                        ghost
                        items={[
                          {
                            key: 'details',
                            label: 'Audit reference details',
                            children: (
                              <dl className="access-evidence-details">
                                <dt>Operation</dt>
                                <dd>{item.event.operation}</dd>
                                <dt>Evidence</dt>
                                <dd>{item.evidenceReference}</dd>
                                <dt>Correlation</dt>
                                <dd>{item.event.correlationId}</dd>
                              </dl>
                            ),
                          },
                        ]}
                      />
                    </div>
                  ),
                }))}
              />
            ) : (
              !loading && <Empty description="No delivered access events yet." />
            )}
          </>
        )}
        {view === 'services' && (
          <>
            <Alert
              type="info"
              showIcon
              title="Service health confirms a running boundary. The scope below identifies which business journeys are implemented."
              className="access-notice"
            />
            <div className="access-service-list">
              {services.map((service) => (
                <Card key={service.id} size="small">
                  <Space>
                    <Tag color={service.available ? 'success' : 'error'}>
                      {service.available ? 'Running' : 'Unavailable'}
                    </Tag>
                    <strong>{service.label}</strong>
                  </Space>
                  <p>{service.implementation}</p>
                </Card>
              ))}
            </div>
          </>
        )}
        {view === 'readiness' && (
          <>
            <Alert
              type="warning"
              showIcon
              title="Local UAT · Production acceptance pending"
              className="access-notice"
            />
            <div className="access-service-list">
              {gates.map((gate) => (
                <Card key={gate.id} size="small">
                  <Tag color="warning">{gate.state}</Tag>
                  <strong>{gate.id}</strong>
                  <p>{gate.detail}</p>
                </Card>
              ))}
            </div>
          </>
        )}
        {cursor && (
          <Button
            className="access-load-more"
            onClick={() => {
              void more();
            }}
            disabled={loading}
          >
            {view === 'sessions' ? 'Load more sessions' : 'Load more activity'}
          </Button>
        )}
      </Spin>
      <Modal
        open={revoke !== null}
        title={`Revoke all sessions for ${revoke?.actor ?? ''}?`}
        onCancel={() => {
          setRevoke(null);
        }}
        onOk={() => {
          void commitRevocation();
        }}
        okText="Revoke sessions"
        okButtonProps={{ danger: true }}
        confirmLoading={loading}
      >
        <p>
          This ends the person’s current sessions in this tenant. Their role and resource access
          remain assigned. They can establish a new session if their membership is active.
        </p>
        <p>Revoking your own sessions returns you to sign-in.</p>
      </Modal>
    </>
  );
  return page ? (
    <>
      <header className="access-page-header">
        <div>
          <p className="workspace-eyebrow">SECURITY & OVERSIGHT</p>
          <h1>{headings[view]}</h1>
          <p>
            {view === 'sessions'
              ? 'Review sessions and end a user’s current access when needed.'
              : 'Trace access decisions to the people who made them.'}
          </p>
        </div>
        {refreshButton}
      </header>
      <section className="access-operation-page">{content}</section>
    </>
  ) : (
    <Drawer open title={headings[view]} size="large" onClose={onClose} extra={refreshButton}>
      {content}
    </Drawer>
  );
}
