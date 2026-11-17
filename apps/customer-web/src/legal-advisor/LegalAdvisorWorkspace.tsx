import { Alert, Button, Card, Empty, Form, Input, List, Select, Tag, Typography } from 'antd';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import { WorkspaceEntry } from '../access/WorkspaceEntry';
import { WorkspaceSessionProvider } from '../access/WorkspaceSessionProvider';
import type { LegalOutcome, LegalReview } from './legalReviewModels';
import { legalReview, legalReviewPage, operationId, outcomeLabel } from './legalReviewModels';
import { readableDateTime } from '../ux/personaCopy';

const { Paragraph, Text, Title } = Typography;
interface OutcomeForm { outcome: LegalOutcome; findings: string }

function Workspace() {
  const { status, session, api, authorization } = useWorkspaceSession();
  const advisor = authorization?.area === 'legal-advisor' ? authorization : null;
  const [items, setItems] = useState<LegalReview[]>([]);
  const [failure, setFailure] = useState('');
  const [busy, setBusy] = useState('');
  const request = useRef<AbortController | null>(null);
  const [form] = Form.useForm();
  const current = items[0] ?? null;

  const load = useCallback(async () => {
    request.current?.abort();
    if (status !== 'authorized' || !session || !api || !advisor) { setItems([]); return; }
    const pending = new AbortController(); request.current = pending; setFailure('');
    try {
      const value = legalReviewPage(await api.case<unknown>('legal-advisor/reviews', 'GET', undefined, pending.signal),
        session.tenantId, session.actorId);
      if (!pending.signal.aborted) setItems(value.items);
    } catch (error) {
      if (!pending.signal.aborted) setFailure(error instanceof AccessApiError && error.status === 403
        ? 'You no longer have access to this legal review.'
        : 'We could not load your legal reviews. Try again.');
    }
  }, [status, session, api, advisor]);
  useEffect(() => {
    const start = window.setTimeout(() => { void load(); }, 0);
    return () => { window.clearTimeout(start); request.current?.abort(); };
  }, [load]);
  const replace = (value: unknown) => {
    setItems(previous => previous.map(item =>
      item.reviewId === (value as { reviewId?: string }).reviewId ? legalReview(value) : item));
  };
  const mutate = async (path: string, body: object, key: string) => {
    if (!api) return false; setBusy(key); setFailure('');
    try { replace(await api.case<unknown>(path, 'POST', body)); return true; }
    catch (error) { setFailure(error instanceof AccessApiError && error.status === 409
      ? 'This matter changed while you were working. Refresh to see the latest information.'
      : 'We could not record the legal review. Try again.'); return false; }
    finally { setBusy(''); }
  };
  const record = async (values: OutcomeForm) => {
    if (!current) return;
    const recorded = await mutate('legal-advisor/reviews/' + encodeURIComponent(current.reviewId) + '/revisions', {
      operationId: operationId(), expectedVersion: current.version, outcome: values.outcome, findings: values.findings,
    }, 'outcome');
    if (recorded) form.resetFields();
  };
  const latest = useMemo(() => current?.revisions.at(-1) ?? null, [current]);
  if (!current) return <main className="mx-auto w-full max-w-6xl p-6" aria-labelledby="legal-advisor-title">
    <Title level={2} id="legal-advisor-title">Legal Reviews</Title>
    <Alert type="info" title="Professional legal review stays human-owned" description="Only an explicitly assigned Legal Advisor may inspect the assigned document versions and record a professional outcome. Monergy does not infer legal findings." />
    {failure ? <Alert className="mt-4" type="error" title={failure} action={<Button onClick={() => void load()}>Try again</Button>} /> : <Empty className="mt-8" description="You don't have any legal reviews waiting for you." />}
  </main>;
  return <main className="mx-auto w-full max-w-6xl p-6" aria-labelledby="legal-advisor-title">
    <Title level={2} id="legal-advisor-title">Legal Reviews</Title>
    <Alert type="info" title="Your professional review" description="Review the documents assigned to this matter and record your own findings. Monergy does not generate the legal outcome." />
    {failure && <Alert className="mt-4" type="error" title={failure} action={<Button onClick={() => void load()}>Refresh</Button>} />}
    <Card className="mt-5" title={current.matterLabel} extra={<Tag>{current.state}</Tag>}>
      <Paragraph><Text strong>Assigned matter:</Text> {current.matterReference}</Paragraph>
      <Title level={4}>Documents</Title>
      <List dataSource={current.evidence} renderItem={document => <List.Item actions={[document.inspected
        ? <Tag key="done" color="success">Inspected</Tag>
        : <Button key="inspect" loading={busy === document.code} onClick={() => void mutate(
            'legal-advisor/reviews/' + encodeURIComponent(current.reviewId) + '/documents/' + encodeURIComponent(document.code) + '/inspection',
            { operationId: operationId(), expectedVersion: current.version }, document.code)}>Mark reviewed</Button>] }>
        <List.Item.Meta title={document.label} description="Included with this matter" />
      </List.Item>} />
      <Form form={form} layout="vertical" className="mt-6" onFinish={(values: OutcomeForm) => { void record(values); }}>
        <Form.Item label="Professional outcome" name="outcome" rules={[{ required: true }]}>
          <Select options={(['NO_ISSUE_IDENTIFIED', 'ACTION_REQUIRED', 'UNABLE_TO_CONCLUDE'] as LegalOutcome[])
            .map(value => ({ value, label: outcomeLabel(value) }))} />
        </Form.Item>
        <Form.Item label="Professional findings" name="findings" rules={[{ required: true, min: 3 }]}>
          <Input.TextArea maxLength={2000} rows={4} />
        </Form.Item>
        <Button type="primary" htmlType="submit" loading={busy === 'outcome'} disabled={current.evidence.some(value => !value.inspected)}>
          {latest ? 'Record revised outcome' : 'Record outcome'}
        </Button>
      </Form>
    </Card>
    <List className="mt-5" header={<Title level={3}>Review history</Title>} dataSource={[...current.revisions].reverse()}
      locale={{ emptyText: 'No professional outcome has been recorded.' }} renderItem={revision => <List.Item>
        <List.Item.Meta title={outcomeLabel(revision.outcome)}
          description={`${readableDateTime(revision.recordedAt)} · ${revision.findings} · ${String(revision.evidence.length)} document${revision.evidence.length === 1 ? '' : 's'} reviewed`} />
      </List.Item>} />
  </main>;
}

export default function LegalAdvisorWorkspace() {
  return <WorkspaceSessionProvider area="legal-advisor"><WorkspaceEntry area="legal-advisor"><Workspace /></WorkspaceEntry></WorkspaceSessionProvider>;
}
