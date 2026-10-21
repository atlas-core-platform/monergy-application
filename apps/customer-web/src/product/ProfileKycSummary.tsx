import { Alert, Button, Card, Descriptions, Skeleton, Space, Tag } from 'antd';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { kycStates, validKycView } from './basicKycApi';
import type { KycView } from './basicKycApi';

// This summary reads the KYC owner; profile declarations never determine KYC state.
export default function ProfileKycSummary({ api, tenant, actor, customer }: { api: AccessApi; tenant: string; actor: string; customer: string }) {
  const [view, setView] = useState<{ api: AccessApi; scope: string; value: KycView | null; failed: boolean } | null>(null);
  const scope = JSON.stringify([tenant, actor, customer]);
  const generation = useRef(0); const pending = useRef<AbortController | null>(null);
  const current = view?.api === api && view.scope === scope ? view : null;
  const load = useCallback(async () => {
    pending.current?.abort(); const controller = new AbortController(); pending.current = controller;
    const id = ++generation.current; setView(null);
    try {
      const value = await api.basicKyc<unknown>('/me', 'GET', undefined, controller.signal);
      if (id !== generation.current || controller.signal.aborted) return;
      if (!validKycView(value, tenant, customer) || value.current && value.current.ownerActorId !== actor) throw new Error('KYC_BINDING_INVALID');
      setView({ api, scope, value, failed: false });
    } catch {
      if (id === generation.current && !controller.signal.aborted) setView({ api, scope, value: null, failed: true });
    }
  }, [api, scope, tenant, actor, customer]);
  useEffect(() => { const start = window.setTimeout(() => { void load(); }, 0); return () => { window.clearTimeout(start); generation.current += 1; pending.current?.abort(); }; }, [load]);
  const item = current?.value?.current;
  return <Card title="Basic KYC" extra={<Button disabled={!current} onClick={() => { void load(); }}>Refresh KYC status</Button>}>
    {!current ? <Skeleton active paragraph={{ rows: 2 }} /> : current.failed ? <Alert type="warning" showIcon title="KYC status could not be verified. Refresh to try again." /> : <>
      <Tag>{item ? kycStates[item.state] : 'Not submitted'}</Tag>
      {item && <Descriptions column={1} items={[
        { key: 'reviewer', label: 'Assigned reviewer', children: item.reviewerName },
        { key: 'updated', label: 'Status recorded', children: new Date(item.recordedAt).toLocaleString() },
        { key: 'reason', label: 'Latest note', children: item.reason },
      ]} />}
      {!current.value?.policy && <p>Your organization has not enabled new KYC submissions. Contact your organization administrator.</p>}
      {item?.state === 'SUBMITTED' && current.value?.policy && item.policyRevision !== current.value.policy.revision && <Alert type="info" title="The review notice has changed. Open Basic KYC to withdraw and resubmit under the current notice." />}
    </>}
    <p>Manual review acceptance is separate from government or provider identity verification. Your profile details and PAN remain self-declared.</p>
    <Space wrap><a href="/kyc">Open Basic KYC and review history</a></Space>
  </Card>;
}
