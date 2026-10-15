import { Alert, Button, Card, Checkbox, Descriptions, Space, Spin, Tag } from 'antd';
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { FoundationProvider, WorkspaceIcon } from '@monergy/ui-foundation';
import { AccessApiError, displayError } from './accessApi';
import type { AccessRecord } from './accessApi';
import { localUat, useWorkspaceSession } from './workspaceSession';
import { OnboardingContext } from './onboarding';
import type {
  DefaultAccessPackAdoptionResult,
  DefaultAccessPackPlan,
  TenantSetupState,
} from './onboarding';
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
    packPlan: DefaultAccessPackPlan | null;
  } | null>(null);
  const [revision, setRevision] = useState(0);
  const [organizationReviewed, setOrganizationReviewed] = useState(false);
  const [accessReviewed, setAccessReviewed] = useState(false);
  const [requestId, setRequestId] = useState(() => crypto.randomUUID());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [onboardingUser, setOnboardingUser] = useState(false);
  const [continueWithExistingPolicy, setContinueWithExistingPolicy] = useState(false);

  useEffect(() => {
    if (!api || !session) return;
    const controller = new AbortController();
    void api
      .owner<TenantSetupState>('uat', 'v1/setup', controller.signal)
      .then(async (state) => {
        const pack = state.accessPack;
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
              !pack ||
              !Number.isSafeInteger(pack.policyVersion) ||
              pack.policyVersion !== state.policyVersion ||
              typeof pack.ready !== 'boolean' ||
              (state.state === 'Active' && !state.receiptReference)))
        )
          throw new AccessApiError(503, 'SETUP_RESPONSE_INVALID');

        const roles =
          state.enabled && state.state !== 'Active'
            ? await api.all('roles', controller.signal, state.policyVersion)
            : [];
        const packPlan =
          state.enabled && pack && !pack.ready
            ? await api.request<DefaultAccessPackPlan>(
                'administration/default-access-pack/plan',
                'GET',
                undefined,
                controller.signal,
              )
            : null;
        if (
          packPlan &&
          (packPlan.targetVersion !== '2.0' ||
            packPlan.policyVersion !== state.policyVersion ||
            !/^[0-9a-f]{64}$/.test(packPlan.planHash))
        )
          throw new AccessApiError(503, 'SETUP_RESPONSE_INVALID');

        if (!controller.signal.aborted) {
          setSnapshot({ state, roles, packPlan });
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
  const pack = state?.accessPack;
  const packPlan = snapshot?.packPlan;

  if (
    state &&
    (!state.enabled ||
      (state.state === 'Active' && (pack?.ready === true || continueWithExistingPolicy)))
  )
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

  const adoptAccessPack = async () => {
    if (!packPlan || packPlan.conflicts.length > 0 || busy) return;
    setBusy(true);
    setError('');
    try {
      await api.request<DefaultAccessPackAdoptionResult>(
        'administration/default-access-pack/adopt',
        'POST',
        {
          expectedPolicyVersion: packPlan.policyVersion,
          operationId: `setup-pack-${requestId}`,
          planHash: packPlan.planHash,
        },
      );
      refresh();
    } catch (failure) {
      setError(displayError(failure));
    } finally {
      setBusy(false);
    }
  };

  const finish = async () => {
    if (!state || !pack?.ready || busy) return;
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

  const existingActive = state?.state === 'Active';
  const accessUpdateBlocked = Boolean(packPlan?.conflicts.length);

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
            <h1 id="setup-title">
              {existingActive ? 'A Monergy access update is available.' : 'Set up your organization.'}
            </h1>
            <p>
              {existingActive
                ? 'Review the recommended access update before applying it. Your current access remains unchanged until you choose to update it.'
                : 'Review your organization and recommended access model before activation.'}
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
          ) : state?.state !== 'ReadyForAdmin' && state?.state !== 'Active' ? (
            <Alert
              type="warning"
              showIcon
              title="Organization preparation is incomplete"
              description="The required owner services have not completed provisioning. Refresh after preparation succeeds."
              action={<Button onClick={refresh}>Check again</Button>}
            />
          ) : (
            <>
              <Card
                title={
                  <span>
                    <span className="onboarding-step">01</span>Recommended access
                  </span>
                }
              >
                {pack?.ready ? (
                  <Alert
                    showIcon
                    type="success"
                    title="Recommended access model ready"
                    description="Monergy has confirmed the current recommended role template for this organization."
                  />
                ) : accessUpdateBlocked ? (
                  <Alert
                    showIcon
                    type="warning"
                    title="Your customized access needs review"
                    description="Monergy will not overwrite customized starter roles or permissions automatically. Review your existing access settings before adopting the recommended template."
                  />
                ) : (
                  <Alert
                    showIcon
                    type="info"
                    title="Recommended access update available"
                    description={`Update from version ${pack?.installedVersion ?? 'not installed'} to ${packPlan?.targetVersion ?? '2.0'}. Existing member assignments and organization-owned custom access are preserved by the owner.`}
                    action={
                      <Button
                        type="primary"
                        loading={busy}
                        disabled={!packPlan || busy}
                        onClick={() => {
                          void adoptAccessPack();
                        }}
                      >
                        Apply recommended access update
                      </Button>
                    }
                  />
                )}
                {existingActive && !pack?.ready && (
                  <Button
                    type="link"
                    onClick={() => {
                      setContinueWithExistingPolicy(true);
                    }}
                  >
                    Continue with current access
                  </Button>
                )}
              </Card>

              {!existingActive && (
                <>
                  <div className="onboarding-grid">
                    <Card
                      title={
                        <span>
                          <span className="onboarding-step">02</span>Your organization
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
                            label: 'Organization Administrator',
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
                          <span className="onboarding-step">03</span>Recommended roles
                        </span>
                      }
                    >
                      <p>
                        Each person has at most one business role. Organization Administrator
                        authority is separate and does not grant financial access.
                      </p>
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
                      <p className="access-form-hint">
                        Customer access follows verified ownership. Advisor access also requires an
                        assigned customer, the role’s permissions, and current customer Consent.
                      </p>
                      <Checkbox
                        checked={accessReviewed}
                        disabled={busy || Boolean(state.receiptReference) || !pack?.ready}
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
                      <h2>Add people</h2>
                      <p>
                        Create a person now, or continue in People after setup. No minimum team size
                        is required. This test environment does not send invitations.
                      </p>
                    </div>
                    <Button
                      disabled={busy || Boolean(state.receiptReference) || !pack?.ready}
                      onClick={() => {
                        setOnboardingUser(true);
                      }}
                    >
                      Create person
                    </Button>
                  </Card>

                  <div className="onboarding-complete">
                    <p>
                      {state.receiptReference
                        ? 'Your review is saved. Resume to finish organization activation.'
                        : 'Completing setup records your review and activates this organization.'}
                    </p>
                    <Button
                      type="primary"
                      loading={busy}
                      disabled={
                        busy ||
                        !pack?.ready ||
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
