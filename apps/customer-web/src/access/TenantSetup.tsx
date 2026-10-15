import { Alert, Button, Card, Checkbox, Descriptions, Space, Spin, Tag } from 'antd';
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { FoundationProvider, WorkspaceIcon } from '@monergy/ui-foundation';
import { AccessApiError, displayError } from './accessApi';
import type { AccessRecord } from './accessApi';
import { localUat, useWorkspaceSession } from './workspaceSession';
import { OnboardingContext } from './onboarding';
import type { SetupAccessPolicyView, TenantSetupState } from './onboarding';
import { ImportDrawer } from './ImportDrawer';
import './onboarding.css';

const accessPolicyStates = ['Ready', 'UpdateAvailable', 'Customized', 'Blocked'] as const;

function validAccessPolicy(value: SetupAccessPolicyView | undefined): value is SetupAccessPolicyView {
  return (
    Boolean(value) &&
    accessPolicyStates.includes(value?.state as (typeof accessPolicyStates)[number]) &&
    [value?.changeCount, value?.roleChangeCount, value?.permissionChangeCount, value?.conflictCount]
      .every((item) => Number.isSafeInteger(item) && (item ?? -1) >= 0)
  );
}

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
              !validAccessPolicy(state.accessPolicy) ||
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

  const accessPolicy = state?.accessPolicy;
  const accessPolicyAllowsCompletion =
    accessPolicy?.state === 'Ready' || accessPolicy?.state === 'Customized';

  const refresh = () => {
    setSnapshot(null);
    setOrganizationReviewed(false);
    setAccessReviewed(false);
    setRequestId(crypto.randomUUID());
    setError('');
    setRevision((value) => value + 1);
  };

  const applyRecommendedAccess = async () => {
    if (!state || accessPolicy?.state !== 'UpdateAvailable' || busy) return;
    setBusy(true);
    setError('');
    try {
      await api.setup('/access-policy/adopt', { requestId });
      refresh();
    } catch (failure) {
      setError(displayError(failure));
    } finally {
      setBusy(false);
    }
  };

  const finish = async () => {
    if (!state || !accessPolicyAllowsCompletion || busy) return;
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

  const accessPolicyNotice = () => {
    if (!accessPolicy) return null;
    if (accessPolicy.state === 'Ready')
      return (
        <Alert
          showIcon
          type="success"
          title="Recommended access is ready"
          description="Your organization has Monergy’s current starter roles. Roles with implemented customer journeys are ready to use; future roles remain configured without placeholder access."
        />
      );
    if (accessPolicy.state === 'UpdateAvailable')
      return (
        <Alert
          showIcon
          type="info"
          title="Recommended access update available"
          description={
            <>
              Monergy has a reviewed starter-access update for this organization. Applying it updates
              only unchanged Monergy starter policy; it does not grant administrative authority.
              <div className="access-form-actions">
                <Button
                  type="primary"
                  loading={busy}
                  onClick={() => {
                    void applyRecommendedAccess();
                  }}
                >
                  Apply recommended access
                </Button>
              </div>
            </>
          }
        />
      );
    if (accessPolicy.state === 'Customized')
      return (
        <Alert
          showIcon
          type="warning"
          title="Your organization uses a customized access model"
          description="Monergy will not replace customized roles or permissions. Review the access model below and continue if it reflects how your organization should work."
        />
      );
    return (
      <Alert
        showIcon
        type="error"
        title="Access setup needs attention"
        description="Monergy could not confirm a supported access-policy state. Organization activation is blocked until the Access Management owner is healthy and the policy state is reviewed."
      />
    );
  };

  return (
    <FoundationProvider appearance="midnight">
      <div className="access-entry onboarding-entry" data-monergy-theme="midnight">
        <header className="access-public-header">
          <span className="access-wordmark">
            <WorkspaceIcon name="shield" size={24} /> monergy
          </span>
          <Space>
            <Tag>Test environment</Tag>
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
            <p className="workspace-eyebrow">ORGANIZATION SETUP</p>
            <h1 id="setup-title">Welcome to Monergy — set up your organization.</h1>
            <p>
              Review your organization, recommended access and people before activating this
              workspace.
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
              <Spin /> Checking organization readiness…
            </div>
          ) : (
            <>
              {state?.state !== 'ReadyForAdmin' ? (
                <Alert
                  type="warning"
                  showIcon
                  title="Organization preparation is incomplete"
                  description="The required provisioning receipts are not complete yet. Refresh after platform preparation succeeds."
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
                            label: 'Organization administrator',
                            children: state.initialAdministratorEmail,
                          },
                        ]}
                      />
                      <p className="access-form-hint">
                        These details belong to this organization. Contact your Monergy platform
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
                          <span className="onboarding-step">02</span>Recommended access
                        </span>
                      }
                    >
                      <p>
                        Each person has at most one business role. Organization Administrator is
                        separate and does not grant access to customer financial information.
                      </p>

                      {accessPolicyNotice()}

                      {accessPolicy?.state !== 'UpdateAvailable' &&
                        accessPolicy?.state !== 'Blocked' && (
                          <ul className="onboarding-roles">
                            {snapshot.roles.map((role) => (
                              <li key={role.id}>
                                <strong>{role.label}</strong>
                                <span>
                                  {(role.permissionIds?.length ?? 0) > 0
                                    ? 'Access ready'
                                    : 'Role configured'}
                                </span>
                              </li>
                            ))}
                          </ul>
                        )}

                      <p className="access-form-hint">
                        Customer access follows verified ownership. Advisor access also requires an
                        assigned customer, the role’s permissions and current customer Consent.
                      </p>
                      <Checkbox
                        checked={accessReviewed}
                        disabled={
                          busy ||
                          Boolean(state.receiptReference) ||
                          !accessPolicyAllowsCompletion
                        }
                        onChange={(event) => {
                          setAccessReviewed(event.target.checked);
                        }}
                      >
                        I have reviewed how roles and customer access work
                      </Checkbox>
                    </Card>
                  </div>

                  <Card className="onboarding-team">
                    <div>
                      <h2>Bring your team into Monergy</h2>
                      <p>
                        Create a person now, or continue in People after setup. No minimum team size
                        is required. This test environment does not send invitations.
                      </p>
                    </div>
                    <Button
                      disabled={
                        busy ||
                        Boolean(state.receiptReference) ||
                        !accessPolicyAllowsCompletion
                      }
                      onClick={() => {
                        setOnboardingUser(true);
                      }}
                    >
                      Add person
                    </Button>
                  </Card>

                  <div className="onboarding-complete">
                    <p>
                      {state.receiptReference
                        ? 'Your review is saved. Resume to activate this organization.'
                        : 'Completing setup saves your review and activates this organization in the test environment.'}
                    </p>
                    <Button
                      type="primary"
                      loading={busy}
                      disabled={
                        busy ||
                        !accessPolicyAllowsCompletion ||
                        (!state.receiptReference && (!organizationReviewed || !accessReviewed))
                      }
                      onClick={() => {
                        void finish();
                      }}
                    >
                      {state.receiptReference ? 'Resume activation' : 'Complete organization setup'}
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
