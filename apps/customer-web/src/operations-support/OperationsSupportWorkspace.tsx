import { Alert } from 'antd';
import { ProcessingStatus } from '../access/ProcessingStatus';
import { useWorkspaceSession } from '../access/workspaceSession';

export default function OperationsSupportWorkspace() {
  const { authorization, api, session } = useWorkspaceSession();
  if (!api || !session || authorization?.area !== 'operations-support') return null;
  return <>
    <Alert showIcon type="info" title="Bounded operational authority"
      description="You may inspect safe job metadata and request an eligible retry or cancellation. Customer finances, documents, Consent, access grants and professional decisions remain outside this workspace." />
    <ProcessingStatus api={api} tenantId={session.tenantId} actorId={session.actorId}
      audience="operations-support" />
  </>;
}
