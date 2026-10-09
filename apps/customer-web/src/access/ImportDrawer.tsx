import { WorkspaceIcon } from '@monergy/ui-foundation';
import {
  Alert,
  Button,
  Collapse,
  Drawer,
  Input,
  Form,
  Select,
  Progress,
  Space,
  Steps,
  Table,
  Tag,
} from 'antd';
import { useEffect, useRef, useState } from 'react';

import { AccessApiError, displayError } from './accessApi';
import type {
  AccessApi,
  AccessRecord,
  ImportError,
  ImportPreview,
  ImportResult,
} from './accessApi';

interface Props {
  api: AccessApi;
  single?: boolean;
  importId: string | null;
  onClose: () => void;
  onChanged: (message: string) => void;
  onExpired: (failure: unknown) => void;
}
const template = 'email,role_code,group_codes\nperson@example.test,,\n';

export function ImportDrawer({
  api,
  importId,
  single = false,
  onClose,
  onChanged,
  onExpired,
}: Props) {
  const [userForm] = Form.useForm<{ email?: string; role?: string; groups?: string[] }>();
  const [choices, setChoices] = useState<{ roles: AccessRecord[]; groups: AccessRecord[] } | null>(
    null,
  );
  const [csv, setCsv] = useState('');
  const [fileName, setFileName] = useState('');
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [dragging, setDragging] = useState(false);
  const [confirm, setConfirm] = useState<'start' | 'retry' | 'cancel' | null>(null);
  const [operationId, setOperationId] = useState(importId ?? '');
  const input = useRef<HTMLInputElement>(null);
  const report = (failure: unknown) => {
    setError(displayError(failure));
    if (failure instanceof AccessApiError && (failure.status === 401 || failure.status === 403))
      onExpired(failure);
  };
  useEffect(() => {
    if (!importId) return;
    const controller = new AbortController();
    void api
      .request<ImportResult>(
        'administration/imports/' + encodeURIComponent(importId),
        'GET',
        undefined,
        controller.signal,
      )
      .then(setResult)
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) {
          setError(
            failure instanceof AccessApiError && failure.status === 404
              ? 'The service confirms this onboarding request was not staged. Close this review and start a new request.'
              : displayError(failure),
          );
          if (
            failure instanceof AccessApiError &&
            (failure.status === 401 || failure.status === 403)
          )
            onExpired(failure);
        }
      });
    return () => {
      controller.abort();
    };
  }, [api, importId, onExpired]);
  useEffect(() => {
    if (!single) return;
    const abort = new AbortController();
    void Promise.all([api.all('roles', abort.signal), api.all('groups', abort.signal)])
      .then(([roles, groups]) => {
        if (!abort.signal.aborted) setChoices({ roles, groups });
      })
      .catch((failure: unknown) => {
        if (!abort.signal.aborted) {
          setError(displayError(failure));
          onExpired(failure);
        }
      });
    return () => {
      abort.abort();
    };
  }, [api, single, onExpired]);
  const changeCsv = (value: string) => {
    setCsv(value);
    setPreview(null);
    setOperationId('');
    setError('');
  };
  const selectFile = async (file: File) => {
    if (busy || result || operationId) return;
    if (file.size > 240_000) {
      setError('Choose a CSV smaller than 240 KB.');
      return;
    }
    setBusy(true);
    try {
      changeCsv(await file.text());
      setFileName(file.name);
    } catch {
      setError('This file could not be read. Choose it again.');
    } finally {
      setBusy(false);
    }
  };
  const validate = async () => {
    if (single) {
      try {
        await userForm.validateFields();
      } catch {
        return;
      }
    }
    setBusy(true);
    setError('');
    try {
      const checked = await api.request<ImportPreview>('administration/imports/preview', 'POST', {
        csv,
      });
      setPreview(checked);
      if (single && checked.valid) setConfirm('start');
    } catch (failure) {
      report(failure);
    } finally {
      setBusy(false);
    }
  };
  const refresh = async () => {
    if (!operationId) return;
    setBusy(true);
    setError('');
    try {
      setResult(
        await api.request<ImportResult>(
          'administration/imports/' + encodeURIComponent(operationId),
        ),
      );
    } catch (failure) {
      if (failure instanceof AccessApiError && failure.status === 404 && !importId) {
        setOperationId('');
        setPreview(null);
        setError(
          'The service confirms this import was not staged. Validate your file again against the current policy.',
        );
      } else report(failure);
    } finally {
      setBusy(false);
    }
  };
  const run = async () => {
    if (!confirm) return;
    setBusy(true);
    setError('');
    let stagedConfirmed = Boolean(result);
    try {
      let id = operationId;
      let expectedPolicyVersion: number;
      if (confirm === 'start' && !preview?.valid) return;
      if (confirm === 'start' && preview?.valid) {
        id ||= crypto.randomUUID();
        setOperationId(id);
        const staged = await api.request<ImportResult>(
          'administration/imports/' + encodeURIComponent(id) + '/commit',
          'POST',
          { expectedPolicyVersion: preview.policyVersion, csv, contentHash: preview.contentHash },
        );
        setResult(staged);
        stagedConfirmed = true;
        if (staged.status === 'Activated' || staged.status === 'Cancelled') {
          onChanged(
            staged.status === 'Activated'
              ? 'User access was activated.'
              : 'Onboarding was cancelled.',
          );
          return;
        }
        expectedPolicyVersion = staged.policyVersion;
      } else {
        const current = await api.list('members');
        expectedPolicyVersion = current.policyVersion;
      }
      if (!id) return;
      const next = await api.request<ImportResult>(
        'administration/imports/' +
          encodeURIComponent(id) +
          (confirm === 'cancel' ? '/cancel' : '/process'),
        'POST',
        { expectedPolicyVersion },
      );
      setResult(next);
      if (next.status === 'Activated')
        onChanged(
          single
            ? 'The user was created and their access was activated.'
            : `${String(next.rowCount)} users were onboarded together.`,
        );
      else if (next.status === 'Cancelled')
        onChanged('The pending import was cancelled. No access was activated by it.');
    } catch (failure) {
      if (
        !stagedConfirmed &&
        failure instanceof AccessApiError &&
        (failure.code === 'POLICY_VERSION_CONFLICT' || failure.code === 'IMPORT_VALIDATION_FAILED')
      ) {
        setOperationId('');
        setPreview(null);
      }
      report(failure);
    } finally {
      setBusy(false);
      setConfirm(null);
    }
  };
  const downloadTemplate = () => {
    const url = URL.createObjectURL(new Blob([template], { type: 'text/csv;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = 'monergy-people-template.csv';
    link.click();
    URL.revokeObjectURL(url);
  };
  const verified = result?.identities.filter((identity) => identity.actorId !== null).length ?? 0;
  const terminal = result?.status === 'Activated' || result?.status === 'Cancelled';
  const confirmationLabel =
    confirm === 'cancel'
      ? 'Cancel onboarding'
      : confirm === 'retry'
        ? 'Retry onboarding'
        : single
          ? 'Create user'
          : 'Onboard people';
  const preparationLabel = single
    ? 'Review user'
    : preview?.valid
      ? 'Confirm onboarding'
      : 'Validate entire file';
  return (
    <>
      <Drawer
        title={
          confirm
            ? confirm === 'cancel'
              ? 'Cancel onboarding?'
              : confirm === 'retry'
                ? 'Review retry'
                : single
                  ? 'Review new user'
                  : 'Review onboarding'
            : importId
              ? 'Onboarding details'
              : single
                ? 'Create user'
                : 'Import users'
        }
        open
        onClose={() => {
          if (!busy) onClose();
        }}
        size={single ? 480 : 650}
        mask={{ closable: !busy }}
        keyboard={!busy}
        destroyOnHidden
        footer={
          <div className="access-drawer-footer">
            <Button onClick={onClose} disabled={busy}>
              Close
            </Button>
            <Space>
              {confirm ? (
                <>
                  <Button
                    disabled={busy}
                    onClick={() => {
                      setConfirm(null);
                    }}
                  >
                    Back
                  </Button>
                  <Button
                    key="confirm-onboarding"
                    type="primary"
                    aria-label={confirmationLabel}
                    aria-busy={busy}
                    disabled={busy}
                    danger={confirm === 'cancel'}
                    loading={busy}
                    onClick={() => {
                      void run();
                    }}
                  >
                    {confirmationLabel}
                  </Button>
                </>
              ) : (
                <>
                  {operationId && (
                    <Button
                      onClick={() => {
                        void refresh();
                      }}
                      disabled={busy}
                    >
                      Check status
                    </Button>
                  )}
                  {!result && (
                    <Button
                      key="prepare-onboarding"
                      type="primary"
                      aria-label={preparationLabel}
                      aria-busy={busy}
                      disabled={!csv || busy || Boolean(operationId) || (single && !choices)}
                      loading={busy}
                      onClick={() => {
                        if (preview?.valid) setConfirm('start');
                        else void validate();
                      }}
                    >
                      {preparationLabel}
                    </Button>
                  )}
                  {result && !terminal && (
                    <>
                      <Button
                        danger
                        disabled={busy}
                        onClick={() => {
                          setConfirm('cancel');
                        }}
                      >
                        Cancel import
                      </Button>
                      <Button
                        type="primary"
                        loading={busy}
                        onClick={() => {
                          setConfirm('retry');
                        }}
                      >
                        Review and retry
                      </Button>
                    </>
                  )}
                </>
              )}
            </Space>
          </div>
        }
      >
        <Steps
          className="access-import-steps"
          size="small"
          current={result?.status === 'Activated' ? 2 : preview?.valid || result ? 1 : 0}
          items={[
            { title: single ? 'User details' : 'Choose file' },
            { title: 'Review & create' },
            { title: 'Complete' },
          ]}
        />
        {error && <Alert type="error" showIcon title={error} className="access-notice" />}
        {confirm && (
          <section aria-label="Review onboarding">
            <p>
              {confirm === 'cancel'
                ? 'No access will be activated by this request. Any identities already created remain available for future onboarding.'
                : confirm === 'retry'
                  ? 'Existing identity confirmations will be reused. Current role permissions and your authority are checked again before access is activated.'
                  : 'Check the details before continuing. Access is activated only after identity verification succeeds.'}
            </p>
            {single && confirm === 'start' ? (
              <dl className="access-review">
                <div className="access-review-row">
                  <dt>Email</dt>
                  <dd>{userForm.getFieldValue('email')}</dd>
                </div>
                <div className="access-review-row">
                  <dt>Business role</dt>
                  <dd>
                    {choices?.roles.find((role) => role.code === userForm.getFieldValue('role'))
                      ?.label ?? 'None — no business permissions'}
                  </dd>
                </div>
                <div className="access-review-row">
                  <dt>Groups</dt>
                  <dd>
                    {(userForm.getFieldValue('groups') as string[] | undefined)
                      ?.map(
                        (code) =>
                          choices?.groups.find((group) => group.code === code)?.label ?? code,
                      )
                      .join(', ') ?? 'None'}
                  </dd>
                </div>
                <div className="access-review-row">
                  <dt>Tenant administrator</dt>
                  <dd>No</dd>
                </div>
              </dl>
            ) : confirm === 'start' ? (
              <>
                <p>{preview?.rowCount} users will be onboarded with the roles and groups below.</p>
                <pre className="access-csv-review">{csv}</pre>
              </>
            ) : null}
            {confirm === 'start' && (
              <p className="access-form-hint">
                This creates tenant membership and assigned access. It does not send an invitation
                or configure a password or sign-in method.
              </p>
            )}
          </section>
        )}
        <div hidden={confirm !== null}>
          {!result ? (
            <>
              {single ? (
                <>
                  <p className="access-drawer-intro">
                    Create one user and choose their access. A business role is optional; groups
                    only organize users.
                  </p>
                  <Form
                    form={userForm}
                    layout="vertical"
                    disabled={busy || Boolean(operationId) || !choices}
                    onValuesChange={() => {
                      const values = userForm.getFieldsValue();
                      const quote = (value: string) => '"' + value.replaceAll('"', '""') + '"';
                      changeCsv(
                        'email,role_code,group_codes\n' +
                          [
                            values.email?.trim() ?? '',
                            values.role ?? '',
                            (values.groups ?? []).join('|'),
                          ]
                            .map(quote)
                            .join(',') +
                          '\n',
                      );
                    }}
                  >
                    <Form.Item
                      name="email"
                      label="Email address"
                      rules={[
                        { required: true, message: 'Enter an email address.' },
                        { type: 'email', message: 'Enter a valid email address.' },
                      ]}
                    >
                      <Input autoComplete="off" placeholder="person@company.com" maxLength={254} />
                    </Form.Item>
                    <Form.Item
                      name="role"
                      label="Business role"
                      extra="Each user can have one business role. Without one, they have no role-based business access."
                    >
                      <Select
                        allowClear
                        showSearch={{ optionFilterProp: 'label' }}
                        loading={!choices}
                        options={choices?.roles.map((role) => ({
                          value: role.code,
                          label: role.label ?? role.code,
                        }))}
                        placeholder="No business role"
                      />
                    </Form.Item>
                    <Form.Item
                      name="groups"
                      label="Groups"
                      extra="Group membership does not grant permissions."
                    >
                      <Select
                        mode="multiple"
                        showSearch={{ optionFilterProp: 'label' }}
                        options={choices?.groups.map((group) => ({
                          value: group.code,
                          label: group.label ?? group.code,
                        }))}
                        placeholder="Choose groups (optional)"
                      />
                    </Form.Item>
                  </Form>
                </>
              ) : (
                <>
                  <p className="access-drawer-intro">
                    Upload up to 500 people. The entire file must be valid, and every identity must
                    be ready before anyone in the batch gains access.
                  </p>
                  <div
                    className={`access-drop-zone ${dragging ? 'is-dragging' : ''}`}
                    onDragOver={(event) => {
                      event.preventDefault();
                      if (!busy && !operationId) setDragging(true);
                    }}
                    onDragLeave={() => {
                      setDragging(false);
                    }}
                    onDrop={(event) => {
                      event.preventDefault();
                      setDragging(false);
                      const file = event.dataTransfer.files[0];
                      if (file) void selectFile(file);
                    }}
                  >
                    <span className="access-upload-icon">
                      <WorkspaceIcon name="upload" size={29} />
                    </span>
                    <h3>{fileName || 'Drop your CSV here'}</h3>
                    <p>CSV · up to 500 people · maximum 240 KB</p>
                    <Button
                      onClick={() => input.current?.click()}
                      disabled={busy || Boolean(operationId)}
                    >
                      Choose CSV
                    </Button>
                    <input
                      ref={input}
                      type="file"
                      accept=".csv,text/csv"
                      hidden
                      aria-label="Choose CSV file"
                      onChange={(event) => {
                        const file = event.target.files?.[0];
                        if (file) void selectFile(file);
                        event.target.value = '';
                      }}
                    />
                  </div>
                  <div className="access-template-row">
                    <span>Start with the right structure.</span>
                    <Button type="link" onClick={downloadTemplate}>
                      Download template
                    </Button>
                  </div>
                  <Collapse
                    ghost
                    items={[
                      {
                        key: 'csv',
                        label: csv ? 'Review or edit the original CSV' : 'Paste CSV instead',
                        children: (
                          <Input.TextArea
                            aria-label="CSV contents"
                            value={csv}
                            onChange={(event) => {
                              changeCsv(event.target.value);
                              setFileName('');
                            }}
                            rows={8}
                            maxLength={240000}
                            disabled={busy || Boolean(operationId)}
                            placeholder={template}
                          />
                        ),
                      },
                      {
                        key: 'rules',
                        label: 'Import guidelines',
                        children: (
                          <ul className="access-guidelines">
                            <li>Use exactly: email, role_code, group_codes.</li>
                            <li>Leave role_code empty for no business access.</li>
                            <li>Use existing role and group codes. Separate group codes with |.</li>
                            <li>Do not include passwords or credentials.</li>
                            <li>Groups organize people; they do not grant permissions.</li>
                          </ul>
                        ),
                      },
                    ]}
                  />
                </>
              )}
              {preview && (
                <div className="access-validation">
                  <Alert
                    type={preview.valid ? 'success' : 'error'}
                    showIcon
                    title={
                      preview.valid
                        ? single
                          ? 'User details validated'
                          : `${String(preview.rowCount)} users ready for review`
                        : `${String(preview.errors.length)} issues to resolve`
                    }
                    description={
                      preview.valid
                        ? single
                          ? 'No changes have been made. Review the user’s details before creating their account.'
                          : 'No changes have been made. Review the original CSV, then confirm onboarding.'
                        : 'Correct every issue and validate again. No part of this file will be imported while errors remain.'
                    }
                  />
                  {!preview.valid && (
                    <Table<ImportError>
                      size="small"
                      rowKey={(item) => `${String(item.rowNumber)}:${item.field}:${item.code}`}
                      dataSource={preview.errors}
                      pagination={{ pageSize: 10 }}
                      columns={[
                        { title: 'Row', dataIndex: 'rowNumber' },
                        { title: 'Field', dataIndex: 'field' },
                        {
                          title: 'Issue',
                          dataIndex: 'code',
                          render: (value: string) => value.toLowerCase().replaceAll('_', ' '),
                        },
                      ]}
                    />
                  )}
                </div>
              )}
            </>
          ) : (
            <div className="access-import-result">
              <div
                className={`access-result-symbol ${result.status === 'Activated' ? 'is-complete' : ''}`}
              >
                <WorkspaceIcon name={result.status === 'Activated' ? 'check' : 'users'} size={36} />
              </div>
              <Tag
                color={
                  result.status === 'Activated'
                    ? 'success'
                    : result.status === 'Failed'
                      ? 'error'
                      : 'default'
                }
              >
                {result.status}
              </Tag>
              <h2>
                {result.status === 'Activated'
                  ? single
                    ? 'User created.'
                    : 'User access activated.'
                  : result.status === 'Cancelled'
                    ? 'Import cancelled.'
                    : 'Onboarding is not complete.'}
              </h2>
              <p>
                {result.status === 'Activated'
                  ? single
                    ? 'This user’s tenant membership and assigned access are active. Sign-in setup is managed separately.'
                    : 'All users in this batch were activated together.'
                  : 'This batch has not activated any access. Verified identities are retained for safe recovery.'}
              </p>
              <Progress
                percent={Math.round((verified / Math.max(result.rowCount, 1)) * 100)}
                status={
                  result.status === 'Failed'
                    ? 'exception'
                    : result.status === 'Activated'
                      ? 'success'
                      : 'normal'
                }
              />
              <span className="access-form-hint">
                {String(verified)} of {String(result.rowCount)} identities verified
              </span>
              {result.errorCode && (
                <Alert
                  className="access-notice"
                  type="warning"
                  showIcon
                  title={result.errorCode.toLowerCase().replaceAll('_', ' ')}
                />
              )}
              <Table
                size="small"
                rowKey="rowNumber"
                pagination={{ pageSize: 10 }}
                dataSource={result.identities}
                columns={[
                  { title: 'Row', dataIndex: 'rowNumber' },
                  { title: 'Person', dataIndex: 'normalizedEmail' },
                  {
                    title: 'Identity',
                    dataIndex: 'actorId',
                    render: (actor: string | null) => (
                      <Tag color={actor ? 'success' : 'default'}>
                        {actor ? 'Verified' : 'Pending'}
                      </Tag>
                    ),
                  },
                ]}
              />
              <Collapse
                ghost
                items={[
                  {
                    key: 'reference',
                    label: 'Import reference',
                    children: <code>{result.id}</code>,
                  },
                ]}
              />
            </div>
          )}
        </div>
      </Drawer>
    </>
  );
}
