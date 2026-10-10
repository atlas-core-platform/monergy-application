import {
  Alert,
  Button,
  Descriptions,
  Drawer,
  Empty,
  Form,
  Input,
  Select,
  Space,
  Table,
  Tabs,
  Tag,
} from 'antd';
import { containDialogTab } from '@monergy/ui-foundation';
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { AccessApiError, displayError } from './accessApi';
import type { AccessApi, AccessPage, AccessRecord, ResourceDirectoryItem } from './accessApi';
import { useOnboarding } from './onboarding';

export function CustomerRelationships({ api, legacy }: { api: AccessApi; legacy: ReactNode }) {
  const { customerRelationships } = useOnboarding();
  return customerRelationships ? (
    <Tabs
      defaultActiveKey="customers"
      items={[
        {
          key: 'customers',
          label: 'Assigned customers',
          children: <AssignedCustomers api={api} />,
        },
        {
          key: 'advanced',
          label: 'Advanced resource grants',
          children: (
            <>
              <Alert
                showIcon
                type="info"
                className="access-notice"
                title="Exact resource grants"
                description="These grants remain available for contracts that explicitly support them. They do not replace customer assignments or Consent in customer workflows."
              />
              {legacy}
            </>
          ),
        },
      ]}
    />
  ) : (
    legacy
  );
}

