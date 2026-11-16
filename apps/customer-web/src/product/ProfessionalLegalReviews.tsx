import { Alert, Card, Empty, List, Typography } from 'antd';
import { useEffect, useState } from 'react';
import type { AccessApi, TenantSession } from '../access/accessApi';
import type { CustomerLegalReview } from '../legal-advisor/legalReviewModels';
import { customerLegalReviews, outcomeLabel } from '../legal-advisor/legalReviewModels';

const { Paragraph, Text, Title } = Typography;

export default function ProfessionalLegalReviews({ api, session }: { api: AccessApi; session: TenantSession }) {
  const [items, setItems] = useState<CustomerLegalReview[]>([]);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    const pending = new AbortController();
    const start = window.setTimeout(() => {
      setFailed(false);
      void api.case<unknown>('legal-reviews/me', 'GET', undefined, pending.signal)
        .then(value => { if (!pending.signal.aborted) setItems(customerLegalReviews(value, session.tenantId, session.actorId)); })
        .catch(() => { if (!pending.signal.aborted) setFailed(true); });
    }, 0);
    return () => { window.clearTimeout(start); pending.abort(); };
  }, [api, session]);
  return <section aria-label="Professional legal reviews" className="mt-6">
    <Title level={2}>Professional legal reviews</Title>
    <Alert type="info" title="Customer-safe professional outcomes" description="These findings were recorded by an assigned Legal Advisor against exact Evidence document versions. Credentials and document contents are not shown here." />
    {failed ? <Alert className="mt-3" type="error" title="Professional legal reviews are unavailable." /> :
      items.length === 0 ? <Empty description="No professional legal review has been shared with you." /> :
      <List className="mt-3" dataSource={items} renderItem={item => <List.Item><Card className="w-full" title={item.matterLabel} extra={`Revision ${String(item.revision)}`}>
        <Paragraph><Text strong>{outcomeLabel(item.outcome)}</Text></Paragraph>
        <Paragraph>{item.findings}</Paragraph>
        <Paragraph type="secondary">Recorded by Legal Advisor {item.recordedBy} · {String(item.evidenceCount)} exact document versions · {item.dataDurability}</Paragraph>
      </Card></List.Item>} />}
  </section>;
}
