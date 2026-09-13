import {
  Alert,
  Button,
  Card,
  Descriptions,
  Empty,
  Form,
  Input,
  Space,
  Statistic,
  Table,
  Tag,
  Typography,
} from 'antd';
import { useRef, useState } from 'react';

import { ReferenceRunError, runReferenceFlow } from './referenceApi';
import type {
  AuthoritativeFinancialFact,
  ExtractedObservation,
  ReferenceRunRequest,
  ReferenceRunResult,
} from './types';

const { Paragraph, Text, Title } = Typography;

const observationColumns = [
  { title: 'Observation', dataIndex: 'label', key: 'label' },
  {
    title: 'Candidate value',
    key: 'value',
    render: (_value: unknown, row: ExtractedObservation) =>
      `${row.currency} ${row.candidateValue.toLocaleString()}`,
  },
  {
    title: 'Confidence',
    dataIndex: 'confidence',
    key: 'confidence',
    render: (value: number) => `${String(Math.round(value * 100))}%`,
  },
  { title: 'Evidence location', dataIndex: 'sourceLocation', key: 'sourceLocation' },
];

const factColumns = [
  { title: 'Authoritative fact', dataIndex: 'label', key: 'label' },
  {
    title: 'Normalized value',
    key: 'value',
    render: (_value: unknown, row: AuthoritativeFinancialFact) =>
      `${row.currency} ${row.value.toLocaleString()}`,
  },
  { title: 'Revision', dataIndex: 'revision', key: 'revision' },
  { title: 'Provenance', dataIndex: 'financialProvenanceId', key: 'provenance' },
];

