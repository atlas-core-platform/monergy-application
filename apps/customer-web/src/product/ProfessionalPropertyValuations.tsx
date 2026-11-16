import { Alert, Card, Empty, List, Typography } from 'antd';
import { useEffect, useState } from 'react';
import type { AccessApi, TenantSession } from '../access/accessApi';
import type { CustomerPropertyValuation } from '../property-valuer/propertyValuationModels';
import { customerPropertyValuations } from '../property-valuer/propertyValuationModels';

const { Paragraph, Text, Title } = Typography;

export default function ProfessionalPropertyValuations({ api, session }: { api: AccessApi; session: TenantSession }) {
  const [items, setItems] = useState<CustomerPropertyValuation[]>([]);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    const pending = new AbortController();
    const start = window.setTimeout(() => {
      setFailed(false);
      void api.case<unknown>('property-valuations/me', 'GET', undefined, pending.signal)
        .then(value => { if (!pending.signal.aborted) setItems(customerPropertyValuations(value, session.tenantId, session.actorId)); })
        .catch(() => { if (!pending.signal.aborted) setFailed(true); });
    }, 0);
    return () => { window.clearTimeout(start); pending.abort(); };
  }, [api, session]);
  return <section aria-label="Professional property valuations" className="mt-6">
    <Title level={2}>Professional property valuations</Title>
    <Alert type="info" title="Separate from your reported property value" description="These conclusions were recorded by an assigned Property Valuer. Your reported property records and Monergy calculations remain separate." />
    {failed ? <Alert className="mt-3" type="error" title="Professional property valuations are unavailable." /> :
      items.length === 0 ? <Empty description="No professional property valuation has been shared with you." /> :
      <List className="mt-3" dataSource={items} renderItem={item => <List.Item><Card className="w-full" title={item.propertyLabel} extra={`Revision ${String(item.revision)}`}>
        <Paragraph><Text strong>{item.currency} {item.marketValue.toLocaleString()}</Text> as of {item.effectiveDate}</Paragraph>
        <Paragraph>{item.basis}</Paragraph><Paragraph>{item.findings}</Paragraph>
        <Paragraph type="secondary">Recorded by Property Valuer {item.recordedBy} · {String(item.evidenceCount)} exact evidence versions · {item.dataDurability}</Paragraph>
      </Card></List.Item>} />}
  </section>;
}
