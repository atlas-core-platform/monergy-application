import { Alert, Button, Card, Empty, Popconfirm, Space, Table, Tag, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { useCallback, useEffect, useState } from 'react';

import type { AccessApi } from './accessApi';
import { displayError } from './accessApi';

const { Paragraph, Text, Title } = Typography;
const target = (value: string): boolean => {
  const parts = value.split('.');
  return parts.length >= 2 && parts.every(part => part.length > 0 && part.length <= 80 &&
    part.charAt(0) >= 'a' && part.charAt(0) <= 'z' && Array.from(part).every(character =>
      character >= 'a' && character <= 'z' || character >= '0' && character <= '9' || character === '-'));
};
const requestId = (value: unknown): value is string =>
  typeof value === 'string' && /^[0-9a-f]{32}$/.test(value);
const safeText = (value: unknown, maximum: number): value is string =>
  typeof value === 'string' && value.trim().length > 0 && value.length <= maximum &&
  !Array.from(value).some(character => {
    const code = character.charCodeAt(0);
    return code < 32 || code === 127;
  });
const date = (value: unknown): value is string =>
  typeof value === 'string' && Number.isFinite(Date.parse(value));
const operationId = () => crypto.randomUUID().replaceAll('-', '');

interface AccessRequestItem {
  requestId: string;
  requester: string;
  target: string;
  targetLabel: string;
  state: 'UnderReview' | 'Rejected' | 'Fulfilled';
  createdAt: string;
  expiresAt: string;
  decisionAt: string | null;
  decisionReason: string | null;
  grantExplanation: string;
}
interface AccessRequestPage {
  tenantId: string;
  actorId: string;
  policyVersion: number;
  items: AccessRequestItem[];
  nextCursor: string | null;
}

function validItem(value: unknown): value is AccessRequestItem {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const item = value as Record<string, unknown>;
  return requestId(item.requestId) && safeText(item.requester, 320) &&
    typeof item.target === 'string' && item.target.length <= 160 && target(item.target) &&
    safeText(item.targetLabel, 120) &&
    (item.state === 'UnderReview' || item.state === 'Rejected' || item.state === 'Fulfilled') &&
    date(item.createdAt) && date(item.expiresAt) &&
    (item.decisionAt === null || date(item.decisionAt)) &&
    (item.decisionReason === null || safeText(item.decisionReason, 160)) &&
    safeText(item.grantExplanation, 400);
}
function page(value: unknown, tenantId: string, actorId: string): AccessRequestPage {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('ACCESS_REQUEST_PAGE_INVALID');
  const result = value as Record<string, unknown>;
  if (result.tenantId !== tenantId || result.actorId !== actorId ||
      typeof result.policyVersion !== 'number' || !Number.isSafeInteger(result.policyVersion) ||
      result.policyVersion < 1 || !Array.isArray(result.items) || result.items.length > 50 ||
      !result.items.every(validItem) ||
      !(result.nextCursor === null || requestId(result.nextCursor)) ||
      new Set(result.items.map(item => item.requestId)).size !== result.items.length)
    throw new Error('ACCESS_REQUEST_PAGE_INVALID');
  return result as unknown as AccessRequestPage;
}
function decided(value: unknown, expectedId: string): AccessRequestItem {
  if (!validItem(value) || value.requestId !== expectedId) throw new Error('ACCESS_REQUEST_DECISION_INVALID');
  return value;
}
function age(value: string): string {
  const seconds = Math.max(0, Math.floor((Date.now() - Date.parse(value)) / 1000));
  if (seconds < 60) return 'Just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60).toString()} min ago`;
  if (seconds < 86_400) return `${Math.floor(seconds / 3600).toString()} hr ago`;
  return `${Math.floor(seconds / 86_400).toString()} day${seconds < 172_800 ? '' : 's'} ago`;
}
const stateLabel: Record<AccessRequestItem['state'], string> = {
  UnderReview: 'Under review', Rejected: 'Rejected', Fulfilled: 'Fulfilled',
};

export function AccessRequests({
  api,
  tenantId,
  actorId,
}: {
  api: AccessApi;
  tenantId: string;
  actorId: string;
}) {
  const [current, setCurrent] = useState<AccessRequestPage | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState('');
  const [revision, setRevision] = useState(0);
  const load = useCallback((signal: AbortSignal) =>
    api.request<unknown>('administration/access-requests?limit=50', 'GET', undefined, signal)
      .then(value => {
        signal.throwIfAborted();
        setCurrent(page(value, tenantId, actorId));
        setError('');
      }), [api, tenantId, actorId]);
  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal).catch((failure: unknown) => {
      if (!controller.signal.aborted) setError(displayError(failure));
    });
    return () => { controller.abort(); };
  }, [load, revision]);

  async function decide(item: AccessRequestItem, action: 'approve' | 'reject') {
    if (busy || item.state !== 'UnderReview') return;
    setBusy(item.requestId + ':' + action);
    setError('');
    setNotice('');
    try {
      const result = await api.request<unknown>(
        `administration/access-requests/${encodeURIComponent(item.requestId)}/${action}`,
        'POST', { operationId: operationId() });
      const verified = decided(result, item.requestId);
      setCurrent(existing => existing ? {
        ...existing,
        items: existing.items.map(entry => entry.requestId === item.requestId ? verified : entry),
      } : existing);
      setNotice(action === 'approve'
        ? 'Access was approved and fulfilled using the requester’s current organization access.'
        : 'The request was rejected. No access was granted.');
    } catch (failure) {
      setError(displayError(failure));
      setRevision(value => value + 1);
    } finally {
      setBusy('');
    }
  }

  const columns: TableColumnsType<AccessRequestItem> = [
    {
      title: 'Requester', dataIndex: 'requester', key: 'requester',
      render: value => <Text strong>{value}</Text>,
    },
    {
      title: 'Requested access', key: 'target',
      render: (_, item) => <Space orientation="vertical" size={2}>
        <Text strong>{item.targetLabel}</Text>
        <Text type="secondary">{item.grantExplanation}</Text>
      </Space>,
    },
    {
      title: 'Age', dataIndex: 'createdAt', key: 'createdAt', width: 120,
      render: (value: string) => <Text>{age(value)}</Text>,
    },
    {
      title: 'Status', dataIndex: 'state', key: 'state', width: 130,
      render: (value: AccessRequestItem['state']) =>
        <Tag color={value === 'Fulfilled' ? 'success' : value === 'Rejected' ? 'error' : 'processing'}>
          {stateLabel[value]}
        </Tag>,
    },
    {
      title: 'Review', key: 'actions', width: 190,
      render: (_, item) => item.state === 'UnderReview' ? <Space>
        <Popconfirm
          title="Approve this access request?"
          description="This grants only the access described here. Customer assignment and consent remain separate."
          okText="Approve access"
          onConfirm={() => { void decide(item, 'approve'); }}
        >
          <Button type="primary" loading={busy === item.requestId + ':approve'} disabled={Boolean(busy)}>
            Approve
          </Button>
        </Popconfirm>
        <Popconfirm
          title="Reject this access request?"
          description="No access will be granted."
          okText="Reject request"
          okButtonProps={{ danger: true }}
          onConfirm={() => { void decide(item, 'reject'); }}
        >
          <Button danger loading={busy === item.requestId + ':reject'} disabled={Boolean(busy)}>
            Reject
          </Button>
        </Popconfirm>
      </Space> : <Text type="secondary">Decision recorded</Text>,
    },
  ];

  return <section aria-labelledby="access-requests-title">
    <header className="access-page-header">
      <div>
        <p className="workspace-eyebrow">ORGANIZATION · ACCESS</p>
        <Title level={1} id="access-requests-title">Access requests</Title>
        <Paragraph>Review bounded, tenant-grantable access requests from your organization.</Paragraph>
      </div>
      <Button onClick={() => { setCurrent(null); setRevision(value => value + 1); }} disabled={Boolean(busy)}>
        Refresh
      </Button>
    </header>
    <Alert type="info" showIcon className="access-notice"
      title="Approval is deliberately bounded"
      description="Approval grants only the capability described on the request. It cannot assign a customer, replace customer consent, grant Platform Administrator access or provide administrator authority." />
    {error && <Alert type="error" showIcon className="access-notice" title={error} />}
    {notice && <Alert type="success" showIcon className="access-notice" title={notice} />}
    <Card>
      {current?.items.length === 0
        ? <Empty description="There are no access requests to review." />
        : <Table<AccessRequestItem>
            rowKey="requestId" columns={columns} dataSource={current?.items ?? []}
            loading={!current} pagination={false} />}
    </Card>
  </section>;
}
