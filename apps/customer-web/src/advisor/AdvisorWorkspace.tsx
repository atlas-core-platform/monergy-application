import { Alert, Button, Card, Checkbox, Descriptions, Empty, Input, List, Space, Spin, Tag, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';
import { advisorEngagement, nextAdvisorStage } from './advisorEngagementModels';
import type { AdvisorEngagement } from './advisorEngagementModels';

const { Title, Paragraph, Text } = Typography;

interface NamedCustomer { customerId: string; displayName: string }
interface ReviewedFact {
  financialFactId: string;
  factType: string;
  label: string;
  value: number;
  currency: string;
  effectiveDate: string;
  revision: number;
  source: { sourceName: string };
}
interface FinancialReview {
  revision: number;
  facts: ReviewedFact[];
}
interface AdvisorAccessRequest {
  requestId: string | null;
  target: 'financial-advisor.assigned-customer-financial-review';
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

const identifier = (value: string) => /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$/.test(value);
const containsControlCharacter = (value: string): boolean => {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code < 32 || code === 127) return true;
  }
  return false;
};
const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);
const record = (value: unknown): Record<string, unknown> => {
  if (!isRecord(value)) throw new Error('UNVERIFIED_OWNER_RESPONSE');
  return value;
};
const validCustomer = (value: unknown): value is NamedCustomer => {
  if (!isRecord(value)) return false;
  const item = value;
  return typeof item.customerId === 'string' && identifier(item.customerId) &&
    typeof item.displayName === 'string' && !!item.displayName.trim() &&
    item.displayName.length <= 120 &&
    !containsControlCharacter(item.displayName);
};
function authorizedClients(value: unknown, tenantId: string, actorId: string,
  policyVersion: number, subjectVersion: number): NamedCustomer[] {
  const data = record(value);
  if (data.tenantId !== tenantId || data.actorId !== actorId ||
      data.policyVersion !== policyVersion || data.subjectVersion !== subjectVersion ||
      !Array.isArray(data.customers) || data.customers.length > 50)
    throw new Error('ADVISOR_LIST_INVALID');
  const values: unknown[] = data.customers;
  if (!values.every(validCustomer)) throw new Error('ADVISOR_LIST_INVALID');
  const customers: NamedCustomer[] = values;
  if (new Set(customers.map(customer => customer.customerId)).size !== customers.length)
    throw new Error('ADVISOR_LIST_INVALID');
  return customers;
}
const validFact = (value: unknown): value is ReviewedFact => {
  if (!isRecord(value) || !isRecord(value.source)) return false;
  const fact = value;
  const source = value.source;
  return typeof fact.financialFactId === 'string' && identifier(fact.financialFactId) &&
    typeof fact.factType === 'string' && typeof fact.label === 'string' && !!fact.label.trim() &&
    typeof fact.value === 'number' && Number.isFinite(fact.value) &&
    typeof fact.currency === 'string' && typeof fact.effectiveDate === 'string' &&
    typeof fact.revision === 'number' && Number.isSafeInteger(fact.revision) &&
    typeof source.sourceName === 'string';
};
function financialReview(value: unknown): FinancialReview {
  const raw = record(value);
  if (typeof raw.revision !== 'number' || !Number.isSafeInteger(raw.revision) ||
      !Array.isArray(raw.facts) || raw.facts.length > 1000)
    throw new Error('ADVISOR_PROFILE_INVALID');
  const facts: unknown[] = raw.facts;
  if (!facts.every(validFact)) throw new Error('ADVISOR_PROFILE_INVALID');
  return { revision: raw.revision, facts };
}
const nullableDate = (value: unknown): value is string | null =>
  value === null || typeof value === 'string' && Number.isFinite(Date.parse(value));
