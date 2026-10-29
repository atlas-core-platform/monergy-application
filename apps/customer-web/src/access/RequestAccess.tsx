import { Alert, Button, Card, Space, Spin, Tag, Typography } from 'antd';
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';

import { useWorkspaceSession } from './workspaceSession';

const { Paragraph, Text, Title } = Typography;

export type RequestTarget =
  | 'customer.financial-information'
  | 'customer.documents'
  | 'customer.processing-status'
  | 'customer.calculations'
  | 'customer.search'
  | 'customer.financial-qa'
  | 'customer.reports'
  | 'customer.data-connections'
  | 'financial-advisor.assigned-customer-financial-review';

export interface RequestAccessStatus {
  requestId: string | null;
  target: RequestTarget;
  targetLabel: string;
  grantExplanation: string;
  state: 'NotRequested' | 'Submitted' | 'Rejected' | 'Fulfilled';
  hasAccess: boolean;
  canRequest: boolean;
  createdAt: string | null;
  decisionAt: string | null;
  fulfilledAt: string | null;
  decisionReason: string | null;
}

interface RequestAccessPage {
  tenantId: string;
  actorId: string;
  policyVersion: number;
  subjectVersion: number;
  items: RequestAccessStatus[];
}

const targetPattern = /^(customer\.(financial-information|documents|processing-status|calculations|search|financial-qa|reports|data-connections)|financial-advisor\.assigned-customer-financial-review)$/;
const safeText = (value: unknown, maximum: number): value is string =>
  typeof value === 'string' && value.trim().length > 0 && value.length <= maximum &&
  !Array.from(value).some(character => character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127);
const nullableDate = (value: unknown): value is string | null =>
  value === null || typeof value === 'string' && Number.isFinite(Date.parse(value));

function validStatus(value: unknown): value is RequestAccessStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const item = value as Record<string, unknown>;
  return (item.requestId === null || typeof item.requestId === 'string' && /^[0-9a-f]{32}$/.test(item.requestId)) &&
    typeof item.target === 'string' && targetPattern.test(item.target) && safeText(item.targetLabel, 120) &&
    safeText(item.grantExplanation, 500) &&
    (item.state === 'NotRequested' || item.state === 'Submitted' || item.state === 'Rejected' || item.state === 'Fulfilled') &&
    typeof item.hasAccess === 'boolean' && typeof item.canRequest === 'boolean' &&
    nullableDate(item.createdAt) && nullableDate(item.decisionAt) && nullableDate(item.fulfilledAt) &&
    (item.decisionReason === null || safeText(item.decisionReason, 200));
}

function parsePage(value: unknown, tenantId: string, actorId: string): RequestAccessPage {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('ACCESS_REQUEST_STATUS_INVALID');
  const page = value as Record<string, unknown>;
  if (page.tenantId !== tenantId || page.actorId !== actorId ||
      typeof page.policyVersion !== 'number' || !Number.isSafeInteger(page.policyVersion) || page.policyVersion < 1 ||
      typeof page.subjectVersion !== 'number' || !Number.isSafeInteger(page.subjectVersion) || page.subjectVersion < 1 ||
      !Array.isArray(page.items) || page.items.length > 20 || !page.items.every(validStatus) ||
      new Set(page.items.map(item => item.target)).size !== page.items.length)
    throw new Error('ACCESS_REQUEST_STATUS_INVALID');
  return page as unknown as RequestAccessPage;
}

function useRequestAccess(enabled = true) {
  const { api, session, refreshContext } = useWorkspaceSession();
  const [page, setPage] = useState<RequestAccessPage | null>(null);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState('');
  const [message, setMessage] = useState('');
  const refreshedVersion = useRef<number | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    if (!enabled || !api || !session) return;
    setLoading(true);
    setMessage('');
    try {
      const value = await api.request<unknown>('access-requests', 'GET', undefined, signal);
      const verified = parsePage(value, session.tenantId, session.actorId);
      setPage(verified);
      if (verified.items.some(item => item.hasAccess && item.state === 'Fulfilled') &&
          refreshedVersion.current !== verified.subjectVersion) {
        refreshedVersion.current = verified.subjectVersion;
        await refreshContext?.();
      }
    } catch (failure) {
      if (!signal?.aborted) setMessage('Your current access status could not be verified. Refresh before trying again.');
      throw failure;
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [enabled, api, session, refreshContext]);

  useEffect(() => {
    if (!enabled || !api || !session) return;
    const controller = new AbortController();
    void Promise.resolve().then(() => load(controller.signal)).catch(() => undefined);
    return () => { controller.abort(); };
  }, [enabled, api, session, load]);

  const submit = useCallback(async (target: RequestTarget) => {
    if (!api || busy) return;
    setBusy(target);
    setMessage('');
    try {
      const value = await api.request<unknown>(
        `access-requests/${encodeURIComponent(target)}/submit`, 'POST',
        { operationId: crypto.randomUUID().replaceAll('-', '') });
      if (!validStatus(value) || value.target !== target) throw new Error('ACCESS_REQUEST_SUBMISSION_INVALID');
      setPage(current => current ? {
        ...current,
        items: current.items.map(item => item.target === target ? value : item),
      } : current);
      setMessage('Your request was submitted to an Organization Administrator.');
    } catch {
      setMessage('The request was not confirmed. Refresh its status before trying again.');
    } finally {
      setBusy('');
    }
  }, [api, busy]);

  return { page, loading, busy, message, submit, load };
}

