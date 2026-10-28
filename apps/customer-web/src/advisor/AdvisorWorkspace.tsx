import { Alert, Button, Card, Descriptions, Empty, Spin, Tag, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';

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
      setNotice('');
      return api.product<unknown>('v1/advisor/customers', 'GET', undefined, abort.signal);
    }).then(data => {
      if (abort.signal.aborted || data === null) return;
      setClients(authorizedClients(data, session.tenantId, session.actorId,
        advisor.policyVersion, advisor.subjectVersion));
    }).catch(() => {
      if (!abort.signal.aborted)
        setNotice('Authorized customer names could not be verified. Refresh to try again.');
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
          setAccessNotice('Your current access request status could not be verified. Refresh before trying again.');
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
    setSelection(null);
    try {
      const received = await api.product<unknown>(
        'v1/advisor/customers/' + encodeURIComponent(customer.customerId) + '/financial-profile');
      const value = financialReview(received);
      setSelection(customer);
      setReview(value);
    } catch {
      setNotice('Financial information is unavailable or access has changed. Review the assignment and current consent.');
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
      setAccessNotice('The request was not confirmed. Refresh its status before trying again.');
    } finally {
      setAccessBusy(false);
    }
  }

  return (
    <main className="mx-auto w-full max-w-5xl p-6" aria-labelledby="advisor-title">
      <Title level={2} id="advisor-title">Financial Advisor workspace</Title>
      <Paragraph>Review customer information only with your organization's permission and the customer's current consent.</Paragraph>
      <Card title="Your access">
        <Descriptions column={1}>
          <Descriptions.Item label="Role">Financial Advisor / Relationship Manager</Descriptions.Item>
          <Descriptions.Item label="Recorded client relationships">{advisor.recordedRelationshipCount}</Descriptions.Item>
          <Descriptions.Item label="Currently authorized for review">{clients.length}</Descriptions.Item>
        </Descriptions>
        <Alert type="info" showIcon title="Customer information is protected"
          description="A recorded relationship does not grant financial access. Both read permissions, a current assignment and customer consent are required for each review." />
      </Card>

      <Card title="Assigned customer financial review" className="mt-4"
        extra={<Tag color={access?.hasAccess ? 'success' : access?.state === 'Submitted' ? 'processing' : 'warning'}>
          {access?.hasAccess ? 'Available' : access?.state === 'Submitted' ? 'Submitted · Under review' :
            access?.state === 'Rejected' ? 'Rejected' : 'Restricted'}
        </Tag>}>
        <Paragraph>
          Review financial information and its supporting documents for customers who are separately assigned to you and have current consent.
        </Paragraph>
        {accessLoading && <Spin aria-label="Checking access request status" />}
        {accessNotice && <Alert type={access?.state === 'Submitted' ? 'success' : 'warning'} showIcon title={accessNotice} />}
        {!accessLoading && access?.hasAccess && <Alert type="success" showIcon
          title="Financial review access is fulfilled"
          description="Customer assignment, record access and current customer consent are still checked for every review." />}
        {!accessLoading && access?.state === 'Submitted' && <Alert type="info" showIcon
          title="Your request is under review"
          description="An Organization Administrator can approve or reject this bounded request. Submitting again will not create a duplicate." />}
        {!accessLoading && access?.state === 'Rejected' && <Alert type="warning" showIcon
          title="The request was not approved"
          description={access.decisionReason ?? 'No access was granted. You may submit a new request if the capability is still available.'} />}
        {!accessLoading && access && !access.hasAccess && access.canRequest &&
          <Button type="primary" onClick={() => { void requestAccess(); }} loading={accessBusy} disabled={accessBusy}>
            Request access
          </Button>}
        {!accessLoading && access && !access.hasAccess && !access.canRequest && access.state === 'NotRequested' &&
          <Alert type="info" showIcon title="This access cannot be requested right now"
            description="The capability is visible, but an Organization Administrator cannot grant it in its current configuration." />}
      </Card>

      <Card title="Customers available for review" className="mt-4"
        extra={<Button type="link" disabled={loading} onClick={() => { setRefresh(value => value + 1); }}>Refresh access</Button>}>
        {loading && <Spin aria-label="Checking customer access" />}
        {notice && <Alert type="warning" showIcon title={notice} />}
        {!loading && clients.length === 0 && <Empty description="No customer reviews are authorized yet. Your organization must assign the customer and approve read permissions, and the customer must grant consent." />}
        <div role="list" aria-label="Authorized customers">
          {clients.map(customer => (
            <div key={customer.customerId} role="listitem"
              className="flex items-center justify-between gap-4 border-b border-solid border-border px-2 py-4 last:border-b-0">
              <Text strong>{customer.displayName}</Text>
              <Button onClick={() => { void openReview(customer); }} disabled={loading}>Review information</Button>
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
                    <Text strong>{fact.label}</Text>
                    <Tag>{categories[fact.factType] ?? 'Financial information'}</Tag>
                  </div>
                  <div className="mt-2"><Text>{amount(fact)}</Text></div>
                  <div className="mt-1"><Text type="secondary">Effective {date(fact.effectiveDate)} · Source: {fact.source.sourceName}</Text></div>
                </div>
              ))}
            </div>}
      </Card>}
    </main>
  );
}