function accessRequest(value: unknown): AdvisorAccessRequest {
  const raw = record(value);
  const states = ['NotRequested', 'Submitted', 'Rejected', 'Fulfilled'];
  if (!(raw.requestId === null || typeof raw.requestId === 'string' && /^[0-9a-f]{32}$/.test(raw.requestId)) ||
      raw.target !== 'financial-advisor.assigned-customer-financial-review' ||
      typeof raw.targetLabel !== 'string' || !raw.targetLabel.trim() || raw.targetLabel.length > 120 ||
      typeof raw.grantExplanation !== 'string' || !raw.grantExplanation.trim() || raw.grantExplanation.length > 500 ||
      typeof raw.state !== 'string' || !states.includes(raw.state) ||
      typeof raw.hasAccess !== 'boolean' || typeof raw.canRequest !== 'boolean' ||
      !nullableDate(raw.createdAt) || !nullableDate(raw.decisionAt) || !nullableDate(raw.fulfilledAt) ||
      !(raw.decisionReason === null || typeof raw.decisionReason === 'string' && raw.decisionReason.length <= 160))
    throw new Error('ADVISOR_ACCESS_REQUEST_INVALID');
  return raw as unknown as AdvisorAccessRequest;
}
function advisorAccessRequest(value: unknown, tenantId: string, actorId: string): AdvisorAccessRequest {
  const raw = record(value);
  if (raw.tenantId !== tenantId || raw.actorId !== actorId ||
      typeof raw.policyVersion !== 'number' || !Number.isSafeInteger(raw.policyVersion) || raw.policyVersion < 1 ||
      typeof raw.subjectVersion !== 'number' || !Number.isSafeInteger(raw.subjectVersion) || raw.subjectVersion < 1 ||
      !Array.isArray(raw.items) || raw.items.length !== 1)
    throw new Error('ADVISOR_ACCESS_REQUEST_INVALID');
  return accessRequest(raw.items[0]);
}
const newOperationId = () => crypto.randomUUID().replaceAll('-', '');

const categories: Record<string, string> = {
  INCOME: 'Income', EXPENSE: 'Expense', LOAN: 'Loan information',
  ASSET: 'Asset information', INVESTMENT: 'Investments',
  INSURANCE: 'Insurance', TAX: 'Tax information',
};
const amount = (fact: ReviewedFact) => {
  try { return new Intl.NumberFormat(undefined, { style: 'currency', currency: fact.currency }).format(fact.value); }
  catch { return fact.currency + ' ' + fact.value.toLocaleString(); }
};
const date = (value: string) => {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed)
    ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(parsed))
    : 'Date unavailable';
};

