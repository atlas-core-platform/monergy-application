import { Alert, Button, Card, Descriptions, List, Spin, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';

const { Title, Paragraph, Text } = Typography;

interface AuthorizedClients {
  tenantId: string;
  actorId: string;
  policyVersion: number;
  subjectVersion: number;
  customerIds: string[];
}
interface ReviewedFact {
  financialFactId: string;
  factType: string;
  label: string;
  value: number;
  currency: string;
  effectiveDate: string;
  source: { sourceName: string };
}
interface FinancialReview {
  revision: number;
  facts: ReviewedFact[];
}

// The server returns only currently consent-authorized client IDs. No ID is
// displayed to the user, cached in local storage, or trusted as authorization.
export default function AdvisorWorkspace() {
  const { authorization, api, session } = useWorkspaceSession();
  const [clients, setClients] = useState<string[]>([]);
  const [review, setReview] = useState<FinancialReview | null>(null);
  const [loading, setLoading] = useState(false);
  const [notice, setNotice] = useState('');
  const advisor = authorization?.area === 'advisor' ? authorization : null;

  useEffect(() => {
    if (!advisor || !api || !session) return;
    const abort = new AbortController();
    setLoading(true);
    setClients([]);
    setReview(null);
    setNotice('');
    void api.request<AuthorizedClients>('advisor/customers', 'GET', undefined, abort.signal)
      .then(data => {
        if (abort.signal.aborted) return;
        if (data.tenantId !== session.tenantId || data.actorId !== session.actorId ||
            data.policyVersion !== advisor.policyVersion || data.subjectVersion !== advisor.subjectVersion ||
            !Array.isArray(data.customerIds) || data.customerIds.length > 50 ||
            data.customerIds.some(id => typeof id !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$/.test(id)) ||
            new Set(data.customerIds).size !== data.customerIds.length)
          throw new Error('ADVISOR_LIST_INVALID');
        setClients(data.customerIds);
      }).catch(() => { if (!abort.signal.aborted) setNotice('Authorized customer assignments could not be verified. Refresh to try again.'); })
      .finally(() => { if (!abort.signal.aborted) setLoading(false); });
    return () => abort.abort();
  }, [advisor?.policyVersion, advisor?.subjectVersion, api, session?.actorId, session?.tenantId]);

  if (!advisor) return null;

  async function openReview(customerId: string) {
    if (!api || !clients.includes(customerId) || loading) return;
    setLoading(true);
    setNotice('');
    setReview(null);
    try {
      const value = await api.product<FinancialReview>(
        'v1/advisor/customers/' + encodeURIComponent(customerId) + '/financial-profile');
      if (!Array.isArray(value.facts) || !Number.isSafeInteger(value.revision) ||
          value.facts.some(fact => typeof fact.label !== 'string' ||
            typeof fact.value !== 'number' || typeof fact.currency !== 'string' ||
            typeof fact.source?.sourceName !== 'string'))
        throw new Error('ADVISOR_PROFILE_INVALID');
      setReview(value);
    } catch {
      setNotice('Financial information is unavailable or access has changed. Review the assignment and current consent.');
    } finally {
      setLoading(false);
    }
  }

  return (
    <main className="mx-auto w-full max-w-5xl p-6" aria-labelledby="advisor-title">
      <Title level={2} id="advisor-title">Financial Advisor workspace</Title>
      <Paragraph>Review assigned customer information only when your organization permits it and the customer has active consent.</Paragraph>
      <Card title="Your access">
        <Descriptions column={1}>
          <Descriptions.Item label="Role">Financial Advisor / Relationship Manager</Descriptions.Item>
          <Descriptions.Item label="Recorded client relationships">{advisor.recordedRelationshipCount}</Descriptions.Item>
          <Descriptions.Item label="Currently authorized for review">{clients.length}</Descriptions.Item>
        </Descriptions>
        <Alert type="info" showIcon title="Customer information is protected"
          description="A recorded relationship is not permission to view a customer's finances. Only assignments with both permitted financial and document access and current customer consent appear below." />
      </Card>
      <Card title="Customers available for review" className="mt-4">
        {loading && <Spin aria-label="Checking customer access" />}
        {notice && <Alert type="warning" showIcon title={notice} />}
        {!loading && clients.length === 0 && <Paragraph>No customer reviews are authorized yet. An Organization Administrator must configure the appropriate read permissions and customer assignment, and the customer must grant consent.</Paragraph>}
        <List dataSource={clients} renderItem={(id, index) => (
          <List.Item actions={[<Button key="review" onClick={() => { void openReview(id); }} disabled={loading}>Review information</Button>]}>
            <Text>Assigned customer {index + 1}</Text>
          </List.Item>
        )} />
      </Card>
      {review && <Card title="Financial information (read only)" className="mt-4">
        {review.facts.length === 0 && <Paragraph>No reviewed financial information is available.</Paragraph>}
        <List dataSource={review.facts} renderItem={fact => (
          <List.Item>
            <div><Text strong>{fact.label}</Text><div><Text>{fact.currency} {fact.value}</Text></div>
              <Text type="secondary">Source: {fact.source.sourceName}</Text>
            </div>
          </List.Item>
        )} />
      </Card>}
    </main>
  );
}
