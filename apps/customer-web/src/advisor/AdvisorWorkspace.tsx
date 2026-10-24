import { Alert, Card, Descriptions, Typography } from 'antd';
import { useWorkspaceSession } from '../access/workspaceSession';

const { Title, Paragraph } = Typography;

// Workspace entry only. This screen never accepts a customer ID or displays
// financial data; subsequent adviser features require owner-approved access.
export default function AdvisorWorkspace() {
  const { authorization } = useWorkspaceSession();
  if (authorization?.area !== 'advisor') return null;
  return (
    <main className="mx-auto w-full max-w-5xl p-6" aria-labelledby="advisor-title">
      <Title level={2} id="advisor-title">Financial Advisor workspace</Title>
      <Paragraph>Review your assigned work within the permissions granted by your organization and your clients.</Paragraph>
      <Card title="Your access">
        <Descriptions column={1}>
          <Descriptions.Item label="Role">Financial Advisor / Relationship Manager</Descriptions.Item>
          <Descriptions.Item label="Recorded client relationships">{authorization.recordedRelationshipCount}</Descriptions.Item>
        </Descriptions>
        <Alert
          type="info"
          showIcon
          title="Customer information is protected"
          description="A recorded relationship is not permission to view a customer's finances. Access requires an approved business capability, a current assignment and the customer's active consent. No customer financial information is exposed on this page."
        />
      </Card>
    </main>
  );
}
