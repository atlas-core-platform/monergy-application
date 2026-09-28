import { Button, Card, Descriptions, Modal, Space, Tag, Typography } from 'antd';
import { lazy, Suspense, useState } from 'react';

const Vs02Experience = lazy(() => import('./vs02/Vs02Experience'));
const SearchExperience = lazy(() => import('./search/SearchExperience'));
const { Paragraph, Text, Title } = Typography;

function ToolchainFoundation() {
  const [detailsOpen, setDetailsOpen] = useState(false);

  return (
    <main className="min-h-screen bg-canvas px-4 py-8 font-sans text-ink sm:px-8 lg:px-12">
      <div className="mx-auto grid max-w-6xl gap-6 lg:grid-cols-[minmax(0,1.35fr)_minmax(20rem,0.65fr)]">
        <section
          aria-labelledby="shell-title"
          className="rounded-panel bg-surface p-6 shadow-panel sm:p-8"
        >
          <Tag color="blue">CONTROLLED IMPLEMENTATION · TOOLCHAIN EVIDENCE</Tag>
          <Title id="shell-title" level={1} className="mt-4">
            Monergy frontend foundation
          </Title>
          <Paragraph className="max-w-2xl text-base text-muted">
            This shell verifies the accepted frontend toolchain. The first bounded business
            implementation is available separately as provider-neutral VS-02 evidence.
          </Paragraph>

          <Space className="mt-8" wrap>
            <Button
              type="primary"
              size="large"
              onClick={() => {
                setDetailsOpen(true);
              }}
            >
              View toolchain evidence
            </Button>
            <Button size="large" href="/vs02">
              Open VS-02 reference flow
            </Button>
            <Button size="large" href="/search">
              Open authorized search
            </Button>
            <Text keyboard>Build {__BUILD_REVISION__.slice(0, 12)}</Text>
          </Space>
        </section>

        <aside aria-labelledby="status-title">
          <Card className="h-full" title={<span id="status-title">Foundation status</span>}>
            <Descriptions column={1} size="small">
              <Descriptions.Item label="Runtime">React 19.3 · Vite 8.3</Descriptions.Item>
              <Descriptions.Item label="Components">Ant Design 6.6.3</Descriptions.Item>
              <Descriptions.Item label="Composition">Tailwind CSS 4.3.3</Descriptions.Item>
              <Descriptions.Item label="State">
                <Tag color="success">TOOLCHAIN READY</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="VS-02">
                <Tag color="success">ACCEPTED · SIMULATOR</Tag>
              </Descriptions.Item>
            </Descriptions>
          </Card>
        </aside>
      </div>

      <Modal
        title="D02 frontend evidence"
        open={detailsOpen}
        destroyOnHidden
        onCancel={() => {
          setDetailsOpen(false);
        }}
        footer={
          <Button
            type="primary"
            onClick={() => {
              setDetailsOpen(false);
            }}
          >
            Close evidence
          </Button>
        }
      >
        <p>
          Ant Design owns this dialog interaction. Tailwind owns the responsive shell layout and
          spacing. Both consume the same governed semantic-token source.
        </p>
      </Modal>
    </main>
  );
}

export function App() {
  if (window.location.pathname === '/search') {
    return (
      <Suspense
        fallback={
          <main className="grid min-h-screen place-items-center bg-canvas p-6" aria-busy="true">
            <Text>Loading search…</Text>
          </main>
        }
      >
        <SearchExperience />
      </Suspense>
    );
  }

  if (window.location.pathname === '/vs02') {
    return (
      <Suspense
        fallback={
          <main className="grid min-h-screen place-items-center bg-canvas p-6" aria-busy="true">
            <Text>Loading VS-02…</Text>
          </main>
        }
      >
        <Vs02Experience />
      </Suspense>
    );
  }

  return <ToolchainFoundation />;
}
