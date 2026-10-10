import { containDialogTab } from '@monergy/ui-foundation';
import {
  Alert,
  Button,
  Descriptions,
  Drawer,
  Form,
  Input,
  Collapse,
  Select,
  Space,
  Spin,
  Switch,
  Tabs,
} from 'antd';
import { useEffect, useRef, useState } from 'react';
import { AccessChangeReview } from './AccessChangeReview';
import type { ReviewValue } from './AccessChangeReview';

import { AccessApiError, displayError, recordId } from './accessApi';
import type {
  AccessApi,
  AccessArea,
  AccessPage,
  AccessRecord,
  Capability,
  Grant,
  ResourceDirectoryItem,
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
  onClose: (reload?: boolean) => void;
  onSaved: (message: string) => void;
  onExpired: (failure: unknown) => void;
  onReload: () => Promise<void>;
}

export function AccessEditor({
  area,
  record,
  api,
  policyVersion,
  onClose,
  onSaved,
  onExpired,
  onReload,
}: Props) {
  const [open, setOpen] = useState(true);
  const [form] = Form.useForm<Values>();
  const selectedPermissions = Form.useWatch('permissionIds', form);
  const selectedRole = Form.useWatch('businessRoleId', form);
  const selectedCapability = Form.useWatch('capabilityId', form);
  const [resourceGrants, setResourceGrants] = useState<AccessRecord[]>([]);
  const [resourceLookup, setResourceLookup] = useState<{
    type: string;
    items: ResourceDirectoryItem[];
    error: string;
  } | null>(null);
  const [resolvedResourceNames, setResolvedResourceNames] = useState<Record<string, string>>({});
  const [permissions, setPermissions] = useState<AccessRecord[]>([]);
  const [roles, setRoles] = useState<AccessRecord[]>([]);
  const [people, setPeople] = useState<AccessRecord[]>([]);
  const [capabilities, setCapabilities] = useState<Capability[]>([]);
  const resourceType =
    capabilities.find((item) => item.capabilityId === selectedCapability)?.resourceType ??
    record?.resourceType;
  const matchingLookup = resourceLookup?.type === resourceType ? resourceLookup : null;
  const resourceOptions = matchingLookup?.items ?? [];
  const resourceLookupLoading = Boolean(resourceType && !matchingLookup);
  const resourceLookupError = matchingLookup?.error ?? '';
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [recovery, setRecovery] = useState<'conflict' | 'uncertain' | null>(null);
  const reviewHeading = useRef<HTMLHeadingElement>(null);
  const reviewButton = useRef<HTMLButtonElement>(null);
  const [mode, setMode] = useState('details');
  const [original, setOriginal] = useState<Values | null>(null);
  const [confirmation, setConfirmation] = useState<{ values?: Values; remove?: boolean } | null>(
    null,
  );
  useEffect(() => {
    if (confirmation) reviewHeading.current?.focus();
  }, [confirmation]);
  const id = record ? recordId(record) : '';
  const reload = async () => {
    setBusy(true);
    try {
      await onReload();
    } catch (failure) {
      setError(displayError(failure));
      onExpired(failure);
    } finally {
      setBusy(false);
    }
  };
  const close = () => {
    if (busy) return;
    setOpen(false);
  };
  const noun =
    area === 'members'
      ? 'user'
      : area === 'resource-grants'
        ? 'resource access'
        : area.slice(0, -1);

  useEffect(() => {
    const controller = new AbortController();
    const load = async () => {
      try {
        const [permissionList, roleList, peopleList, catalog, resources] = await Promise.all([
          area === 'roles' || area === 'members'
            ? api.all('permissions', controller.signal, policyVersion)
            : Promise.resolve([]),
          area === 'members'
            ? api.all('roles', controller.signal, policyVersion)
            : Promise.resolve([]),
          area === 'groups' || area === 'resource-grants'
            ? api.all('members', controller.signal, policyVersion)
            : Promise.resolve([]),
          area === 'permissions' ||
          area === 'resource-grants' ||
          area === 'members' ||
          area === 'roles'
            ? api.request<{ capabilities: Capability[] }>(
                'capabilities',
                'GET',
                undefined,
                controller.signal,
              )
            : Promise.resolve({ capabilities: [] }),
          area === 'members'
            ? api.all('resource-grants', controller.signal, policyVersion)
            : Promise.resolve([]),
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
        setResourceGrants(resources.filter((grant) => grant.actorId === record?.actorId));
        setPermissions(permissionList);
        setRoles(roleList);
        setPeople(peopleList);
        setCapabilities(
          catalog.capabilities.filter((capability) => capability.lifecycle === 'Active'),
        );
        const initial: Values = {
          code: record?.code ?? '',
          label: record?.label ?? '',
          permissionIds: record?.permissionIds ?? [],
          grants: record?.grants ?? [],
          businessRoleId: record?.businessRoleId ?? undefined,
          active: record?.status === 'Active',
          tenantAdmin: record?.tenantAdmin ?? false,
          actorIds,
        };
        form.setFieldsValue(initial);
        setOriginal(initial);
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

  useEffect(() => {
    if (area !== 'resource-grants' || !resourceType) return;
    const controller = new AbortController();
    void api
      .resourceDirectory(resourceType, controller.signal)
      .then((directory) => {
        if (!controller.signal.aborted) {
          setResourceLookup({ type: resourceType, items: directory.items, error: '' });
          setResolvedResourceNames((current) => ({
            ...current,
            ...Object.fromEntries(
              directory.items.map((item) => [item.resourceId, item.displayName]),
            ),
          }));
        }
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) {
          setResourceLookup({
            type: resourceType,
            items: [],
            error:
              failure instanceof AccessApiError && failure.code === 'RESOURCE_DIRECTORY_UNAVAILABLE'
                ? 'This resource type does not yet have an owner-backed directory.'
                : displayError(failure),
          });
        }
      });
    return () => {
      controller.abort();
    };
  }, [api, area, resourceType]);

  useEffect(() => {
    if (area !== 'members' || resourceGrants.length === 0) return;
    const controller = new AbortController();
    const resourceTypes = [
      ...new Set(resourceGrants.map((grant) => grant.resourceType).filter(Boolean)),
    ] as string[];
    void Promise.all(
      resourceTypes.map(async (resourceType) => {
        try {
          return (await api.resourceDirectory(resourceType, controller.signal)).items;
        } catch {
          return [];
        }
      }),
    ).then((groups) => {
      if (!controller.signal.aborted)
        setResolvedResourceNames(
          Object.fromEntries(groups.flat().map((item) => [item.resourceId, item.displayName])),
        );
    });
    return () => {
      controller.abort();
    };
  }, [api, area, resourceGrants]);

  const resourceName = (resourceId?: string) =>
    (resourceId ? resolvedResourceNames[resourceId] : undefined) ??
    resourceOptions.find((item) => item.resourceId === resourceId)?.displayName ??
    (resourceId ? 'Restricted resource' : 'None');

  const save = async () => {
    if (!confirmation || recovery || busy) return;
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
      if (failure instanceof AccessApiError && failure.status === 409) setRecovery('conflict');
      else if (!(failure instanceof AccessApiError) || failure.status >= 500)
        setRecovery('uncertain');
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
  const lookup = (records: AccessRecord[], value?: string) =>
    records.find((item) => (item.id ?? item.actorId) === value)?.label ??
    records.find((item) => (item.id ?? item.actorId) === value)?.normalizedEmail ??
    value ??
    'None';
  const capabilityName = (value?: string) =>
    capabilities.find((item) => item.capabilityId === value)?.displayName ?? value ?? 'None';
  const list = (items: string[]) => (items.length ? [...items].sort().join('\n') : 'None');
  const describe = (values: Values): ReviewValue[] => {
    if (area === 'members')
      return [
        {
          label: 'Business role',
          value: lookup(roles, values.businessRoleId),
          identity: values.businessRoleId ?? '',
        },
        { label: 'Account status', value: values.active ? 'Active' : 'Disabled' },
        { label: 'Tenant administrator', value: values.tenantAdmin ? 'Yes' : 'No' },
      ];
    if (area === 'groups' && mode === 'people')
      return [
        {
          label: 'People',
          identity: list(values.actorIds ?? []),
          value: list((values.actorIds ?? []).map((value) => lookup(people, value))),
        },
      ];
    if (area === 'resource-grants')
      return [
        { label: 'Person', value: lookup(people, values.actorId) },
        { label: 'Capability', value: capabilityName(values.capabilityId) },
        {
          label: 'Resource',
          value: resourceName(values.resourceId),
          identity: values.resourceId ?? '',
        },
      ];
    return [
      { label: 'Name', value: values.label },
      { label: 'Code', value: values.code },
      ...(area === 'roles'
        ? [
            {
              label: 'Permissions',
              identity: list(values.permissionIds ?? []),
              value: list((values.permissionIds ?? []).map((value) => lookup(permissions, value))),
            },
          ]
        : []),
      ...(area === 'permissions'
        ? [
            {
              label: 'Capabilities and scope',
              identity: list(
                (values.grants ?? []).map((grant) => `${grant.capabilityId}:${grant.scope}`),
              ),
              value: list(
                (values.grants ?? []).map(
                  (grant) =>
                    `${capabilityName(grant.capabilityId)} · ${grant.scope === 'Tenant' ? 'Tenant-wide' : 'Specific resources'}`,
                ),
              ),
            },
          ]
        : []),
    ];
  };
  const before = original
    ? describe(original).map((item) =>
        area === 'members' && item.label === 'Account status' && record?.status
          ? {
              ...item,
              value: record.status === 'PendingIdentity' ? 'Pending identity' : record.status,
            }
          : item,
      )
    : [];
  const proposed = confirmation?.values ? describe(confirmation.values) : [];
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
  const configuredPermissions = (
    <Collapse
      className="access-assignment-summary"
      items={[
        {
          key: 'configured',
          label: 'Review configured permissions',
          children: (
            <>
              <p className="access-form-hint">
                This describes the selected role’s configuration. Actual access is checked by each
                service, including user status and resource assignments.
              </p>
              {(area === 'roles'
                ? (selectedPermissions ?? [])
                : (roles.find((role) => role.id === selectedRole)?.permissionIds ?? [])
              ).length ? (
                (area === 'roles'
                  ? (selectedPermissions ?? [])
                  : (roles.find((role) => role.id === selectedRole)?.permissionIds ?? [])
                ).map((permissionId) => {
                  const permission = permissions.find((item) => item.id === permissionId);
                  return (
                    <div className="access-permission-summary" key={permissionId}>
                      <strong>{permission?.label ?? permissionId}</strong>
                      <ul>
                        {permission?.grants?.map((grant) => (
                          <li key={`${grant.capabilityId}:${grant.scope}`}>
                            {capabilityName(grant.capabilityId)} ·{' '}
                            {grant.scope === 'Tenant'
                              ? 'Tenant-wide'
                              : 'Specific resources (requires assignment)'}
                          </li>
                        ))}
                      </ul>
                    </div>
                  );
                })
              ) : (
                <p>No permissions in the selected role.</p>
              )}
              {area === 'members' && (
                <>
                  <h4>Saved resource assignments</h4>
                  {resourceGrants.length ? (
                    <ul>
                      {resourceGrants.map((grant) => (
                        <li key={recordId(grant)}>
                          {capabilityName(grant.capabilityId)} · {resourceName(grant.resourceId)}
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <p>No individual resource assignments.</p>
                  )}
                </>
              )}
            </>
          ),
        },
      ]}
    />
  );
  return (
    <>
      <Drawer
        onKeyDown={(event) => {
          containDialogTab(event);
          // Details remain mounted to preserve the draft; their popup portals must
          // not consume Escape while the review is the active surface.
          if (event.key === 'Escape' && confirmation && !busy) {
            event.stopPropagation();
            setOpen(false);
          }
        }}
        title={
          confirmation
            ? confirmation.remove
              ? 'Remove this access configuration?'
              : record
                ? 'Review changes'
                : `Review new ${noun}`
            : record
              ? area === 'members'
                ? 'Manage user'
                : (record.label ?? 'Resource access')
              : `Create ${noun}`
        }
        open={open}
        onClose={close}
        afterOpenChange={(visible) => {
          if (!visible) onClose(Boolean(recovery));
        }}
        size={480}
        destroyOnHidden
        mask={{ closable: !busy }}
        keyboard={!busy && !confirmation}
        footer={
          <div className="access-drawer-footer">
            <div>
              {record && area !== 'members' && !confirmation && (
                <Button
                  danger
                  onClick={() => {
                    setConfirmation({ remove: true });
                  }}
                  disabled={busy || loading || Boolean(recovery)}
                >
                  Remove
                </Button>
              )}
            </div>
            <Space>
              <Button
                onClick={() => {
                  if (confirmation) {
                    setConfirmation(null);
                    requestAnimationFrame(() => reviewButton.current?.focus());
                  } else if (recovery) void reload();
                  else close();
                }}
                disabled={busy}
              >
                {confirmation ? 'Back' : recovery ? 'Reload current records' : 'Close'}
              </Button>
              {(Boolean(confirmation) || !(area === 'resource-grants' && record)) && (
                <Button
                  type="primary"
                  ref={reviewButton}
                  onClick={() => {
                    if (confirmation) void save();
                    else form.submit();
                  }}
                  disabled={loading || Boolean(recovery)}
                  danger={confirmation?.remove}
                  loading={busy}
                >
                  {confirmation
                    ? confirmation.remove
                      ? 'Remove'
                      : record
                        ? 'Save changes'
                        : `Create ${noun}`
                    : 'Review change'}
                </Button>
              )}
            </Space>
          </div>
        }
      >
        {error && <Alert type="error" showIcon title={error} className="access-notice" />}
        {recovery && (
          <Alert
            type="warning"
            showIcon
            className="access-notice"
            title={
              recovery === 'conflict'
                ? 'Reload before making another change'
                : 'The result has not been confirmed'
            }
            description="Your draft is shown below for reference. Reload reads the current records from the service before returning to the directory. Check the saved values before starting another change. This request will not be repeated."
          />
        )}
        {confirmation && (
          <section aria-label="Review changes">
            <h2 className="access-review-heading" tabIndex={-1} ref={reviewHeading}>
              Confirm {confirmation.remove ? 'removal' : 'access change'}
            </h2>
            <p>
              {confirmation.remove
                ? 'The service will reject removal if another access configuration still depends on this item.'
                : 'Check the details below. Saving applies this change immediately to your tenant.'}
            </p>
            {record && (
              <p className="access-review-subject">
                <span>Affected {noun}</span>
                <strong>
                  {area === 'resource-grants'
                    ? resourceName(record.resourceId)
                    : (record.normalizedEmail ?? record.label ?? id)}
                </strong>
              </p>
            )}
            {proposed.length > 0 && (
              <AccessChangeReview current={record ? before : undefined} proposed={proposed} />
            )}

            {area === 'members' && !confirmation.remove && (
              <Alert
                type="warning"
                showIcon
                title="Effect on this user"
                description={
                  <>
                    <p>Saving changes invalidates this user’s existing sessions.</p>
                    {original?.businessRoleId !== confirmation.values?.businessRoleId && (
                      <p>
                        Changing the business role removes all individual resource grants. Review
                        and reassign any required resources after saving.
                      </p>
                    )}
                    {!confirmation.values?.active && (
                      <p>This user will be unable to access the tenant until reactivated.</p>
                    )}
                    {confirmation.values?.tenantAdmin && (
                      <p>
                        This user will be able to administer users and access across this tenant.
                      </p>
                    )}
                  </>
                }
              />
            )}
          </section>
        )}
        <div hidden={Boolean(confirmation)}>
          <p className="access-drawer-intro">
            {area === 'members'
              ? 'Edit account access, then review the exact changes before saving.'
              : area === 'groups'
                ? 'Groups organize people. They do not grant roles or business access.'
                : 'Make access clear and intentional. Review your change before saving.'}
          </p>

          {loading && !error ? (
            <div role="status" className="access-loading">
              <Spin />
              <span>Loading current configuration…</span>
            </div>
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
                { key: 'resource', label: 'Resource', children: resourceName(record.resourceId) },
              ]}
            />
          ) : (
            <Form
              form={form}
              layout="vertical"
              onFinish={(values) => {
                if (
                  area === 'resource-grants' &&
                  (resourceLookupLoading ||
                    resourceLookupError ||
                    !resourceOptions.some((resource) => resource.resourceId === values.resourceId))
                ) {
                  form.setFields([
                    { name: 'resourceId', errors: ['Choose an available resource by name.'] },
                  ]);
                  return;
                }
                setConfirmation({ values });
              }}
              disabled={busy || loading || Boolean(recovery)}
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
              {area === 'roles' && configuredPermissions}
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
              )}{' '}
              {area === 'members' && (
                <>
                  <section className="access-editor-section" aria-labelledby="user-identity">
                    <h3 id="user-identity">Identity</h3>
                    <dl className="access-identity">
                      <dt>Email address</dt>
                      <dd>{record?.normalizedEmail ?? 'Not available'}</dd>
                      <dt>User reference</dt>
                      <dd>{record?.actorId}</dd>
                    </dl>
                    <p className="access-form-hint">Identity details are read-only here.</p>
                    {record?.status === 'PendingIdentity' && (
                      <p className="access-form-hint">
                        Identity activation is pending. Complete the existing request in{' '}
                        <a href="/access/users/history">Onboarding history</a> before editing
                        access.
                      </p>
                    )}
                  </section>
                  <section className="access-editor-section" aria-labelledby="user-status">
                    <h3 id="user-status">Account status</h3>
                    <Form.Item name="active" label="Active membership" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <p className="access-form-hint">
                      An inactive user cannot access this tenant. Saving a change invalidates
                      existing sessions.
                    </p>
                  </section>
                  <section className="access-editor-section" aria-labelledby="user-role">
                    <h3 id="user-role">Business access</h3>
                    <Form.Item name="businessRoleId" label="Business role">
                      <Select
                        allowClear
                        showSearch={{ optionFilterProp: 'label' }}
                        options={labeledOptions(roles)}
                        placeholder="No business role"
                      />
                    </Form.Item>
                    <p className="access-form-hint">
                      One business role per user. Changing it removes individual resource grants.
                    </p>
                    {configuredPermissions}
                  </section>
                  <section className="access-editor-section" aria-labelledby="user-authority">
                    <h3 id="user-authority">Administrative authority</h3>
                    <Form.Item
                      name="tenantAdmin"
                      label="Tenant administrator"
                      valuePropName="checked"
                    >
                      <Switch />
                    </Form.Item>
                    <p className="access-form-hint">
                      Allows administration of this tenant. Business data still requires a business
                      role and applicable resource grants.
                    </p>
                  </section>
                  <section className="access-editor-section" aria-labelledby="user-groups">
                    <h3 id="user-groups">Group membership</h3>
                    <p className="access-form-hint">
                      Review and manage existing membership in <a href="/access/groups">Groups</a>.
                      Group membership does not grant access.
                    </p>
                  </section>
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
                      onChange={() => {
                        form.setFieldValue('resourceId', undefined);
                      }}
                      options={capabilities
                        .filter((capability) => capability.resourceType)
                        .map((capability) => ({
                          value: capability.capabilityId,
                          label: capability.displayName,
                        }))}
                    />
                  </Form.Item>
                  <Form.Item name="resourceId" label="Resource" rules={[{ required: true }]}>
                    <Select
                      showSearch={{ optionFilterProp: 'label' }}
                      loading={resourceLookupLoading}
                      disabled={
                        !selectedCapability || resourceLookupLoading || Boolean(resourceLookupError)
                      }
                      placeholder="Choose a business resource"
                      options={resourceOptions.map((resource) => ({
                        value: resource.resourceId,
                        label: resource.secondaryLabel
                          ? resource.displayName + ' · ' + resource.secondaryLabel
                          : resource.displayName,
                      }))}
                    />
                  </Form.Item>
                  {resourceLookupError && (
                    <Alert
                      type="warning"
                      showIcon
                      title="Resource lookup is unavailable"
                      description={resourceLookupError}
                      className="access-notice"
                    />
                  )}
                  <p className="access-form-hint">
                    Choose the business resource by name. Monergy stores its immutable identifier
                    automatically. The person’s role must already include this capability with
                    resource-specific scope.
                  </p>
                </>
              )}
            </Form>
          )}
        </div>
      </Drawer>
    </>
  );
}
