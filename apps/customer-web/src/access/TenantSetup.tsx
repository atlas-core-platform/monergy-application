import { Alert, Button, Card, Checkbox, Descriptions, Space, Spin, Tag } from 'antd';
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { FoundationProvider, WorkspaceIcon } from '@monergy/ui-foundation';
import { AccessApiError, displayError } from './accessApi';
import type { AccessRecord } from './accessApi';
import { localUat, useWorkspaceSession } from './workspaceSession';
import { OnboardingContext } from './onboarding';
import type { TenantSetupState } from './onboarding';
import { ImportDrawer } from './ImportDrawer';
import './onboarding.css';

export function TenantSetupGate({ children }: { children: ReactNode }) {
  return localUat ? <LocalTenantSetup>{children}</LocalTenantSetup> : children;
}

export function LocalTenantSetup({ children }: { children: ReactNode }) {
  const { api, session, signOut } = useWorkspaceSession();
  const [snapshot, setSnapshot] = useState<{
    state: TenantSetupState;
    roles: AccessRecord[];
  } | null>(null);
  const [revision, setRevision] = useState(0);
  const [organizationReviewed, setOrganizationReviewed] = useState(false);
  const [accessReviewed, setAccessReviewed] = useState(false);
  const [requestId, setRequestId] = useState(() => crypto.randomUUID());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [onboardingUser, setOnboardingUser] = useState(false);
  useEffect(() => {
    if (!api || !session) return;
    const controller = new AbortController();
    void api
      .owner<TenantSetupState>('uat', 'v1/setup', controller.signal)
      .then(async (state) => {
        if (
          state.tenantId !== session.tenantId ||
          typeof state.enabled !== 'boolean' ||
          typeof state.customerRelationships !== 'boolean' ||
          (state.enabled &&
            (state.actorId !== session.actorId ||
              !Number.isSafeInteger(state.policyVersion) ||
              (state.policyVersion ?? 0) < 1 ||
              !state.organizationName ||
              !state.countryCode ||
              !state.timeZone ||
              !state.initialAdministratorEmail ||
              ![
                'Requested',
                'Provisioning',
                'ProvisioningFailed',
                'ReadyForAdmin',
                'Active',
              ].includes(state.state ?? '') ||
              (state.state === 'Active' && !state.receiptReference)))
        )
          throw new AccessApiError(503, 'SETUP_RESPONSE_INVALID');
        const roles =
          state.enabled && state.state !== 'Active'
            ? await api.all('roles', controller.signal, state.policyVersion)
            : [];
        if (!controller.signal.aborted) {
          setSnapshot({ state, roles });
          setError('');
        }
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) setError(displayError(failure));
      });
    return () => {
      controller.abort();
    };
  }, [api, session, revision]);
  if (!api || !session) return null;
  const state = snapshot?.state;
  if (state && (!state.enabled || state.state === 'Active'))
    return (
      <OnboardingContext value={{ customerRelationships: state.customerRelationships }}>
        {children}
      </OnboardingContext>
    );
  const refresh = () => {
    setSnapshot(null);
    setOrganizationReviewed(false);
    setAccessReviewed(false);
    setRequestId(crypto.randomUUID());
    setError('');
    setRevision((value) => value + 1);
  };
  const finish = async () => {
    if (!state || busy) return;
    setBusy(true);
    setError('');
    try {
      await api.setup(
        state.receiptReference ? '/activate' : '',
        state.receiptReference
          ? {}
          : {
              requestId,
              expectedPolicyVersion: state.policyVersion,
              organizationReviewed,
              accessReviewed,
            },
      );
      refresh();
    } catch (failure) {
      setError(displayError(failure));
    } finally {
      setBusy(false);
    }
  };
  return (
    <FoundationProvider appearance="midnight">
      <div className="access-entry onboarding-entry" data-monergy-theme="midnight">
        <header className="access-public-header">
          <span className="access-wordmark">
            <WorkspaceIcon name="shield" size={24} /> monergy
          </span>
          <Space>
            <Tag>Local UAT</Tag>
            <Button
              type="text"
              onClick={() => {
                void signOut();
              }}
            >
              Sign out
            </Button>
          </Space>
        </header>
        <main className="onboarding-main" aria-labelledby="setup-title">
          <div className="onboarding-heading">
            <p className="workspace-eyebrow">WORKSPACE SETUP</p>
            <h1 id="setup-title">A clear start for your team.</h1>
            <p>
              Review your organization and access model. Your progress is saved when you complete
              setup.
            </p>
          </div>
          {error && (
            <Alert
              showIcon
              type="error"
              title={error}
              action={<Button onClick={refresh}>Refresh setup</Button>}
            />
          )}
          {!snapshot ? (
            <div className="access-loading" role="status">
              <Spin /> Checking workspace readiness…
            </div>
          ) : (
            <>
              {state?.state !== 'ReadyForAdmin' ? (
                <Alert
                  type="warning"
                  showIcon
                  title="Workspace preparation is incomplete"
                  description="An owner has not completed the required provisioning. Refresh after the local setup process succeeds."
                  action={<Button onClick={refresh}>Check again</Button>}
                />
              ) : (
                <>
                  <div className="onboarding-grid">
                    <Card
                      title={
                        <span>
                          <span className="onboarding-step">01</span>Your organization
                        </span>
                      }
                    >
                      <Descriptions
                        column={1}
                        size="small"
                        items={[
                          { key: 'name', label: 'Organization', children: state.organizationName },
                          { key: 'country', label: 'Country', children: state.countryCode },
                          { key: 'zone', label: 'Time zone', children: state.timeZone },
                          {
                            key: 'admin',
                            label: 'Initial administrator',
                            children: state.initialAdministratorEmail,
                          },
                        ]}
                      />
                      <p className="access-form-hint">
                        These details belong to the selected local workspace. Contact your platform
                        administrator if they are incorrect.
                      </p>
                      <Checkbox
                        checked={organizationReviewed}
                        disabled={busy || Boolean(state.receiptReference)}
                        onChange={(event) => {
                          setOrganizationReviewed(event.target.checked);
                        }}
                      >
                        I have reviewed the organization details
                      </Checkbox>
                    </Card>
                    <Card
                      title={
                        <span>
                          <span className="onboarding-step">02</span>Your access model
                        </span>
                      }
                    >
                      <p>
                        Each user has at most one business role. Tenant administrator authority is
                        separate and grants no financial access.
                      </p>
                      <ul className="onboarding-roles">
                        {snapshot.roles.map((role) => (
                          <li key={role.id}>
                            <strong>{role.label}</strong>
                            <span>{role.permissionIds?.length ?? 0} permissions</span>
                          </li>
                        ))}
                      </ul>
                      <p className="access-form-hint">
                        Customer access follows verified ownership. Advisor access also requires an
                        assigned customer, the role’s permissions, and current customer Consent.
                      </p>
                      <Checkbox
                        checked={accessReviewed}
                        disabled={busy || Boolean(state.receiptReference)}
                        onChange={(event) => {
                          setAccessReviewed(event.target.checked);
                        }}
                      >
                        I understand how roles and customer access work
                      </Checkbox>
                    </Card>
                  </div>
                  <Card className="onboarding-team">
                    <div>
                      <h2>Bring your team into the workspace</h2>
                      <p>
                        Create a user now, or continue in Users after setup. No minimum team size is
                        required. Local account creation does not send invitations.
                      </p>
                    </div>
                    <Button
                      disabled={busy || Boolean(state.receiptReference)}
                      onClick={() => {
                        setOnboardingUser(true);
                      }}
                    >
                      Create user
                    </Button>
                  </Card>
                  <div className="onboarding-complete">
                    <p>
                      {state.receiptReference
                        ? 'Your review is saved. Resume to finish workspace activation.'
                        : 'Completing setup records your review and activates this local workspace.'}
                    </p>
                    <Button
                      type="primary"
                      loading={busy}
                      disabled={
                        busy ||
                        (!state.receiptReference && (!organizationReviewed || !accessReviewed))
                      }
                      onClick={() => {
                        void finish();
                      }}
                    >
                      {state.receiptReference ? 'Resume activation' : 'Complete setup'}
                    </Button>
                  </div>
                </>
              )}
            </>
          )}
          {onboardingUser && (
            <ImportDrawer
              api={api}
              single
              importId={null}
              onClose={() => {
                setOnboardingUser(false);
                refresh();
              }}
              onChanged={refresh}
              onExpired={() => undefined}
            />
          )}
        </main>
      </div>
    </FoundationProvider>
  );
}
