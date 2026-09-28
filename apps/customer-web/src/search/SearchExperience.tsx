import {
  Alert,
  Button,
  Card,
  Descriptions,
  Empty,
  Form,
  Input,
  Radio,
  Space,
  Switch,
  Tag,
  Typography,
} from 'antd';
import { useState } from 'react';
import { ReferenceSearchError, searchReference } from './referenceSearchApi';
import type { SearchKind, SearchResults } from './types';
const { Paragraph, Text, Title } = Typography;
export default function SearchExperience() {
  const [kind, setKind] = useState<SearchKind>('documents');
  const [semantic, setSemantic] = useState(false);
  const [result, setResult] = useState<SearchResults | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  async function submit(values: { query: string }) {
    setLoading(true);
    setFailure(null);
    setResult(null);
    try {
      setResult(
        await searchReference(kind, values.query.trim(), semantic ? 'Semantic' : 'Lexical'),
      );
    } catch (error) {
      setFailure(
        error instanceof ReferenceSearchError
          ? `${error.failure.code}: ${error.message}`
          : 'Search failed safely.',
      );
    } finally {
      setLoading(false);
    }
  }
  return (
    <main className="min-h-screen bg-canvas px-4 py-6 font-sans text-ink sm:px-8 lg:px-12">
      <div className="mx-auto max-w-6xl space-y-6">
        <header className="rounded-panel bg-surface p-6 shadow-panel sm:p-8">
          <Space wrap>
            <Tag color="blue">REFERENCE · LOCAL / CI ONLY</Tag>
            <Tag color="purple">MWP-03-D07 · SIMULATOR</Tag>
          </Space>
          <Title level={1} className="mt-4">
            Authorized search &amp; provenance
          </Title>
          <Paragraph className="max-w-3xl text-base text-muted">
            Search derived, rebuildable document and financial indexes while preserving
            authoritative source, provenance and calculation-lineage references. Reference data is
            not live customer information.
          </Paragraph>
          <Button href="/">Back to foundation</Button>
        </header>
        <Card title="Search reference indexes">
          <Form
            layout="vertical"
            onFinish={(values: { query: string }) => {
              void submit(values);
            }}
          >
            <Form.Item label="Result family">
              <Radio.Group
                value={kind}
                onChange={(event) => {
                  setKind(event.target.value as SearchKind);
                }}
              >
                <Radio.Button value="documents">Documents &amp; evidence</Radio.Button>
                <Radio.Button value="financial">Financial &amp; calculations</Radio.Button>
              </Radio.Group>
            </Form.Item>
            <Form.Item
              label="Search query"
              name="query"
              rules={[{ required: true, whitespace: true, message: 'Enter a search query.' }]}
            >
              <Input
                placeholder="Try salary, evidence, or savings"
                maxLength={256}
                autoComplete="off"
              />
            </Form.Item>
            <Form.Item label="Semantic reference matching">
              <Switch
                checked={semantic}
                onChange={setSemantic}
                aria-label="Semantic reference matching"
              />
              <Text className="ml-3" type="secondary">
                Deterministic provider-neutral representation
              </Text>
            </Form.Item>
            <Button type="primary" htmlType="submit" loading={loading}>
              Search authorized references
            </Button>
          </Form>
        </Card>
        <section aria-live="polite" aria-busy={loading} aria-label="Search results">
          {loading && (
            <Card>
              <Text>Searching the authorized derived index…</Text>
            </Card>
          )}
          {failure && (
            <Alert type="error" showIcon title="Search failed safely" description={failure} />
          )}
          {result?.items.length === 0 && (
            <Card>
              <Empty description="No authorized results matched this query." />
            </Card>
          )}
          {result && result.items.length > 0 && (
            <Space orientation="vertical" size="middle" className="w-full">
              <Text role="status">
                {result.items.length} authorized result{result.items.length === 1 ? '' : 's'} ·{' '}
                {result.matchMode}
              </Text>
              {result.items.map((item) => (
                <Card key={item.searchRecordId} title={item.title}>
                  <Space wrap>
                    <Tag
                      color={
                        item.resultType === 'Document'
                          ? 'blue'
                          : item.resultType === 'Financial'
                            ? 'green'
                            : 'gold'
                      }
                    >
                      {item.resultType}
                    </Tag>
                    <Tag>DERIVED · REBUILDABLE</Tag>
                    <Text strong>{item.authoritativeOwner}</Text>
                  </Space>
                  <Paragraph className="mt-4">{item.summary}</Paragraph>
                  <Descriptions size="small" column={{ xs: 1, md: 2 }} bordered>
                    <Descriptions.Item label="Source type">
                      {item.source.sourceObjectType}
                    </Descriptions.Item>
                    <Descriptions.Item label="Source object">
                      {item.source.sourceObjectId}
                    </Descriptions.Item>
                    <Descriptions.Item label="Source reference">
                      {item.source.sourceReferenceId}
                    </Descriptions.Item>
                    <Descriptions.Item label="Provenance">
                      {item.source.provenanceReferenceId ?? 'Not applicable'}
                    </Descriptions.Item>
                    <Descriptions.Item label="Calculation lineage">
                      {item.source.calculationLineageReferenceId ?? 'Not applicable'}
                    </Descriptions.Item>
                  </Descriptions>
                </Card>
              ))}
            </Space>
          )}
          {!loading && !failure && !result && (
            <Card>
              <Empty description="Enter a query to inspect authorized source references." />
            </Card>
          )}
        </section>
      </div>
    </main>
  );
}
