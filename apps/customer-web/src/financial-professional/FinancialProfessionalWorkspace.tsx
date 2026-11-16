import { Alert, Button, Card, Checkbox, Descriptions, Empty, Form, Input, List, Radio, Space, Spin, Tag, Typography } from 'antd';
import { useCallback, useEffect, useState } from 'react';
import { WorkspaceEntry } from '../access/WorkspaceEntry';
import { WorkspaceSessionProvider } from '../access/WorkspaceSessionProvider';
import { useWorkspaceSession } from '../access/workspaceSession';
import { financialProfessionalReview, financialProfessionalReviewPage, operationId } from './financialProfessionalModels';
import type { FinancialProfessionalReview } from './financialProfessionalModels';

const { Paragraph, Text, Title } = Typography;
type Outcome = 'Certified with no exceptions' | 'Certified with exceptions' | 'Unable to certify';

function Workspace() {
  const { api, session, authorization } = useWorkspaceSession();
  const professional = authorization?.area === 'financial-professional' ? authorization : null;
  const [items, setItems] = useState<FinancialProfessionalReview[]>([]);
  const [loadedFor, setLoadedFor] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState('');
  const [note, setNote] = useState('');
  const [noteSources, setNoteSources] = useState<string[]>([]);
  const [outcome, setOutcome] = useState<Outcome>('Unable to certify');
  const [rationale, setRationale] = useState('');

  const refresh = useCallback(async () => {
    if (!api || !session || !professional) return;
    setLoading(true); setNotice('');
    try {
      const value = financialProfessionalReviewPage(await api.case<unknown>('financial-professional/reviews'),
        session.tenantId, session.actorId);
      setItems(value.items); setLoadedFor(session.tenantId + ':' + session.actorId);
      setSelected(current => current && value.items.some(item => item.reviewId === current)
        ? current : value.items[0]?.reviewId ?? null);
    } catch {
      setItems([]); setLoadedFor(session.tenantId + ':' + session.actorId); setSelected(null);
      setNotice('Assigned reviews could not be verified. Confirm customer sharing, your assignment and current role before retrying.');
    } finally { setLoading(false); }
  }, [api, session, professional]);

  useEffect(() => {
    const start = window.setTimeout(() => { void refresh(); }, 0);
    return () => { window.clearTimeout(start); };
  }, [refresh]);
  if (!api || !session || !professional) return null;
  const scoped = loadedFor === session.tenantId + ':' + session.actorId ? items : [];
  const current = scoped.find(item => item.reviewId === selected) ?? null;

  async function mutate(path: string, body: Record<string, unknown>, success: string) {
    if (!api || !current || busy) return false;
    setBusy(true); setNotice('');
    try {
      const updated = financialProfessionalReview(await api.case<unknown>(path, 'POST', {
        operationId: operationId(), expectedVersion: current.version, ...body,
      }));
      setItems(values => values.map(value => value.reviewId === updated.reviewId ? updated : value));
      setNotice(success); return true;
    } catch {
      setNotice('The action was not confirmed. The assignment, source version, customer sharing or your role may have changed; refresh before retrying.');
      return false;
    } finally { setBusy(false); }
  }

  async function addNote() {
    if (!current || note.trim().length < 3 || noteSources.length === 0) return;
    const recorded = await mutate('financial-professional/reviews/' + encodeURIComponent(current.reviewId) + '/notes',
      { note: note.trim(), sourceCodes: noteSources }, 'Your source-linked professional note was recorded.');
    if (recorded) { setNote(''); setNoteSources([]); }
  }

  async function certify() {
    if (!current || rationale.trim().length < 3) return;
    const recorded = await mutate('financial-professional/reviews/' + encodeURIComponent(current.reviewId) + '/certification',
      { outcome, rationale: rationale.trim() }, 'The signer-owned certification and source snapshot were recorded.');
    if (recorded) setRationale('');
  }

  return <main className="mx-auto w-full max-w-6xl p-6" aria-labelledby="financial-professional-title">
    <Title level={2} id="financial-professional-title">Chartered Accountant / Financial Professional workspace</Title>
    <Paragraph>Review only financial information explicitly assigned to you and currently shared by the customer. Monergy records source versions and your professional conclusion; it does not create tax, audit or statutory assurance.</Paragraph>
    <Alert showIcon type="info" title="Professional responsibility stays with the signer"
      description="Customer-entered data, OCR output and AI assistance are supporting sources only. You must review every source and personally choose the certification outcome." />
    <Card className="mt-4" title="Your current authority"><Descriptions column={1}>
      <Descriptions.Item label="Role">Chartered Accountant / Financial Professional</Descriptions.Item>
      <Descriptions.Item label="Consent-verified assignments">{scoped.length}</Descriptions.Item>
    </Descriptions></Card>
    <Card className="mt-4" title="Assigned reviews" extra={<Button type="link" onClick={() => { void refresh(); }} disabled={loading || busy}>Refresh</Button>}>
      {loading && <Spin aria-label="Loading assigned financial reviews" />}
      {notice && <Alert className="mb-4" showIcon type={notice.includes('recorded') ? 'success' : 'warning'} title={notice} />}
      {!loading && scoped.length === 0 && <Empty description="No consent-verified financial reviews are assigned to you." />}
      <List dataSource={scoped} renderItem={item => <List.Item actions={[<Button key="open" onClick={() => { setSelected(item.reviewId); }}>Open review</Button>]}>
        <List.Item.Meta title={item.reviewReference} description={`Assigned ${new Date(item.assignedAt).toLocaleDateString()}`} />
        <Tag color={item.state === 'Certified' ? 'success' : item.state === 'Under review' ? 'processing' : 'default'}>{item.state}</Tag>
      </List.Item>} />
    </Card>

    {current && <Card className="mt-4" title={current.reviewReference} extra={<Tag>{current.dataDurability}</Tag>}>
      <Title level={4}>Assigned source record</Title>
      <List dataSource={current.sources} renderItem={source => <List.Item actions={!source.reviewed && !current.certification
        ? [<Button key="review" disabled={busy || source.assignedVersion !== source.currentVersion} onClick={() => { void mutate(
            'financial-professional/reviews/' + encodeURIComponent(current.reviewId) + '/sources/' + encodeURIComponent(source.code) + '/review',
            {}, `${source.label} was marked professionally reviewed.`); }}>Mark reviewed</Button>] : []}>
        <List.Item.Meta title={source.label} description={`${source.owner} · ${source.reference} · version ${String(source.currentVersion)}`} />
        <Tag color={source.assignedVersion !== source.currentVersion ? 'error' : source.reviewed ? 'success' : 'processing'}>
          {source.assignedVersion !== source.currentVersion ? 'Version changed' : source.reviewed ? 'Reviewed' : 'Ready'}
        </Tag>
      </List.Item>} />

      <Title level={4} className="mt-4">Professional notes</Title>
      {current.notes.length > 0 && <List dataSource={current.notes} renderItem={item => <List.Item>
        <List.Item.Meta title={item.note} description={`Sources: ${item.sourceCodes.join(', ')} · ${new Date(item.recordedAt).toLocaleString()}`} />
      </List.Item>} />}
      {!current.certification && <Form layout="vertical" onFinish={() => { void addNote(); }}>
        <Form.Item label="Link note to sources" required><Checkbox.Group value={noteSources}
          options={current.sources.map(source => ({ label: source.label, value: source.code }))}
          onChange={values => { setNoteSources(values); }} /></Form.Item>
        <Form.Item label="Professional note" required><Input.TextArea aria-label="Professional note" value={note} rows={3} maxLength={2000}
          onChange={event => { setNote(event.target.value); }} /></Form.Item>
        <Button htmlType="submit" disabled={busy || note.trim().length < 3 || noteSources.length === 0}>Record source-linked note</Button>
      </Form>}

      {current.certification ? <Alert className="mt-4" showIcon type="success" title={current.certification.outcome}
        description={<><Text>{current.certification.rationale}</Text><br /><Text type="secondary">Signed by {current.certification.certifiedBy} on {new Date(current.certification.certifiedAt).toLocaleString()} with {current.certification.sources.length} immutable source snapshots.</Text></>} />
      : <Form className="mt-4" layout="vertical" onFinish={() => { void certify(); }}>
        <Title level={4}>Record professional certification</Title>
        <Form.Item label="Outcome"><Radio.Group value={outcome} onChange={event => { setOutcome(event.target.value as Outcome); }}>
          <Space orientation="vertical"><Radio value="Certified with no exceptions">Certified with no exceptions</Radio>
            <Radio value="Certified with exceptions">Certified with exceptions</Radio><Radio value="Unable to certify">Unable to certify</Radio></Space>
        </Radio.Group></Form.Item>
        <Form.Item label="Professional rationale" required><Input.TextArea aria-label="Professional rationale" value={rationale} maxLength={2000} rows={4}
          onChange={event => { setRationale(event.target.value); }} /></Form.Item>
        <Button htmlType="submit" type="primary" loading={busy} disabled={busy || rationale.trim().length < 3 ||
          current.sources.some(source => !source.reviewed || source.assignedVersion !== source.currentVersion)}>Record certification</Button>
        {current.sources.some(source => !source.reviewed || source.assignedVersion !== source.currentVersion) &&
          <Paragraph type="secondary" className="mt-2">Review every assigned source at its current recorded version before certifying.</Paragraph>}
      </Form>}
    </Card>}
  </main>;
}

export default function FinancialProfessionalWorkspace() {
  return <WorkspaceSessionProvider area="financial-professional"><WorkspaceEntry area="financial-professional"><Workspace /></WorkspaceEntry></WorkspaceSessionProvider>;
}
