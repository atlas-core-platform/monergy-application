import { Alert, Button, Card, Empty, Form, Input, InputNumber, List, Select, Space, Tag, Typography } from 'antd';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import { WorkspaceEntry } from '../access/WorkspaceEntry';
import { WorkspaceSessionProvider } from '../access/WorkspaceSessionProvider';
import type { PropertyValuation } from './propertyValuationModels';
import { operationId, propertyValuation, propertyValuationPage } from './propertyValuationModels';

const { Paragraph, Text, Title } = Typography;
interface ValuationForm {
  marketValue: number; currency: string; effectiveDate: string; basis: string; findings: string;
}

function Workspace() {
  const { status, session, api, authorization } = useWorkspaceSession();
  const valuer = authorization?.area === 'property-valuer' ? authorization : null;
  const [items, setItems] = useState<PropertyValuation[]>([]);
  const [failure, setFailure] = useState('');
  const [busy, setBusy] = useState('');
  const request = useRef<AbortController | null>(null);
  const [form] = Form.useForm();
  const current = items[0] ?? null;

  const load = useCallback(async () => {
    request.current?.abort();
    if (status !== 'authorized' || !session || !api || !valuer) { setItems([]); return; }
    const pending = new AbortController(); request.current = pending; setFailure('');
    try {
      const value = propertyValuationPage(await api.case<unknown>('property-valuer/valuations', 'GET', undefined, pending.signal),
        session.tenantId, session.actorId);
      if (!pending.signal.aborted) setItems(value.items);
    } catch (error) {
      if (!pending.signal.aborted) setFailure(error instanceof AccessApiError && error.status === 403
        ? 'Your Property Valuer authority, assignment or customer sharing is no longer current.'
        : 'Property valuation assignments are unavailable. Try again.');
    }
  }, [status, session, api, valuer]);
  useEffect(() => {
    const start = window.setTimeout(() => { void load(); }, 0);
    return () => { window.clearTimeout(start); request.current?.abort(); };
  }, [load]);
  const replace = (value: unknown) => {
    setItems(previous => previous.map(item =>
      item.valuationId === (value as { valuationId?: string }).valuationId ? propertyValuation(value) : item));
  };
  const mutate = async (path: string, body: object, key: string) => {
    if (!api) return false; setBusy(key); setFailure('');
    try { replace(await api.case<unknown>(path, 'POST', body)); return true; }
    catch (error) { setFailure(error instanceof AccessApiError && error.status === 409
      ? 'The assignment, evidence or authority changed. Refresh before continuing.'
      : 'The governed valuation action could not be recorded.'); return false; }
    finally { setBusy(''); }
  };
  const record = async (values: ValuationForm) => {
    if (!current) return;
    const recorded = await mutate('property-valuer/valuations/' + encodeURIComponent(current.valuationId) + '/revisions', {
      operationId: operationId(), expectedVersion: current.version, marketValue: values.marketValue,
      currency: values.currency, effectiveDate: values.effectiveDate,
      basis: values.basis, findings: values.findings,
    }, 'valuation');
    if (recorded) form.resetFields();
  };
  const latest = useMemo(() => current?.revisions.at(-1) ?? null, [current]);
  if (!current) return <main className="mx-auto w-full max-w-6xl p-6" aria-labelledby="property-valuer-title">
    <Title level={2} id="property-valuer-title">Property Valuer workspace</Title>
    <Alert type="info" title="Professional valuation remains separate" description="Customer-reported property values and Monergy calculations are not professional valuations. Only an explicitly assigned Property Valuer may record a conclusion here." />
    {failure ? <Alert className="mt-4" type="error" title={failure} action={<Button onClick={() => void load()}>Retry</Button>} /> : <Empty className="mt-8" description="No property valuation is assigned to you." />}
  </main>;
  return <main className="mx-auto w-full max-w-6xl p-6" aria-labelledby="property-valuer-title">
    <Title level={2} id="property-valuer-title">Property Valuer workspace</Title>
    <Alert type="info" title="Professional valuation remains separate" description="Customer-reported property values and Monergy calculations are supporting information only. This result is owned by the assigned Property Valuer and bound to exact source versions." />
    {failure && <Alert className="mt-4" type="error" title={failure} action={<Button onClick={() => void load()}>Refresh</Button>} />}
    <Card className="mt-5" title={current.propertyLabel} extra={<Tag>{current.state}</Tag>}>
      <Paragraph><Text strong>Assigned property:</Text> {current.propertyReference}</Paragraph>
      <Paragraph type="secondary">Assignment {current.valuationId} · version {String(current.version)} · {current.dataDurability}</Paragraph>
      <List dataSource={current.evidence} renderItem={evidence => <List.Item actions={[evidence.inspected
        ? <Tag key="done" color="success">Inspected</Tag>
        : <Button key="inspect" loading={busy === evidence.code} onClick={() => void mutate(
            'property-valuer/valuations/' + encodeURIComponent(current.valuationId) + '/evidence/' + encodeURIComponent(evidence.code) + '/inspection',
            { operationId: operationId(), expectedVersion: current.version }, evidence.code)}>Record inspection</Button>] }>
        <List.Item.Meta title={evidence.label} description={`${evidence.owner} · ${evidence.reference} · source version ${String(evidence.currentVersion)}`} />
      </List.Item>} />
      <Form form={form} layout="vertical" className="mt-6" onFinish={(values: ValuationForm) => { void record(values); }}>
        <Space wrap align="start">
          <Form.Item label="Professional market value" name="marketValue" rules={[{ required: true }]}><InputNumber min={0.01} precision={2} /></Form.Item>
          <Form.Item label="Currency" name="currency" initialValue="USD" rules={[{ required: true }]}><Select options={['USD', 'INR', 'SAR', 'AED'].map(value => ({ value }))} /></Form.Item>
          <Form.Item label="Effective date" name="effectiveDate" rules={[{ required: true }]}><Input type="date" /></Form.Item>
        </Space>
        <Form.Item label="Valuation basis" name="basis" rules={[{ required: true, min: 3 }]}><Input maxLength={500} /></Form.Item>
        <Form.Item label="Professional findings" name="findings" rules={[{ required: true, min: 3 }]}><Input.TextArea maxLength={2000} rows={4} /></Form.Item>
        <Button type="primary" htmlType="submit" loading={busy === 'valuation'} disabled={current.evidence.some(value => !value.inspected)}>
          {latest ? 'Record revised valuation' : 'Record valuation'}
        </Button>
      </Form>
    </Card>
    <List className="mt-5" header={<Title level={3}>Valuation history</Title>} dataSource={[...current.revisions].reverse()}
      locale={{ emptyText: 'No professional valuation has been recorded.' }} renderItem={revision => <List.Item>
        <List.Item.Meta title={`Revision ${String(revision.revision)} · ${revision.currency} ${revision.marketValue.toLocaleString()}`}
          description={`${revision.effectiveDate} · ${revision.basis} · ${revision.findings} · ${String(revision.evidence.length)} exact evidence versions`} />
      </List.Item>} />
  </main>;
}

export default function PropertyValuerWorkspace() {
  return <WorkspaceSessionProvider area="property-valuer"><WorkspaceEntry area="property-valuer"><Workspace /></WorkspaceEntry></WorkspaceSessionProvider>;
}