// Names come only from a C&I owner lookup after AM's two current read grants,
// active customer assignment and live Consent checks; never browser-supplied IDs.
export default function AdvisorWorkspace() {
  const { authorization, api, session } = useWorkspaceSession();
  const [clients, setClients] = useState<NamedCustomer[]>([]);
  const [selection, setSelection] = useState<NamedCustomer | null>(null);
  const [review, setReview] = useState<FinancialReview | null>(null);
  const [engagement, setEngagement] = useState<AdvisorEngagement | null>(null);
  const [selectedSources, setSelectedSources] = useState<string[]>([]);
  const [note, setNote] = useState('');
  const [communication, setCommunication] = useState('');
  const [activityBusy, setActivityBusy] = useState(false);
  const [activityNotice, setActivityNotice] = useState('');
  const [loading, setLoading] = useState(false);
  const [notice, setNotice] = useState('');
  const [refresh, setRefresh] = useState(0);
  const [access, setAccess] = useState<AdvisorAccessRequest | null>(null);
  const [accessLoading, setAccessLoading] = useState(false);
  const [accessBusy, setAccessBusy] = useState(false);
  const [accessNotice, setAccessNotice] = useState('');
  const advisor = authorization?.area === 'advisor' ? authorization : null;

  useEffect(() => {
    if (!advisor || !api || !session || !access?.hasAccess) return;
    const abort = new AbortController();
    // Defer the refresh transition until the request begins. Keep effects
    // limited to external synchronization rather than synchronous state updates.
    void Promise.resolve().then(() => {
      if (abort.signal.aborted) return null;
      setLoading(true);
      setClients([]);
      setSelection(null);
      setReview(null);
      setEngagement(null);
      setNotice('');
      return api.product<unknown>('v1/advisor/customers', 'GET', undefined, abort.signal);
    }).then(data => {
      if (abort.signal.aborted || data === null) return;
      setClients(authorizedClients(data, session.tenantId, session.actorId,
        advisor.policyVersion, advisor.subjectVersion));
    }).catch(() => {
      if (!abort.signal.aborted)
        setNotice('We could not load your assigned customers. Refresh to try again.');
    }).finally(() => {
      if (!abort.signal.aborted) setLoading(false);
    });
    return () => { abort.abort(); };
  }, [advisor, api, session, refresh, access?.hasAccess]);

  useEffect(() => {
    if (!advisor || !api || !session) return;
    const abort = new AbortController();
    void Promise.resolve().then(() => {
      if (abort.signal.aborted) return null;
      setAccessLoading(true);
      setAccessNotice('');
      return api.request<unknown>('access-requests', 'GET', undefined, abort.signal);
    })
      .then(value => {
        if (value === null) return;
        abort.signal.throwIfAborted();
        setAccess(advisorAccessRequest(value, session.tenantId, session.actorId));
      })
      .catch(() => {
        if (!abort.signal.aborted)
          setAccessNotice('We could not check your request. Refresh before trying again.');
      })
      .finally(() => {
        if (!abort.signal.aborted) setAccessLoading(false);
      });
    return () => { abort.abort(); };
  }, [advisor, api, session, refresh]);

  if (!advisor) return null;

  async function openReview(customer: NamedCustomer) {
    if (!api || !clients.some(entry => entry.customerId === customer.customerId) || loading) return;
    setLoading(true);
    setNotice('');
    setReview(null);
    setEngagement(null);
    setSelectedSources([]);
    setNote('');
    setCommunication('');
    setActivityNotice('');
    setSelection(null);
    try {
      const customerPath = encodeURIComponent(customer.customerId);
      const [received, engagementResponse] = await Promise.all([
        api.product<unknown>('v1/advisor/customers/' + customerPath + '/financial-profile'),
        api.case<unknown>('advisor/customers/' + customerPath + '/engagement'),
      ]);
      const value = financialReview(received);
      const work = advisorEngagement(engagementResponse, customer.customerId);
      setSelection(customer);
      setReview(value);
      setEngagement(work);
    } catch {
      setNotice('We could not open this customer. Your assignment or the customer’s permission to share may have changed.');
    } finally {
      setLoading(false);
    }
  }

  async function requestAccess() {
    if (!api || !session || !access?.canRequest || accessBusy) return;
    setAccessBusy(true);
    setAccessNotice('');
    try {
      const value = await api.request<unknown>('access-requests/financial-advisor.assigned-customer-financial-review/submit', 'POST', {
        operationId: newOperationId(),
      });
      setAccess(accessRequest(value));
      setAccessNotice('Your request was submitted to an Organization Administrator.');
    } catch {
      setAccessNotice('We could not confirm your request. Refresh before trying again.');
    } finally {
      setAccessBusy(false);
    }
  }

  async function mutateEngagement(path: string, body: Record<string, unknown>, success: string): Promise<boolean> {
    if (!api || !selection || !engagement || activityBusy) return false;
    setActivityBusy(true);
    setActivityNotice('');
    try {
      const value = await api.case<unknown>('advisor/customers/' + encodeURIComponent(selection.customerId) + '/' + path,
        'POST', { operationId: newOperationId(), expectedVersion: engagement.version, ...body });
      setEngagement(advisorEngagement(value, selection.customerId));
      setActivityNotice(success);
      return true;
    } catch {
      setActivityNotice('We could not save that change. Refresh the customer and try again.');
      return false;
    } finally { setActivityBusy(false); }
  }

  async function saveNote() {
    if (!note.trim() || selectedSources.length === 0) return;
    if (await mutateEngagement('notes', { note: note.trim(), sourceFactIds: selectedSources },
      'Advisory note saved.')) {
      setNote('');
      setSelectedSources([]);
    }
  }

  async function recordCommunication() {
    if (!communication.trim()) return;
    if (await mutateEngagement('communications', { message: communication.trim() },
      'Customer communication recorded. No message was sent.'))
      setCommunication('');
  }

  async function advanceApplication() {
    if (!engagement) return;
    const stage = nextAdvisorStage(engagement.applicationStage);
    if (!stage) return;
    await mutateEngagement('application-stage', { stage }, 'Application preparation updated.');
  }

  return (
    <main className="mx-auto w-full max-w-5xl p-6" aria-labelledby="advisor-title">
      <Title level={2} id="advisor-title">My Customers</Title>
      <Paragraph>Work with customers assigned to you and view information they have agreed to share.</Paragraph>
      <Card title="Your advisory work">
        <Descriptions column={1}>
          <Descriptions.Item label="Role">Financial Advisor / Relationship Manager</Descriptions.Item>
          <Descriptions.Item label="Customer relationships">{advisor.recordedRelationshipCount}</Descriptions.Item>
          <Descriptions.Item label="Customers available to review">{clients.length}</Descriptions.Item>
        </Descriptions>
        <Alert type="info" showIcon title="Customer information is protected"
          description="A customer appears here only while your assignment and the customer's permission to share are current." />
      </Card>

      <Card title="Financial information access" className="mt-4"
        extra={<Tag color={access?.hasAccess ? 'success' : access?.state === 'Submitted' ? 'processing' : 'warning'}>
          {access?.hasAccess ? 'Available' : access?.state === 'Submitted' ? 'Submitted · Under review' :
            access?.state === 'Rejected' ? 'Rejected' : 'Restricted'}
        </Tag>}>
        <Paragraph>
          Ask your Organization Administrator for permission to review financial information for customers assigned to you.
        </Paragraph>
        {accessLoading && <Spin aria-label="Checking access request status" />}
        {accessNotice && <Alert type={access?.state === 'Submitted' ? 'success' : 'warning'} showIcon title={accessNotice} />}
        {!accessLoading && access?.hasAccess && <Alert type="success" showIcon
          title="Financial information is available"
          description="Your assignment and the customer's permission to share are checked each time you open a review." />}
        {!accessLoading && access?.state === 'Submitted' && <Alert type="info" showIcon
          title="Your request is under review"
          description="An Organization Administrator can approve or reject it. You do not need to submit it again." />}
        {!accessLoading && access?.state === 'Rejected' && <Alert type="warning" showIcon
          title="The request was not approved"
          description={access.decisionReason ?? 'Permission was not granted. You can submit a new request if this option is still available.'} />}
        {!accessLoading && access && !access.hasAccess && access.canRequest &&
          <Button type="primary" onClick={() => { void requestAccess(); }} loading={accessBusy} disabled={accessBusy}>
            Request access
          </Button>}
        {!accessLoading && access && !access.hasAccess && !access.canRequest && access.state === 'NotRequested' &&
          <Alert type="info" showIcon title="This request is not available right now"
            description="Ask your Organization Administrator to review your role and permissions." />}
      </Card>

      <Card title="Customers you advise" className="mt-4"
        extra={<Button type="link" disabled={loading} onClick={() => { setRefresh(value => value + 1); }}>Refresh</Button>}>
        {loading && <Spin aria-label="Checking customer access" />}
        {notice && <Alert type="warning" showIcon title={notice} />}
        {!loading && clients.length === 0 && <Empty description="No customers are currently assigned to you with permission to share financial information." />}
        <div role="list" aria-label="Customers you advise">
          {clients.map(customer => (
            <div key={customer.customerId} role="listitem"
              className="flex items-center justify-between gap-4 border-b border-solid border-border px-2 py-4 last:border-b-0">
              <Text strong>{customer.displayName}</Text>
              <Button onClick={() => { void openReview(customer); }} disabled={loading}>Open customer</Button>
            </div>
          ))}
        </div>
      </Card>

      {review && selection && <Card title={<span>Financial information — {selection.displayName}</span>} className="mt-4"
        extra={<Tag>Read only</Tag>}>
        {review.facts.length === 0
          ? <Empty description="No reviewed financial information is available for this customer." />
          : <div role="list" aria-label="Reviewed financial information">
              {review.facts.map(fact => (
                <div role="listitem" key={fact.financialFactId}
                  className="border-b border-solid border-border px-2 py-4 last:border-b-0">
                  <div className="flex flex-wrap items-center justify-between gap-3">
                    <Checkbox checked={selectedSources.includes(fact.financialFactId)}
                      onChange={event => { setSelectedSources(previous => event.target.checked
                        ? [...previous, fact.financialFactId]
                        : previous.filter(value => value !== fact.financialFactId)); }}>
                      <Text strong>{fact.label}</Text>
                    </Checkbox>
                    <Tag>{categories[fact.factType] ?? 'Financial information'}</Tag>
                  </div>
                  <div className="mt-2"><Text>{amount(fact)}</Text></div>
                  <div className="mt-1"><Text type="secondary">Effective {date(fact.effectiveDate)} · Source: {fact.source.sourceName}</Text></div>
                </div>
              ))}
            </div>}
      </Card>}

      {review && selection && engagement && <>
        <Card title="Advisor notes" className="mt-4"
          extra={<Tag>{selectedSources.length} item{selectedSources.length === 1 ? '' : 's'} selected</Tag>}>
          <Paragraph>Select reviewed information above, then record your note. The note remains linked to the information you reviewed.</Paragraph>
          <Input.TextArea aria-label="Advisory note" value={note} maxLength={600} rows={3}
            onChange={event => { setNote(event.target.value); }}
            placeholder="Record the analysis preparation or customer follow-up needed." />
          <Button className="mt-3" type="primary" loading={activityBusy}
            disabled={activityBusy || !note.trim() || selectedSources.length === 0}
            onClick={() => { void saveNote(); }}>Save advisory note</Button>
        </Card>

        <Card title="Customer communication" className="mt-4">
          <Alert type="info" showIcon title="Communication record only"
            description="This records what should be communicated in the governed activity history. It does not send email, SMS or a notification." />
          <Input.TextArea className="mt-3" aria-label="Customer communication" value={communication} maxLength={600} rows={3}
            onChange={event => { setCommunication(event.target.value); }}
            placeholder="Record the customer-ready update or information request." />
          <Button className="mt-3" loading={activityBusy} disabled={activityBusy || !communication.trim()}
            onClick={() => { void recordCommunication(); }}>Record communication</Button>
        </Card>

        <Card title="Application preparation" className="mt-4"
          extra={<Tag color={engagement.applicationStage === 'Ready for submission' ? 'success' : 'processing'}>
            {engagement.applicationStage}</Tag>}>
          <Paragraph>Track preparation only. Underwriting, approval, pricing and lender decisions are outside this workspace.</Paragraph>
          {nextAdvisorStage(engagement.applicationStage) && <Button loading={activityBusy} disabled={activityBusy}
            onClick={() => { void advanceApplication(); }}>
            Move to {nextAdvisorStage(engagement.applicationStage)}
          </Button>}
          {!nextAdvisorStage(engagement.applicationStage) &&
            <Alert type="success" showIcon title="Ready for separate submission" description="No lender decision has been made." />}
        </Card>

        <Card title="Activity history" className="mt-4">
          {activityNotice && <Alert className="mb-4" showIcon
            type={activityNotice.includes('not confirmed') ? 'warning' : 'success'} title={activityNotice} />}
          {engagement.activities.length === 0 ? <Empty description="No advisory activity has been recorded." /> :
            <List dataSource={[...engagement.activities].reverse()} renderItem={activity => <List.Item>
              <Space orientation="vertical" size={0}>
                <Text strong>{activity.kind === 'NOTE' ? 'Advisory note' :
                  activity.kind === 'COMMUNICATION' ? 'Customer communication' : 'Application stage'}</Text>
                {activity.body && <Text>{activity.body}</Text>}
                {activity.applicationStage && <Text>{activity.applicationStage}</Text>}
                {activity.sourceFactIds.length > 0 && <Text type="secondary">
                  Linked to {activity.sourceFactIds.length} reviewed item{activity.sourceFactIds.length === 1 ? '' : 's'}
                </Text>}
                <Text type="secondary">{date(activity.occurredAt)}</Text>
              </Space>
            </List.Item>} />}
        </Card>
      </>}
    </main>
  );
}
