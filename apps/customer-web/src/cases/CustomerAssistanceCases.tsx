import { Alert, Button, Card, Empty, List, Modal, Space, Spin, Tag, Typography } from 'antd';
import { useCallback, useEffect, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';
import { caseHistory, caseItem, casePage, displayDate, operationId } from './caseModels';
import type { AssistanceCase, AssistanceCaseEvent } from './caseModels';

const { Paragraph, Text, Title } = Typography;

export default function CustomerAssistanceCases() {
  const { api, session, authorization } = useWorkspaceSession();
  const [items, setItems] = useState<AssistanceCase[]>([]);
  const [events, setEvents] = useState<AssistanceCaseEvent[]>([]);
  const [historyCase, setHistoryCase] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState('');
  const customer = authorization?.area === 'product' ? authorization.context : null;

  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (!api || !session || !customer) return;
    setLoading(true);
    setNotice('');
    try {
      const value = await api.case<unknown>('me', 'GET', undefined, signal);
      setItems(casePage(value, session.tenantId, session.actorId));
    } catch {
      if (!signal?.aborted) setNotice('Your assistance cases could not be verified. Refresh before trying again.');
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [api, customer, session]);

  useEffect(() => {
    const controller = new AbortController();
    void Promise.resolve().then(() => refresh(controller.signal)).catch(() => undefined);
    return () => { controller.abort(); };
  }, [refresh]);

  if (!customer || !api || !session) return null;
  const caseApi = api;
  const active = items.length > 0;

  async function createCase() {
    if (busy || active) return;
    setBusy(true);
    setNotice('');
    try {
      const received = await caseApi.case<unknown>('me', 'POST', { operationId: operationId() });
      const created = caseItem(received);
      setItems(previous => [created, ...previous.filter(item => item.caseId !== created.caseId)]);
      setNotice('Your assistance case was submitted to the DSA / Case Manager queue.');
    } catch {
      setNotice('The case was not confirmed. Refresh your case list before trying again.');
    } finally { setBusy(false); }
  }

  async function openHistory(item: AssistanceCase) {
    setHistoryCase(item.caseId);
    setEvents([]);
    try {
      const value = await caseApi.case<unknown>('me/' + encodeURIComponent(item.caseId) + '/history');
      setEvents(caseHistory(value, item.caseId));
    } catch {
      setNotice('The case history could not be verified.');
      setHistoryCase(null);
    }
  }

  return (
    <main className="mx-auto w-full max-w-5xl p-6" aria-labelledby="assistance-cases-title">
      <Title level={2} id="assistance-cases-title">Assistance cases</Title>
      <Paragraph>Ask an eligible DSA / Case Manager in your organization to accept a bounded assistance case.</Paragraph>
      <Alert type="info" showIcon title="Your financial information stays protected"
        description="Submitting or accepting a case does not share documents or financial information, create consent, make an assignment, or authorize a lender decision." />
      <Card className="mt-4" title="Your cases" extra={
        <Button type="primary" loading={busy} disabled={busy || active} onClick={() => { void createCase(); }}>
          Request assistance
        </Button>
      }>
        {notice && <Alert className="mb-4" showIcon type={notice.startsWith('Your assistance') ? 'success' : 'warning'} title={notice} />}
        {loading ? <Spin aria-label="Loading assistance cases" /> : items.length === 0 ?
          <Empty description="No assistance case has been submitted." /> :
          <List dataSource={items} renderItem={item => (
            <List.Item actions={[<Button key="history" type="link" onClick={() => { void openHistory(item); }}>View history</Button>] }>
              <List.Item.Meta title={<Space><Text strong>{item.requestType}</Text>
                <Tag color={item.state === 'Accepted' ? 'success' : 'processing'}>{item.state}</Tag></Space>}
                description={`Submitted ${displayDate(item.createdAt)} · Reference ${item.caseId}`} />
            </List.Item>
          )} />}
      </Card>
      <Modal open={historyCase !== null} title="Case history" footer={<Button onClick={() => { setHistoryCase(null); }}>Close</Button>}
        onCancel={() => { setHistoryCase(null); }}>
        {events.length === 0 ? <Spin aria-label="Loading case history" /> :
          <List dataSource={events} renderItem={event => <List.Item>
            <Space orientation="vertical" size={0}><Text strong>{event.action}</Text><Text type="secondary">{displayDate(event.occurredAt)}</Text></Space>
          </List.Item>} />}
      </Modal>
    </main>
  );
}