function StatusCard({ item, loading, busy, message, submit, onRefresh }: {
  item: RequestAccessStatus | null;
  loading: boolean;
  busy: string;
  message: string;
  submit: (target: RequestTarget) => Promise<void>;
  onRefresh: () => Promise<void>;
}) {
  return <Card className="access-request-card"
    title={item?.targetLabel ?? 'Restricted capability'}
    extra={<Tag color={item?.hasAccess ? 'success' : item?.state === 'Submitted' ? 'processing' : 'warning'}>
      {item?.hasAccess ? 'Available' : item?.state === 'Submitted' ? 'Under review' : item?.state === 'Rejected' ? 'Rejected' : 'Restricted'}
    </Tag>}>
    {loading && <Spin aria-label="Checking access status" />}
    {item && <Paragraph>{item.grantExplanation}</Paragraph>}
    {item?.state === 'Submitted' && <Alert showIcon type="info" title="Your request is under review"
      description="An Organization Administrator can approve or reject this bounded request. Submitting again will not create a duplicate." />}
    {item?.state === 'Rejected' && <Alert showIcon type="warning" title="The request was not approved"
      description={item.decisionReason ?? 'No access was granted. You may submit a new request if it is still available.'} />}
    {message && <Alert className="mt-3" showIcon type={item?.state === 'Submitted' ? 'success' : 'warning'} title={message} />}
    <Space className="mt-3" wrap>
      {item?.canRequest && !item.hasAccess && <Button type="primary" loading={busy === item.target}
        disabled={Boolean(busy)} onClick={() => { void submit(item.target); }}>Request access</Button>}
      <Button disabled={Boolean(busy)} onClick={() => { void onRefresh().catch(() => undefined); }}>Refresh status</Button>
    </Space>
  </Card>;
}

export function RequestAccessGate({ target, requiredCapabilities, children }: {
  target: RequestTarget;
  requiredCapabilities: string[];
  children: ReactNode;
}) {
  const { authorization } = useWorkspaceSession();
  const capabilities = new Set(authorization?.area === 'product' ? authorization.context.capabilities : []);
  const granted = requiredCapabilities.every(capability => capabilities.has(capability));
  const access = useRequestAccess(!granted);
  const item = access.page?.items.find(value => value.target === target) ?? null;
  if (granted) return <>{children}</>;
  return <main className="access-page" aria-labelledby="restricted-capability-title">
    <Title level={2} id="restricted-capability-title">This capability needs organization access</Title>
    <Paragraph>The capability remains visible, but its protected data is not loaded until access is fulfilled.</Paragraph>
    <StatusCard item={item} loading={access.loading} busy={access.busy} message={access.message}
      submit={access.submit} onRefresh={access.load} />
  </main>;
}

export function RequestAccessAction({ target, requiredCapabilities, title, description }: {
  target: RequestTarget;
  requiredCapabilities: string[];
  title: string;
  description: string;
}) {
  const { authorization } = useWorkspaceSession();
  const capabilities = new Set(authorization?.area === 'product' ? authorization.context.capabilities : []);
  const granted = requiredCapabilities.every(capability => capabilities.has(capability));
  const access = useRequestAccess(!granted);
  const item = access.page?.items.find(value => value.target === target) ?? null;
  if (granted) return null;
  const underReview = item?.state === 'Submitted';
  const rejected = item?.state === 'Rejected';
  return <Alert className="request-access-action" showIcon
    type={underReview ? 'info' : rejected ? 'warning' : 'warning'}
    title={underReview ? 'Your request is under review' : rejected ? 'The request was not approved' : title}
    description={<Space orientation="vertical" size="small">
      <Text>{underReview
        ? 'An Organization Administrator is reviewing this bounded request.'
        : rejected
          ? item.decisionReason ?? 'No access was granted. You may submit a new request if it is still available.'
          : description}</Text>
      {access.message && <Text>{access.message}</Text>}
      <Space wrap>
        {item?.canRequest && !item.hasAccess && <Button type="primary" size="small"
          loading={access.busy === target} disabled={Boolean(access.busy)}
          onClick={() => { void access.submit(target); }}>Request access</Button>}
        <Button size="small" disabled={Boolean(access.busy)}
          onClick={() => { void access.load().catch(() => undefined); }}>
          {access.loading ? 'Checking status…' : 'Refresh status'}
        </Button>
      </Space>
    </Space>} />;
}

export function RequestAccessOverview({ targets }: { targets: RequestTarget[] }) {
  const access = useRequestAccess();
  const items = useMemo(() => access.page?.items.filter(item => targets.includes(item.target) && !item.hasAccess) ?? [],
    [access.page, targets]);
  if (!access.loading && items.length === 0) return null;
  return <section aria-labelledby="request-access-overview-title">
    <div className="workspace-section-heading">
      <div>
        <p className="workspace-eyebrow">AVAILABLE WITH APPROVAL</p>
        <h2 id="request-access-overview-title">Request access to another capability</h2>
      </div>
      <Text type="secondary">Your Organization Administrator reviews each bounded request.</Text>
    </div>
    <div className="workspace-area-grid">
      {items.map(item => <StatusCard key={item.target} item={item} loading={false} busy={access.busy}
        message={access.message} submit={access.submit} onRefresh={access.load} />)}
      {access.loading && <Spin aria-label="Loading requestable access" />}
    </div>
  </section>;
}
