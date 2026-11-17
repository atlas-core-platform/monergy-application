import { Alert } from 'antd';
import { ProcessingStatus } from '../access/ProcessingStatus';
import { useWorkspaceSession } from '../access/workspaceSession';

export default function OperationsSupportWorkspace() {
  const { authorization, api, session } = useWorkspaceSession();
  if (!api || !session || authorization?.area !== 'operations-support') return null;
  return <>
    <Alert showIcon type="info" title="Operations"
      description="Review tasks that need attention and safely try again or cancel eligible work. Customer financial information and professional decisions are not shown here." />
    <ProcessingStatus api={api} tenantId={session.tenantId} actorId={session.actorId}
      audience="operations-support" />
  </>;
}