export default function Vs02Experience() {
  const [result, setResult] = useState<ReferenceRunResult>();
  const [failure, setFailure] = useState<ReferenceRunError>();
  const [running, setRunning] = useState(false);
  const controller = useRef<AbortController | undefined>(undefined);

  async function submit(values: ReferenceRunRequest) {
    controller.current?.abort();
    controller.current = new AbortController();
    setRunning(true);
    setFailure(undefined);
    try {
      setResult(await runReferenceFlow(values, controller.current.signal));
    } catch (error) {
      if (error instanceof ReferenceRunError) {
        setFailure(error);
      } else if (!(error instanceof DOMException && error.name === 'AbortError')) {
        setFailure(
          new ReferenceRunError('The reference flow could not be reached.', {
            code: 'reference.unavailable',
            message: 'The reference flow could not be reached.',
            retryable: true,
          }),
        );
      }
    } finally {
      setRunning(false);
    }
  }

  return (
    <main className="min-h-screen bg-canvas px-4 py-6 font-sans text-ink sm:px-8 lg:px-12">
      <div className="mx-auto max-w-7xl">
        <header className="mb-6 flex flex-col justify-between gap-4 sm:flex-row sm:items-start">
          <div>
            <Space wrap>
              <Tag color="gold">REFERENCE · LOCAL / CI ONLY</Tag>
              <Tag color="processing">VS-02 IMPLEMENTATION CANDIDATE</Tag>
            </Space>
            <Title level={1} className="mt-3">
              Evidence to financial truth
            </Title>
            <Paragraph className="max-w-3xl text-base text-muted">
              Exercise the provider-neutral evidence, extraction, validation, normalization,
              provenance, job, and audit path. Results are simulator evidence—not production
              provider evidence.
            </Paragraph>
          </div>
          <Button href="/">Back to foundation</Button>
        </header>

        <Alert
          type="warning"
          showIcon
          className="mb-6"
          title="Reference-adapter boundary"
          description="This route is intentionally restricted to LOCAL and CI / EPHEMERAL execution zones. It does not establish provider sandbox, production compatibility, UAT, or Production readiness."
        />

        <div className="grid gap-6 lg:grid-cols-[minmax(18rem,0.7fr)_minmax(0,1.3fr)]">
          <Card title="Register evidence reference">
            <Form<ReferenceRunRequest>
              layout="vertical"
              requiredMark="optional"
              onFinish={(values) => void submit(values)}
              initialValues={{
                customerId: 'customer-reference-001',
                sourceName: 'Statement fixture',
                originalFileName: 'statement-reference.txt',
                contentReference: 'fixture://vs02/statement-001',
              }}
            >
              <Form.Item
                label="Customer ID"
                name="customerId"
                rules={[{ required: true, whitespace: true, message: 'Enter a customer ID.' }]}
              >
                <Input autoComplete="off" />
              </Form.Item>
              <Form.Item
                label="Source name"
                name="sourceName"
                rules={[{ required: true, whitespace: true, message: 'Enter a source name.' }]}
              >
                <Input />
              </Form.Item>
              <Form.Item
                label="Original file name"
                name="originalFileName"
                rules={[
                  { required: true, whitespace: true, message: 'Enter a safe file name.' },
                  { pattern: /^[^/\\]+$/, message: 'File name must not contain path separators.' },
                ]}
              >
                <Input />
              </Form.Item>
              <Form.Item
                label="Content reference"
                name="contentReference"
                extra="Only a governed reference is sent; evidence bytes are not exposed in this UI."
                rules={[
                  { required: true, whitespace: true, message: 'Enter a content reference.' },
                ]}
              >
                <Input />
              </Form.Item>
              <Button type="primary" htmlType="submit" loading={running} block>
                Run reference flow
              </Button>
            </Form>
          </Card>

          <section aria-labelledby="results-title" aria-busy={running}>
            <Title id="results-title" level={2} className="sr-only">
              Reference flow results
            </Title>
            <div role="status" aria-live="polite" className="sr-only">
              {running && 'Reference flow is running.'}
              {result && !running && 'Reference flow completed.'}
              {failure && !running && `Reference flow failed: ${failure.failure.message}`}
            </div>

            {failure && (
              <Alert
                type="error"
                showIcon
                className="mb-6"
                title="Reference flow failed"
                description={`${failure.failure.code}: ${failure.failure.message} ${failure.failure.retryable ? 'The operation may be retried.' : 'The operation must be corrected before retry.'}`}
              />
            )}

            {!result ? (
              <Card>
                <Empty
                  description={
                    <span>
                      {running
                        ? 'The five-boundary reference flow is running…'
                        : 'Run the reference flow to inspect observations, authoritative facts, provenance, and audit evidence.'}
                    </span>
                  }
                />
              </Card>
            ) : (
              <Space orientation="vertical" size="large" className="w-full">
                <Card title="Governed execution result">
                  <div className="grid gap-4 sm:grid-cols-3">
                    <Statistic title="Processing" value={result.processingState} />
                    <Statistic title="Validated observations" value={result.observations.length} />
                    <Statistic title="Audit records" value={result.auditRecordCount} />
                  </div>
                  <Descriptions className="mt-5" size="small" column={1} bordered>
                    <Descriptions.Item label="Evidence">{result.evidenceId}</Descriptions.Item>
                    <Descriptions.Item label="Document version">
                      {result.documentVersionId}
                    </Descriptions.Item>
                    <Descriptions.Item label="Correlation">
                      {result.correlationId}
                    </Descriptions.Item>
                  </Descriptions>
                </Card>

                <Card
                  title="Extracted observations"
                  extra={<Tag>NOT AUTHORITATIVE FINANCIAL TRUTH</Tag>}
                >
                  <Text type="secondary">
                    Candidate values retain their evidence location and extraction confidence.
                  </Text>
                  <Table
                    className="mt-4"
                    rowKey="sourceFactId"
                    columns={observationColumns}
                    dataSource={result.observations}
                    pagination={false}
                    scroll={{ x: true }}
                  />
                </Card>

                <Card
                  title="Normalized financial facts"
                  extra={<Tag color="green">AUTHORITATIVE</Tag>}
                >
                  <Text type="secondary">
                    Financial Profile owns these normalized facts and their immutable provenance.
                  </Text>
                  <Table
                    className="mt-4"
                    rowKey="financialFactId"
                    columns={factColumns}
                    dataSource={result.financialFacts}
                    pagination={false}
                    scroll={{ x: true }}
                  />
                </Card>
              </Space>
            )}
          </section>
        </div>
      </div>
    </main>
  );
}
