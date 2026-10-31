import { Alert, Button, Card, Empty, List, Space, Spin, Tag, Typography } from 'antd';
import { useCallback, useEffect, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';
import { caseItem, casePage, displayDate, operationId } from './caseModels';
import type { AssistanceCase } from './caseModels';

const { Paragraph, Text, Title } = Typography;

export default function DsaCaseWorkspace() {
  const { api, session, authorization } = useWorkspaceSession();
  const [items, setItems] = useState<AssistanceCase[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState('');
  const dsa = authorization?.area === 'dsa' ? authorization : null;

  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (!api || !session || !dsa) return;
    setLoading(true);
    setNotice('');
    try {
      const value = await api.case<unknown>('work', 'GET', undefined, signal);
      setItems(casePage(value, session.tenantId, session.actorId));
    } catch {
      if (!signal?.aborted) setNotice('The assistance queue could not be verified. Refresh before trying again.');
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [api, dsa, session]);

  useEffect(() => {
    const controller = new AbortController();
    void Promise.resolve().then(() => refresh(controller.signal)).catch(() => undefined);
    return () => { controller.abort(); };
  }, [refresh]);

  if (!dsa || !api || !session) return null;
  const caseApi = api;

  async function accept(item: AssistanceCase) {
    if (busy || item.state !== 'Submitted') return;
    setBusy(item.caseId);
    setNotice('');
    try {
      const received = await caseApi.case<unknown>(encodeURIComponent(item.caseId) + '/accept', 'POST', {
        operationId: operationId(), expectedVersion: item.version,
      });
      const accepted = caseItem(received);
      setItems(previous => previous.map(value => value.caseId === accepted.caseId ? accepted : value));
      setNotice('The assistance case is now assigned to you.');
    } catch {
      setNotice('The case was not accepted. It may have changed or been accepted by another Case Manager. Refresh the queue.');
    } finally { setBusy(null); }
  }

  return (
    <main className="mx-auto w-full max-w-5xl p-6" aria-labelledby="dsa-case-title">
      <Title level={2} id="dsa-case-title">DSA case workspace</Title>
      <Paragraph>Accept one submitted assistance case at a time using your current DSA / Case Manager authority.</Paragraph>
      <Alert type="info" showIcon title="Acceptance is not financial access"
        description="This queue contains no financial facts or documents. Customer data access, Consent, assignments, advice, underwriting and lender decisions remain separate." />
      <Card className="mt-4" title="Assistance queue" extra={<Button onClick={() => { void refresh(); }} disabled={loading || busy !== null}>Refresh</Button>}>
        {notice && <Alert className="mb-4" showIcon type={notice.startsWith('The assistance case is') ? 'success' : 'warning'} title={notice} />}
        {loading ? <Spin aria-label="Loading assistance queue" /> : items.length === 0 ?
          <Empty description="No assistance cases are waiting." /> :
          <List dataSource={items} renderItem={item => <List.Item actions={item.state === 'Submitted' ? [
            <Button key="accept" type="primary" loading={busy === item.caseId} disabled={busy !== null}
              onClick={() => { void accept(item); }}>Accept case</Button>,
          ] : []}>
            <List.Item.Meta title={<Space><Text strong>{item.requestType}</Text>
              <Tag color={item.state === 'Accepted' ? 'success' : 'processing'}>{item.assignedToMe ? 'Assigned to you' : item.state}</Tag></Space>}
              description={`Submitted ${displayDate(item.createdAt)} · Reference ${item.caseId}`} />
          </List.Item>} />}
      </Card>
    </main>
  );
}
