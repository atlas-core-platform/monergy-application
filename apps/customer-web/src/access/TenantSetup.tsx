import { Alert, Button, Card, Checkbox, Descriptions, Space, Spin, Tag } from 'antd';
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { FoundationProvider, WorkspaceIcon } from '@monergy/ui-foundation';
import { AccessApiError, displayError } from './accessApi';
import type { AccessRecord, ResourceDirectoryItem } from './accessApi';
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
    people: AccessRecord[];
    teams: AccessRecord[];
    assignments: AccessRecord[];
    customers: ResourceDirectoryItem[];
    packPlan: DefaultAccessPackPlan | null;
  } | null>(null);
  const [revision, setRevision] = useState(0);
  const [organizationReviewed, setOrganizationReviewed] = useState(false);
  const [accessReviewed, setAccessReviewed] = useState(false);
  const [accessUpdateReviewed, setAccessUpdateReviewed] = useState(false);
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

        let roles: AccessRecord[] = [];
        let people: AccessRecord[] = [];
        let teams: AccessRecord[] = [];
        let assignments: { policyVersion: number; items: AccessRecord[] } = {
          policyVersion: state.policyVersion ?? 0,
          items: [],
        };
        let customerDirectory: { resourceType: string; items: ResourceDirectoryItem[] } = {
          resourceType: 'customer',
          items: [],
        };
        if (state.enabled && state.state !== 'Active') {
          [roles, people, teams, assignments, customerDirectory] = await Promise.all([
            api.all('roles', controller.signal, state.policyVersion),
            api.all('members', controller.signal, state.policyVersion),
            api.all('groups', controller.signal, state.policyVersion),
            state.customerRelationships
              ? api.request<{ policyVersion: number; items: AccessRecord[] }>(
                  'administration/customer-relationships?limit=100',
                  'GET',
                  undefined,
                  controller.signal,
                )
              : Promise.resolve({ policyVersion: state.policyVersion ?? 0, items: [] }),
            state.customerRelationships
              ? api.resourceDirectory('customer', controller.signal)
              : Promise.resolve({ resourceType: 'customer', items: [] }),
          ]);
          if (assignments.policyVersion !== state.policyVersion)
            throw new AccessApiError(409, 'POLICY_VERSION_CONFLICT');
        }
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
          setSnapshot({
            state,
            roles,
            people,
            teams,
            assignments: assignments.items,
            customers: customerDirectory.items,
            packPlan,
          });
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
    setAccessUpdateReviewed(false);
    setContinueWithExistingPolicy(false);
    setRequestId(crypto.randomUUID());
    setError('');
    setRevision((value) => value + 1);
  };

  const adoptAccessPack = async () => {
    if (!packPlan || packPlan.conflicts.length > 0 || !accessUpdateReviewed || busy) return;
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
  const roleLabel = (roleId?: string | null) =>
    snapshot?.roles.find((role) => role.id === roleId)?.label ?? 'No business role';
  const personLabel = (actorId?: string) =>
    snapshot?.people.find((person) => person.actorId === actorId)?.normalizedEmail ??
    'Person unavailable';
  const customerLabel = (customerId?: string) =>
    snapshot?.customers.find((customer) => customer.resourceId === customerId)?.displayName ??
    'Customer unavailable';
  const changeLabel = (change: DefaultAccessPackPlan['changes'][number]) => {
    const before = change.before?.split('|');
    const after = change.after?.split('|');
    switch (change.kind) {
      case 'ADD_ROLE':
        return `Add recommended role: ${after?.[1] ?? 'configured role'}`;
      case 'UPDATE_ROLE':
        return `Update recommended role: ${before?.[1] ?? 'current role'} → ${after?.[1] ?? 'recommended role'}`;
      case 'ADD_PERMISSION':
        return `Add access permission: ${after?.[1] ?? 'recommended permission'}`;
      case 'UPDATE_PERMISSION':
        return `Update access permission: ${before?.[1] ?? 'current permission'} → ${after?.[1] ?? 'recommended permission'}`;
      case 'SET_PACK_VERSION':
        return `Recommended access model: version ${change.before ?? 'current'} → ${change.after ?? packPlan?.targetVersion ?? '2.0'}`;
      default:
        return 'Update the recommended access configuration';
    }
  };
  const roleGuidance: Record<string, string> = {
    customer: 'Use customer-owned documents and supported customer journeys.',
    'financial-advisor':
      'Client access requires an assigned customer, implemented permissions and current customer Consent.',
    'operations-support':
      'Operational support access becomes available only through implemented support operations.',
    'dsa-case-manager': 'Case-management tasks remain unavailable until the case owner is implemented.',
    'lender-professional': 'Lending tasks remain unavailable until application/lender owners are implemented.',
    'financial-professional':
      'Professional verification tasks remain unavailable until governed engagements are implemented.',
    'property-valuer': 'Valuation tasks remain unavailable until governed valuation assignments are implemented.',
    'legal-advisor': 'Legal-review tasks remain unavailable until governed legal matters are implemented.',
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
            <h1 id="setup-title">
              {existingActive
                ? 'A Monergy access update is available.'
                : 'Welcome to Monergy — Set up your organization'}
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
                  <>
                    <Alert
                      showIcon
                      type="info"
                      title="Recommended access update available"
                      description={`Review the changes from version ${pack?.installedVersion ?? 'not installed'} to ${packPlan?.targetVersion ?? '2.0'}. Existing people, customer assignments and organization-owned custom access remain unchanged unless the reviewed plan says otherwise.`}
                    />
                    <section aria-label="Recommended access update review">
                      <h3>Review changes</h3>
                      <ul className="onboarding-roles">
                        {(packPlan?.changes.length ?? 0) > 0 ? (
                          packPlan?.changes.map((change, index) => (
                            <li key={`${change.kind}-${index}`}>
                              <span>{changeLabel(change)}</span>
                            </li>
                          ))
                        ) : (
                          <li>
                            <span>No access-object changes are required.</span>
                          </li>
                        )}
                      </ul>
                      <Checkbox
                        checked={accessUpdateReviewed}
                        disabled={busy || !packPlan}
                        onChange={(event) => {
                          setAccessUpdateReviewed(event.target.checked);
                        }}
                      >
                        I have reviewed the recommended access changes
                      </Checkbox>
                      <div>
                        <Button
                          type="primary"
                          loading={busy}
                          disabled={!packPlan || !accessUpdateReviewed || busy}
                          onClick={() => {
                            void adoptAccessPack();
                          }}
                        >
                          Apply reviewed access update
                        </Button>
                      </div>
                    </section>
                  </>
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
                            <div>
                              <strong>{role.label}</strong>
                              <small>
                                {roleGuidance[role.code ?? ''] ??
                                  'Tasks depend on implemented permissions and assignments.'}
                              </small>
                            </div>
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

                  <div className="onboarding-grid">
                    <Card
                      title={
                        <span>
                          <span className="onboarding-step">04</span>People and teams
                        </span>
                      }
                    >
                      <div aria-label="People configured for this organization">
                        {snapshot.people.length ? (
                          <ul>
                            {snapshot.people.map((person) => (
                              <li key={person.actorId ?? person.id}>
                                <strong>{person.normalizedEmail ?? 'Person unavailable'}</strong>
                                {' — '}
                                {person.tenantAdmin ? 'Organization Administrator' : roleLabel(person.businessRoleId)}
                              </li>
                            ))}
                          </ul>
                        ) : (
                          <p>No additional people configured — optional</p>
                        )}
                      </div>
                      <div aria-label="Teams configured for this organization">
                        {snapshot.teams.length ? (
                          <ul>
                            {snapshot.teams.map((team) => (
                              <li key={team.id}>
                                {team.label ?? team.code ?? 'Team'} · {team.memberCount ?? 0} members
                              </li>
                            ))}
                          </ul>
                        ) : (
                          <p>None configured — optional</p>
                        )}
                      </div>
                      <p className="access-form-hint">
                        Teams organize people and do not grant access. You can continue without a
                        team.
                      </p>
                    </Card>
                    <Card
                      title={
                        <span>
                          <span className="onboarding-step">05</span>Customer assignments
                        </span>
                      }
                    >
                      {snapshot.assignments.length ? (
                        <ul aria-label="Configured customer assignments">
                          {snapshot.assignments.map((assignment) => (
                            <li key={assignment.id}>
                              <strong>{customerLabel(assignment.customerId)}</strong>
                              {' — '}
                              {personLabel(assignment.actorId)}
                            </li>
                          ))}
                        </ul>
                      ) : (
                        <p>No customer assignments configured — optional</p>
                      )}
                      <p className="access-form-hint">
                        Customer assignments are required only for delegated customer workflows.
                        Customer self-access follows verified ownership.
                      </p>
                    </Card>
                  </div>

                  <Card>
                    <div className="onboarding-step">06</div>
                    <h2>Connected services</h2>
                    <Alert
                      type="info"
                      showIcon
                      title="External services — Not configured · Optional"
                      description="No external service is required to activate this organization. This test environment supports document/manual workflows without an external provider connection; unsupported services are not presented as connected."
                    />
                  </Card>

                  <Card>
                    <div className="onboarding-step">07</div>
                    <h2>Review and complete setup</h2>
                    <Descriptions
                      column={1}
                      size="small"
                      items={[
                        { key: 'organization', label: 'Organization', children: state.organizationName },
                        {
                          key: 'administrator',
                          label: 'Organization Administrator',
                          children: state.initialAdministratorEmail,
                        },
                        {
                          key: 'access',
                          label: 'Recommended access',
                          children: pack?.ready ? 'Version 2.0 confirmed' : 'Needs attention',
                        },
                        {
                          key: 'people',
                          label: 'People',
                          children: snapshot.people.length
                            ? snapshot.people
                                .map(
                                  (person) =>
                                    `${person.normalizedEmail ?? 'Person unavailable'} (${
                                      person.tenantAdmin
                                        ? 'Organization Administrator'
                                        : roleLabel(person.businessRoleId)
                                    })`,
                                )
                                .join(', ')
                            : 'No additional people configured',
                        },
                        {
                          key: 'teams',
                          label: 'Teams',
                          children: snapshot.teams.length
                            ? snapshot.teams
                                .map((team) => team.label ?? team.code ?? 'Team')
                                .join(', ')
                            : 'None configured — optional',
                        },
                        {
                          key: 'assignments',
                          label: 'Customer assignments',
                          children: snapshot.assignments.length
                            ? snapshot.assignments
                                .map(
                                  (assignment) =>
                                    `${customerLabel(assignment.customerId)} — ${personLabel(assignment.actorId)}`,
                                )
                                .join(', ')
                            : 'None configured — optional',
                        },
                        {
                          key: 'services',
                          label: 'External services',
                          children: 'Not configured — optional',
                        },
                      ]}
                    />
                    <p className="access-form-hint">
                      Recommended access is owner-confirmed. Teams, customer assignments and external
                      services shown as optional may be completed later when a supported workflow
                      requires them.
                    </p>
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
