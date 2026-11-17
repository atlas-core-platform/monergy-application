import { Alert, Button, Card, Descriptions, Empty, Form, Input, List, Radio, Space, Spin, Tag, Typography } from 'antd';
import { useCallback, useEffect, useState } from 'react';
import { WorkspaceEntry } from '../access/WorkspaceEntry';
import { WorkspaceSessionProvider } from '../access/WorkspaceSessionProvider';
import { useWorkspaceSession } from '../access/workspaceSession';
import { lenderApplication, lenderApplicationPage, operationId } from './lenderApplicationModels';
import type { LenderApplication } from './lenderApplicationModels';
import { readableDate } from '../ux/personaCopy';

const { Paragraph, Text, Title } = Typography;
type Decision = 'Approved' | 'Declined' | 'More information required';

function Workspace() {
  const { api, session, authorization } = useWorkspaceSession();
  const lender = authorization?.area === 'lender' ? authorization : null;
  const [items, setItems] = useState<LenderApplication[]>([]);
  const [loadedFor, setLoadedFor] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState('');
  const [decision, setDecision] = useState<Decision>('More information required');
  const [reason, setReason] = useState('');

  const refresh = useCallback(async () => {
    if (!api || !session || !lender) return;
    setLoading(true);
    setNotice('');
    try {
      const value = lenderApplicationPage(await api.case<unknown>('lender/applications'),
        session.tenantId, session.actorId);
      setItems(value.items);
      setLoadedFor(session.tenantId + ':' + session.actorId);
      setSelected(current => current && value.items.some(item => item.applicationId === current)
        ? current : value.items[0]?.applicationId ?? null);
    } catch {
      setItems([]);
      setLoadedFor(session.tenantId + ':' + session.actorId);
      setSelected(null);
      setNotice('Assigned applications could not be verified. Reconnect before continuing.');
    } finally { setLoading(false); }
  }, [api, session, lender]);

  useEffect(() => {
    if (!api || !session || !lender) return;
    let active = true;
    void api.case<unknown>('lender/applications').then(value => {
      if (!active) return;
      const page = lenderApplicationPage(value, session.tenantId, session.actorId);
      setItems(page.items);
      setLoadedFor(session.tenantId + ':' + session.actorId);
      setSelected(page.items[0]?.applicationId ?? null);
      setNotice('');
    }).catch(() => {
      if (!active) return;
      setItems([]);
      setLoadedFor(session.tenantId + ':' + session.actorId);
      setSelected(null);
      setNotice('Assigned applications could not be verified. Reconnect before continuing.');
    }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [api, session, lender]);
  if (!api || !session || !lender) return null;
  const scopedItems = loadedFor === session.tenantId + ':' + session.actorId ? items : [];
  const current = scopedItems.find(item => item.applicationId === selected) ?? null;

  async function mutate(path: string, body: Record<string, unknown>, success: string) {
    if (!api || !current || busy) return false;
    setBusy(true);
    setNotice('');
    try {
      const updated = lenderApplication(await api.case<unknown>(path, 'POST', {
        operationId: operationId(), expectedVersion: current.version, ...body,
      }));
      setItems(values => values.map(value => value.applicationId === updated.applicationId ? updated : value));
      setNotice(success);
      return true;
    } catch {
      setNotice('The action was not confirmed. Your assignment, role or this application may have changed; refresh before retrying.');
      return false;
    } finally { setBusy(false); }
  }

  async function recordDecision() {
    if (!current || reason.trim().length < 3) return;
    const recorded = await mutate('lender/applications/' + encodeURIComponent(current.applicationId) + '/decision',
      { decision, reason: reason.trim() }, 'Decision recorded.');
    if (recorded) setReason('');
  }

  return <main className="mx-auto w-full max-w-6xl p-6" aria-labelledby="lender-title">
    <Title level={2} id="lender-title">Applications to Review</Title>
    <Paragraph>Review applications assigned to you and record your own decision.</Paragraph>
    <Alert showIcon type="info" title="The decision remains yours"
      description="Customer-reviewed information and calculations are provided to support your review. Monergy does not decide eligibility, pricing or creditworthiness." />
    <Card className="mt-4" title="Your work">
      <Descriptions column={1}>
        <Descriptions.Item label="Role">Lender Professional</Descriptions.Item>
        <Descriptions.Item label="Assigned applications">{scopedItems.length}</Descriptions.Item>
      </Descriptions>
    </Card>
    <Card className="mt-4" title="Applications assigned to you"
      extra={<Button type="link" onClick={() => { void refresh(); }} disabled={loading || busy}>Refresh</Button>}>
      {loading && <Spin aria-label="Loading assigned applications" />}
      {notice && <Alert className="mb-4" showIcon
        type={notice.includes('was recorded') || notice.includes('was marked reviewed') ? 'success' : 'warning'} title={notice} />}
      {!loading && scopedItems.length === 0 && <Empty description="You don't have any applications waiting for review." />}
      <List dataSource={scopedItems} renderItem={item => <List.Item
        actions={[<Button key="open" onClick={() => { setSelected(item.applicationId); }}>Open review</Button>]}>
        <List.Item.Meta title={item.applicationReference}
          description={`Assigned ${readableDate(item.assignedAt)}`} />
        <Tag color={item.state === 'Decided' ? 'success' : item.state === 'Under review' ? 'processing' : 'default'}>{item.state}</Tag>
      </List.Item>} />
    </Card>

    {current && <Card className="mt-4" title={current.applicationReference}>
      <Title level={4}>Required documents</Title>
      <List dataSource={current.requiredDocuments} renderItem={document => <List.Item
        actions={!document.reviewed && document.present && current.state !== 'Decided'
          ? [<Button key="review" disabled={busy} onClick={() => { void mutate(
              'lender/applications/' + encodeURIComponent(current.applicationId) + '/documents/' + encodeURIComponent(document.code) + '/review',
              {}, `${document.label} was marked reviewed.`); }}>Mark reviewed</Button>] : []}>
        <List.Item.Meta title={document.label} description={!document.present ? 'Not supplied' : document.reviewed
          ? `Reviewed ${document.reviewedAt ? new Date(document.reviewedAt).toLocaleString() : ''}` : 'Available for review'} />
        <Tag color={!document.present ? 'warning' : document.reviewed ? 'success' : 'processing'}>
          {!document.present ? 'Missing' : document.reviewed ? 'Reviewed' : 'Ready'}
        </Tag>
      </List.Item>} />

      {current.decision ? <Alert className="mt-4" showIcon type="success"
        title={`Recorded decision: ${current.decision.decision}`}
        description={<><Text>{current.decision.reason}</Text><br /><Text type="secondary">Recorded {new Date(current.decision.decidedAt).toLocaleString()}</Text></>} />
      : <Form className="mt-4" layout="vertical" onFinish={() => { void recordDecision(); }}>
        <Title level={4}>Record lender decision</Title>
        <Form.Item label="Decision">
          <Radio.Group value={decision} onChange={event => { setDecision(event.target.value as Decision); }}>
            <Space orientation="vertical">
              <Radio value="Approved">Approved</Radio>
              <Radio value="Declined">Declined</Radio>
              <Radio value="More information required">More information required</Radio>
            </Space>
          </Radio.Group>
        </Form.Item>
        <Form.Item label="Decision reason" required>
          <Input.TextArea aria-label="Decision reason" value={reason} maxLength={600} rows={4}
            onChange={event => { setReason(event.target.value); }} />
        </Form.Item>
        <Button htmlType="submit" type="primary" loading={busy} disabled={busy || reason.trim().length < 3 ||
          current.requiredDocuments.some(document => !document.present || !document.reviewed)}>
          Record decision
        </Button>
        {current.requiredDocuments.some(document => !document.present || !document.reviewed) &&
          <Paragraph type="secondary" className="mt-2">Review every required document before recording a decision.</Paragraph>}
      </Form>}
    </Card>}
  </main>;
}

export default function LenderWorkspace() {
  return <WorkspaceSessionProvider area="lender"><WorkspaceEntry area="lender"><Workspace /></WorkspaceEntry></WorkspaceSessionProvider>;
}