function AssignedCustomers({ api }: { api: AccessApi }) {
  const [data, setData] = useState<{
    page: AccessPage;
    people: AccessRecord[];
    customers: ResourceDirectoryItem[];
  } | null>(null);
  const [revision, setRevision] = useState(0);
  const [query, setQuery] = useState('');
  const [draft, setDraft] = useState<{ actorId?: string; customerId?: string; id?: string } | null>(
    null,
  );
  const [review, setReview] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [blocked, setBlocked] = useState(false);
  const [form] = Form.useForm<{ actorId: string; customerId: string }>();
  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      const page = await api.request<AccessPage>(
        'administration/customer-relationships?limit=100',
        'GET',
        undefined,
        controller.signal,
      );
      const people = await api.all('members', controller.signal, page.policyVersion);
      const directory = await api.resourceDirectory('customer', controller.signal);
      if (!controller.signal.aborted) {
        setData({ page, people, customers: directory.items });
        setError('');
      }
    })().catch((failure: unknown) => {
      if (!controller.signal.aborted) setError(displayError(failure));
    });
    return () => {
      controller.abort();
    };
  }, [api, revision]);
  const customerName = (id?: string) =>
    data?.customers.find((value) => value.resourceId === id)?.displayName ?? 'Customer unavailable';
  const personName = (id?: string) =>
    data?.people.find((value) => value.actorId === id)?.normalizedEmail ?? 'User unavailable';
  const refresh = () => {
    setDraft(null);
    setReview(false);
    setData(null);
    setBlocked(false);
    setError('');
    setRevision((value) => value + 1);
  };
  const more = async () => {
    if (!data?.page.nextCursor) return;
    setBusy(true);
    try {
      const page = await api.request<AccessPage>(
        'administration/customer-relationships?limit=100&after=' +
          encodeURIComponent(data.page.nextCursor),
      );
      if (page.policyVersion !== data.page.policyVersion)
        throw new AccessApiError(409, 'POLICY_VERSION_CONFLICT');
      setData({ ...data, page: { ...page, items: [...data.page.items, ...page.items] } });
    } catch (failure) {
      setError(displayError(failure));
    } finally {
      setBusy(false);
    }
  };
  const save = async () => {
    if (!data || !draft || busy || blocked) return;
    setBusy(true);
    setError('');
    try {
      await api.request(
        'administration/customer-relationships' +
          (draft.id ? '/' + encodeURIComponent(draft.id) : ''),
        draft.id ? 'DELETE' : 'POST',
        draft.id
          ? { expectedPolicyVersion: data.page.policyVersion }
          : {
              expectedPolicyVersion: data.page.policyVersion,
              actorId: draft.actorId,
              customerId: draft.customerId,
            },
      );
      setNotice(
        draft.id
          ? 'Customer assignment removed. This relationship no longer authorizes access.'
          : 'Customer assigned. Access still requires role permissions and current customer Consent.',
      );
      refresh();
    } catch (failure) {
      setError(displayError(failure));
      setBlocked(true);
    } finally {
      setBusy(false);
    }
  };
  const rows =
    data?.page.items.filter((row) =>
      (customerName(row.customerId) + ' ' + personName(row.actorId))
        .toLowerCase()
        .includes(query.toLowerCase()),
    ) ?? [];
  return (
    <>
      <header className="access-page-header">
        <div>
          <p className="workspace-eyebrow">RESOURCE ACCESS</p>
          <h1>Assigned customers</h1>
          <p>
            Connect advisors to named customers. Their role determines what they can do; customer
            Consent determines what they may use.
          </p>
        </div>
        <Button
          type="primary"
          disabled={!data || busy}
          onClick={() => {
            form.resetFields();
            setDraft({});
            setReview(false);
            setBlocked(false);
            setError('');
          }}
        >
          Assign customer
        </Button>
      </header>
      <Alert
        type="info"
        showIcon
        className="access-notice"
        title="One relationship, consistent customer boundaries"
        description="An assignment applies to supported customer workflows. Verified customers receive self-access only where their role permits it. Neither administrator authority nor a group grants financial access."
      />
      {notice && <Alert type="success" showIcon title={notice} className="access-notice" />}
      {error && !draft && (
        <Alert
          type="error"
          showIcon
          title={error}
          action={<Button onClick={refresh}>Refresh assignments</Button>}
          className="access-notice"
        />
      )}
      <section className="access-table-panel" aria-label="Assigned customers">
        <Input.Search
          enterButton="Search"
          aria-label="Search loaded assignments"
          placeholder="Find a customer or advisor"
          value={query}
          onChange={(event) => {
            setQuery(event.target.value);
          }}
          allowClear
        />
        <Table<AccessRecord>
          size="small"
          rowKey={(row) => row.id ?? ''}
          loading={!data && !error}
          dataSource={rows}
          pagination={false}
          locale={{
            emptyText: (
              <Empty
                description={
                  query
                    ? 'No assignments match your search.'
                    : 'No customers assigned yet. Assign a customer to an advisor with a business role.'
                }
              />
            ),
          }}
          columns={[
            {
              title: 'Customer',
              key: 'customer',
              render: (_, row) => <strong>{customerName(row.customerId)}</strong>,
            },
            { title: 'Advisor', key: 'advisor', render: (_, row) => personName(row.actorId) },
            {
              title: 'Access conditions',
              key: 'scope',
              render: () => <Tag>Role permissions + Consent</Tag>,
            },
            {
              title: 'Action',
              key: 'action',
              render: (_, row) => (
                <Button
                  type="text"
                  disabled={
                    !data?.customers.some((customer) => customer.resourceId === row.customerId) ||
                    !data.people.some((person) => person.actorId === row.actorId)
                  }
                  onClick={() => {
                    setDraft({ id: row.id, actorId: row.actorId, customerId: row.customerId });
                    setReview(true);
                    setBlocked(false);
                    setError('');
                  }}
                >
                  Review removal
                  <span className="sr-only">
                    {' '}
                    for {customerName(row.customerId)} and {personName(row.actorId)}
                  </span>
                </Button>
              ),
            },
          ]}
        />
        {data?.page.nextCursor && (
          <Button
            loading={busy}
            onClick={() => {
              void more();
            }}
          >
            Load more assignments
          </Button>
        )}
      </section>
      <Drawer
        title={
          draft?.id
            ? 'Review customer removal'
            : review
              ? 'Review customer assignment'
              : 'Assign customer'
        }
        open={Boolean(draft)}
        size={480}
        onKeyDown={containDialogTab}
        onClose={() => {
          if (!busy) setDraft(null);
        }}
        mask={{ closable: !busy }}
        keyboard={!busy}
        destroyOnHidden
        footer={
          <Space>
            <Button
              disabled={busy}
              onClick={() => {
                if (blocked) refresh();
                else if (review && !draft?.id) setReview(false);
                else setDraft(null);
              }}
            >
              {blocked ? 'Reload current assignments' : review && !draft?.id ? 'Back' : 'Cancel'}
            </Button>
            <Button
              type="primary"
              danger={Boolean(draft?.id)}
              loading={busy}
              disabled={blocked || !data}
              onClick={() => {
                if (review) void save();
                else form.submit();
              }}
            >
              {review ? (draft?.id ? 'Remove assignment' : 'Assign customer') : 'Review assignment'}
            </Button>
          </Space>
        }
      >
        {error && (
          <Alert
            type="error"
            showIcon
            title={error}
            description="Reload current assignments before retrying. This request will not be repeated automatically."
            className="access-notice"
          />
        )}
        {review ? (
          <section aria-label="Review customer access">
            <Descriptions
              column={1}
              items={[
                { key: 'customer', label: 'Customer', children: customerName(draft?.customerId) },
                { key: 'advisor', label: 'Advisor', children: personName(draft?.actorId) },
                {
                  key: 'change',
                  label: 'Change',
                  children: draft?.id
                    ? 'Remove customer relationship'
                    : 'Assign customer relationship',
                },
              ]}
            />
            <Alert
              type="warning"
              showIcon
              title="Effect on access"
              description={
                draft?.id
                  ? 'Removal takes effect immediately and invalidates this advisor’s existing sessions. This customer relationship will no longer authorize access.'
                  : 'The advisor must sign in again. This assignment does not grant new capabilities or supply Consent on the customer’s behalf.'
              }
            />
          </section>
        ) : (
          <Form
            form={form}
            layout="vertical"
            onFinish={(values) => {
              setDraft(values);
              setReview(true);
            }}
          >
            <Form.Item
              name="actorId"
              label="Advisor"
              rules={[{ required: true, message: 'Choose an advisor.' }]}
            >
              <Select
                aria-label="Advisor"
                showSearch={{ optionFilterProp: 'label' }}
                placeholder="Choose an active user with a business role"
                options={data?.people
                  .filter((person) => person.status === 'Active' && person.businessRoleId)
                  .map((person) => ({ value: person.actorId, label: person.normalizedEmail }))}
              />
            </Form.Item>
            <Form.Item
              name="customerId"
              label="Customer"
              rules={[{ required: true, message: 'Choose a customer.' }]}
            >
              <Select
                aria-label="Customer"
                showSearch={{ optionFilterProp: 'label' }}
                placeholder="Find a customer by name"
                options={data?.customers.map((customer) => ({
                  value: customer.resourceId,
                  label:
                    customer.displayName +
                    (customer.secondaryLabel ? ' · ' + customer.secondaryLabel : ''),
                }))}
              />
            </Form.Item>
            <p className="access-form-hint">
              The service checks that the selected user has a customer-related capability. Customer
              self-access follows verified ownership and does not need a manual assignment.
            </p>
          </Form>
        )}
      </Drawer>
    </>
  );
}
