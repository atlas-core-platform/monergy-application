import {
  Alert,
  Button,
  Descriptions,
  Drawer,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Spin,
  Switch,
  Tabs,
} from 'antd';
import { useEffect, useState } from 'react';

import { AccessApiError, displayError, recordId } from './accessApi';
import type {
  AccessApi,
  AccessArea,
  AccessPage,
  AccessRecord,
  Capability,
  Grant,
} from './accessApi';

interface Values {
  code: string;
  label: string;
  permissionIds?: string[];
  grants?: Grant[];
  businessRoleId?: string;
  active?: boolean;
  tenantAdmin?: boolean;
  actorId?: string;
  capabilityId?: string;
  resourceId?: string;
  actorIds?: string[];
}
interface Props {
  area: AccessArea;
  record?: AccessRecord;
  api: AccessApi;
  policyVersion: number;
  onClose: () => void;
  onSaved: (message: string) => void;
  onExpired: (failure: unknown) => void;
}

export function AccessEditor({
  area,
  record,
  api,
  policyVersion,
  onClose,
  onSaved,
  onExpired,
}: Props) {
  const [form] = Form.useForm<Values>();
  const [permissions, setPermissions] = useState<AccessRecord[]>([]);
  const [roles, setRoles] = useState<AccessRecord[]>([]);
  const [people, setPeople] = useState<AccessRecord[]>([]);
  const [capabilities, setCapabilities] = useState<Capability[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [mode, setMode] = useState('details');
  const [confirmation, setConfirmation] = useState<{ values?: Values; remove?: boolean } | null>(
    null,
  );
  const id = record ? recordId(record) : '';
  const noun =
    area === 'members'
      ? 'person'
      : area === 'resource-grants'
        ? 'resource access'
        : area.slice(0, -1);

  useEffect(() => {
    const controller = new AbortController();
    const load = async () => {
      try {
        const [permissionList, roleList, peopleList, catalog] = await Promise.all([
          area === 'roles' ? api.all('permissions', controller.signal) : Promise.resolve([]),
          area === 'members' ? api.all('roles', controller.signal) : Promise.resolve([]),
          area === 'groups' || area === 'resource-grants'
            ? api.all('members', controller.signal)
            : Promise.resolve([]),
          area === 'permissions' || area === 'resource-grants'
            ? api.request<{ capabilities: Capability[] }>(
                'capabilities',
                'GET',
                undefined,
                controller.signal,
              )
            : Promise.resolve({ capabilities: [] }),
        ]);
        const actorIds: string[] = [];
        if (area === 'groups' && id) {
          let cursor: string | undefined;
          do {
            const group = await api.request<AccessPage>(
              'administration/groups/' +
                encodeURIComponent(id) +
                '/members?limit=100' +
                (cursor ? '&after=' + encodeURIComponent(cursor) : ''),
              'GET',
              undefined,
              controller.signal,
            );
            if (group.policyVersion !== policyVersion)
              throw new AccessApiError(409, 'POLICY_VERSION_CONFLICT');
            for (const person of group.items) if (person.actorId) actorIds.push(person.actorId);
            cursor = group.nextCursor ?? undefined;
          } while (cursor && actorIds.length <= 1000);
          if (cursor) throw new AccessApiError(400, 'SELECTION_TOO_LARGE');
        }
        if (controller.signal.aborted) return;
        setPermissions(permissionList);
        setRoles(roleList);
        setPeople(peopleList);
        setCapabilities(
          catalog.capabilities.filter((capability) => capability.lifecycle === 'Active'),
        );
        form.setFieldsValue({
          code: record?.code ?? '',
          label: record?.label ?? '',
          permissionIds: record?.permissionIds ?? [],
          grants: record?.grants ?? [],
          businessRoleId: record?.businessRoleId ?? undefined,
          active: record?.status === 'Active',
          tenantAdmin: record?.tenantAdmin ?? false,
          actorIds,
        });
        setLoading(false);
      } catch (failure) {
        if (!controller.signal.aborted) {
          setError(displayError(failure));
          if (
            failure instanceof AccessApiError &&
            (failure.status === 401 || failure.status === 403)
          )
            onExpired(failure);
        }
      }
    };
    void load();
    return () => {
      controller.abort();
    };
  }, [api, area, form, id, onExpired, policyVersion, record]);

  const save = async () => {
    if (!confirmation) return;
    setBusy(true);
    setError('');
    try {
      const values = confirmation.values;
      const suffix = id ? '/' + encodeURIComponent(id) : '';
      if (confirmation.remove)
        await api.request('administration/' + area + suffix, 'DELETE', {
          expectedPolicyVersion: policyVersion,
        });
      else if (values) {
        let body: unknown;
        let path = 'administration/' + area + suffix;
        if (area === 'members')
          body = {
            expectedPolicyVersion: policyVersion,
            businessRoleId: values.businessRoleId ?? null,
            active: Boolean(values.active),
            tenantAdmin: Boolean(values.tenantAdmin),
          };
        else if (area === 'roles')
          body = {
            expectedPolicyVersion: policyVersion,
            code: values.code,
            label: values.label,
            permissionIds: values.permissionIds ?? [],
          };
        else if (area === 'permissions')
          body = {
            expectedPolicyVersion: policyVersion,
            code: values.code,
            label: values.label,
            grants: values.grants ?? [],
          };
        else if (area === 'groups' && mode === 'people') {
          body = { expectedPolicyVersion: policyVersion, actorIds: values.actorIds ?? [] };
          path += '/members';
        } else if (area === 'groups')
          body = { expectedPolicyVersion: policyVersion, code: values.code, label: values.label };
        else
          body = {
            expectedPolicyVersion: policyVersion,
            actorId: values.actorId,
            capabilityId: values.capabilityId,
            resourceType: capabilities.find(
              (capability) => capability.capabilityId === values.capabilityId,
            )?.resourceType,
            resourceId: values.resourceId,
          };
        await api.request(path, id ? 'PUT' : 'POST', body);
      }
      onSaved(
        confirmation.remove ? 'Access configuration removed.' : 'Your access change was saved.',
      );
    } catch (failure) {
      setError(displayError(failure));
      if (failure instanceof AccessApiError && (failure.status === 401 || failure.status === 403))
        onExpired(failure);
    } finally {
      setBusy(false);
      setConfirmation(null);
    }
  };
  const labeledOptions = (records: AccessRecord[]) =>
    records
      .filter((item) => item.id)
      .map((item) => ({ value: item.id ?? '', label: item.label ?? item.code ?? item.id }));
  const names = (
    <>
      <Form.Item name="label" label="Name" rules={[{ required: true, whitespace: true, max: 120 }]}>
        <Input
          placeholder={
            area === 'roles'
              ? 'For example, Financial analyst'
              : area === 'groups'
                ? 'For example, Advisory team'
                : 'For example, Read financial information'
          }
        />
      </Form.Item>
      <Form.Item
        name="code"
        label="Code"
        tooltip="Use this stable code in CSV imports."
        rules={[
          { required: true },
          {
            pattern: /^[A-Za-z][A-Za-z0-9_-]{0,79}$/,
            message: 'Start with a letter. Use letters, numbers, hyphens or underscores.',
          },
        ]}
      >
        <Input placeholder="For example, financial-analyst" />
      </Form.Item>
    </>
  );
  return (
    <>
      <Drawer
        title={
          record ? (record.normalizedEmail ?? record.label ?? 'Resource access') : `Create ${noun}`
        }
        open
        onClose={() => {
          if (!busy) onClose();
        }}
        size={560}
        destroyOnHidden
        mask={{ closable: !busy }}
        keyboard={!busy}
        footer={
          <div className="access-drawer-footer">
            <div>
              {record && area !== 'members' && (
                <Button
                  danger
                  onClick={() => {
                    setConfirmation({ remove: true });
                  }}
                  disabled={busy || loading}
                >
                  Remove
                </Button>
              )}
            </div>
            <Space>
              <Button onClick={onClose} disabled={busy}>
                Close
              </Button>
              {!(area === 'resource-grants' && record) && (
                <Button
                  type="primary"
                  onClick={() => {
                    form.submit();
                  }}
                  disabled={loading}
                  loading={busy}
                >
                  Review change
                </Button>
              )}
            </Space>
          </div>
        }
      >
        <p className="access-drawer-intro">
          {area === 'members'
            ? 'Manage this person’s role and administrative authority. Every change is verified by the service.'
            : area === 'groups'
              ? 'Groups organize people. They do not grant roles or business access.'
              : 'Make access clear and intentional. Review your change before saving.'}
        </p>
        {error && <Alert type="error" showIcon title={error} className="access-notice" />}
        {loading && !error ? (
          <Spin />
        ) : area === 'resource-grants' && record ? (
          <Descriptions
            column={1}
            items={[
              { key: 'person', label: 'Person', children: record.actorId },
              {
                key: 'capability',
                label: 'Capability',
                children:
                  capabilities.find((item) => item.capabilityId === record.capabilityId)
                    ?.displayName ?? record.capabilityId,
              },
              { key: 'resource', label: 'Resource', children: record.resourceId },
            ]}
          />
        ) : (
          <Form
            form={form}
            layout="vertical"
            onFinish={(values) => {
              setConfirmation({ values });
            }}
            disabled={busy || loading}
          >
            {area === 'groups' && record && (
              <Tabs
                activeKey={mode}
                onChange={setMode}
                items={[
                  { key: 'details', label: 'Group details' },
                  { key: 'people', label: 'People in this group' },
                ]}
              />
            )}
            {(area === 'roles' ||
              area === 'permissions' ||
              (area === 'groups' && mode === 'details')) &&
              names}
            {area === 'roles' && (
              <Form.Item name="permissionIds" label="Permissions">
                <Select
                  mode="multiple"
                  showSearch={{ optionFilterProp: 'label' }}
                  options={labeledOptions(permissions)}
                  placeholder="Choose permissions"
                />
              </Form.Item>
            )}
            {area === 'permissions' && (
              <Form.List name="grants">
                {(fields, { add, remove }) => (
                  <>
                    <h3 className="access-form-section">Capabilities</h3>
                    <p className="access-form-hint">
                      Tenant-wide access applies within this tenant. Resource-specific access also
                      requires an individual resource grant.
                    </p>
                    {fields.map((field) => (
                      <div className="access-grant-row" key={field.key}>
                        <Form.Item
                          name={[field.name, 'capabilityId']}
                          label={`Capability ${String(field.name + 1)}`}
                          rules={[{ required: true }]}
                        >
                          <Select
                            showSearch={{ optionFilterProp: 'label' }}
                            options={Array.from(
                              new Set(capabilities.map((item) => item.service)),
                            ).map((service) => ({
                              label: service.replaceAll('-', ' '),
                              options: capabilities
                                .filter((item) => item.service === service)
                                .map((item) => ({
                                  value: item.capabilityId,
                                  label: item.displayName,
                                })),
                            }))}
                          />
                        </Form.Item>
                        <Form.Item
                          name={[field.name, 'scope']}
                          label="Scope"
                          rules={[{ required: true }]}
                        >
                          <Select
                            options={[
                              { value: 'Tenant', label: 'Tenant-wide' },
                              { value: 'Resource', label: 'Specific resources' },
                            ]}
                          />
                        </Form.Item>
                        <Button
                          type="text"
                          danger
                          onClick={() => {
                            remove(field.name);
                          }}
                          aria-label={`Remove capability ${String(field.name + 1)}`}
                        >
                          Remove
                        </Button>
                      </div>
                    ))}
                    <Button
                      onClick={() => {
                        add({ scope: 'Tenant' });
                      }}
                      disabled={fields.length >= 1000}
                    >
                      Add capability
                    </Button>
                  </>
                )}
              </Form.List>
            )}
            {area === 'members' && (
              <>
                <Form.Item name="businessRoleId" label="Business role">
                  <Select
                    allowClear
                    showSearch={{ optionFilterProp: 'label' }}
                    options={labeledOptions(roles)}
                    placeholder="No business role"
                  />
                </Form.Item>
                <Alert
                  type="info"
                  showIcon
                  title="Changing a role removes existing individual resource grants."
                  className="access-notice"
                />
                <Form.Item name="active" label="Active membership" valuePropName="checked">
                  <Switch />
                </Form.Item>
                <Form.Item name="tenantAdmin" label="Tenant administrator" valuePropName="checked">
                  <Switch />
                </Form.Item>
                <p className="access-form-hint">
                  Administrative authority does not grant access to financial or other business
                  data. Disabling access invalidates the person’s existing sessions.
                </p>
              </>
            )}
            {area === 'groups' && mode === 'people' && (
              <Form.Item name="actorIds" label="People" rules={[{ type: 'array', max: 1000 }]}>
                <Select
                  mode="multiple"
                  showSearch={{ optionFilterProp: 'label' }}
                  options={people.map((person) => ({
                    value: person.actorId ?? '',
                    label: person.normalizedEmail ?? person.actorId,
                  }))}
                  placeholder="Choose people"
                />
              </Form.Item>
            )}
            {area === 'resource-grants' && !record && (
              <>
                <Form.Item name="actorId" label="Person" rules={[{ required: true }]}>
                  <Select
                    showSearch={{ optionFilterProp: 'label' }}
                    options={people
                      .filter((person) => person.status === 'Active')
                      .map((person) => ({
                        value: person.actorId ?? '',
                        label: person.normalizedEmail ?? person.actorId,
                      }))}
                  />
                </Form.Item>
                <Form.Item name="capabilityId" label="Capability" rules={[{ required: true }]}>
                  <Select
                    showSearch={{ optionFilterProp: 'label' }}
                    options={capabilities
                      .filter((capability) => capability.resourceType)
                      .map((capability) => ({
                        value: capability.capabilityId,
                        label: capability.displayName,
                      }))}
                  />
                </Form.Item>
                <Form.Item
                  name="resourceId"
                  label="Resource identifier"
                  rules={[
                    { required: true },
                    {
                      pattern: /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,199}$/,
                      message: 'Enter the exact resource identifier.',
                    },
                  ]}
                >
                  <Input />
                </Form.Item>
                <p className="access-form-hint">
                  The person’s role must already include this capability with resource-specific
                  scope.
                </p>
              </>
            )}
          </Form>
        )}
      </Drawer>
      <Modal
        title={
          confirmation?.remove ? 'Remove this access configuration?' : 'Save this access change?'
        }
        open={confirmation !== null}
        onCancel={() => {
          if (!busy) setConfirmation(null);
        }}
        onOk={() => {
          void save();
        }}
        confirmLoading={busy}
        okText={confirmation?.remove ? 'Remove' : 'Save change'}
        okButtonProps={{ danger: confirmation?.remove }}
        cancelButtonProps={{ disabled: busy }}
        closable={!busy}
        mask={{ closable: !busy }}
      >
        <p>
          {confirmation?.remove
            ? 'The service will reject removal if another access configuration still depends on this item.'
            : 'This change takes effect immediately. Current administrator authority and the workspace revision will be checked again.'}
        </p>
        {area === 'members' && (
          <p>
            <strong>{record?.normalizedEmail}</strong>
            <br />
            Role:{' '}
            {roles.find((role) => role.id === confirmation?.values?.businessRoleId)?.label ??
              'No business role'}
            <br />
            Membership: {confirmation?.values?.active ? 'Active' : 'Disabled'}
            <br />
            Tenant administrator: {confirmation?.values?.tenantAdmin ? 'Yes' : 'No'}
          </p>
        )}
      </Modal>
    </>
  );
}
