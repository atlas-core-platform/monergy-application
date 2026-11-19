# MONERGY-ASSISTANT-01B — ownership and reviewer decision

Current status: **IMPLEMENTATION CANDIDATE PREPARED — AWAITING SEPARATE TESTING**.

Decision `MONERGY-ASSISTANT-01B-DECISION-01` supersedes the ownership blocker below.
AI Intelligence owns the isolated AssistantFeedback module; Access Management owns
separate platform reviewer authorization; Platform Control Plane remains the
identity issuer; Audit owns a separate platform evidence store; System Expert owns
read-only canonical knowledge. The original assessment below is preserved as
history, not the current decision. All four changes are implementation candidates.
Tests, builds, runtime, migrations and grant provisioning remain NOT RUN.

Forward amendment: Architecture
`docs/15-Architecture-Revisions/MONERGY-ASSISTANT-01B/MONERGY-ASSISTANT-01B-Feedback-Authority.md`.
Accepted-source locks remain unchanged. Candidate dependency references are separate.

Historical status: **BLOCKED — FEEDBACK OWNERSHIP AND REVIEWER AUTHORITY DECISION REQUIRED**.
Date: 2026-11-19. This is a source/design assessment, not a tested implementation.
The revised implementation-only brief replaces the previous physical-acceptance brief.

## Inspected accepted sources

| Repository | HEAD | Tree |
| --- | --- | --- |
| Application | `995d0a30991ad2a14a464ade62e0ed948f3e6914` | `5e40ffa758c008609de02c7ecb621223b6e7c9ab` |
| System Expert | `0bd252a317b1be3e3754b7ab8cdb855aa3561362` | `c7deb6f91fc038323009c3a92b9d5589afc05bde` |
| Architecture | `80cb94953c2d67c9eee568d333ae3435532af0d8` | `77c271846eeb3044170b5d97075560919f748305` |
| Access Management | `85cb70159a1a8f700c2e15d191fb8097651c4b78` | `ab5482549fbc8a27a5b6eaa583687906e80cbe4b` |

Remote main identities matched these inputs during the initial assessment. No newer
baseline was substituted. At that time the two initial isolated worktrees had no
production implementation commit. Decision-01 subsequently authorized four candidates.

## Historical finding, before Decision-01

The accepted source provides authentication for tenant and Platform Administrator
help, but does not establish an owner or a separately delegated reviewer entitlement
for the new product/platform knowledge-gap backlog. Selecting an existing store by
convenience, reusing generic platform authentication, or creating a new permission
without approval would exceed the brief.

| Inspected boundary | Evidence at the accepted source | Why it does not supply the required authority |
| --- | --- | --- |
| Application & Case | Application `services/application-case/README.md:3`; `migrations/0001_durable_assistance_cases.sql:11`; `ApplicationCaseRegistration.cs:40` | Owns financial-assistance and assigned professional cases. Existing records require tenant/customer identities and Financial assistance request type. This is not the required non-customer product/platform feedback store. |
| AI Intelligence | Architecture `docs/04-Service-Architecture/R3/Service-Catalog.md:73`; `docs/05-Data-Evidence-Knowledge/R4/Data-Ownership.md:32`; Application `services/ai-intelligence/Qa/QaStore.cs:9` | Owns governed financial RAG/AI Response Records and traces. Current storage is tenant/customer/actor-bound financial Q&A; general knowledge-gap intake and review ownership is not established. |
| Platform Control Plane | Architecture `docs/15-Architecture-Revisions/AR-001/Monergy-Architecture-Baseline-v1.1.md:37`; Application `platform/control-plane/PlatformOperations.cs:12` | Owns registry/placement/control metadata and service posture. That does not automatically authorize a help-topic backlog. |
| Platform authentication | Application `build/uat/Monergy.UatGateway/PlatformAdministrationEndpoints.cs:25`, `:39`, `:136`; `platform/control-plane/PlatformIdentityAndIntegrations.cs:125` | Establishes the current generic PlatformAdministrator session/credential version. No independent feedback-review entitlement is supplied. |
| AM capability catalogue | AM `src/Monergy.AccessManagement/ModelsAndEvaluator.cs:54`, `:131` | No help-topic/knowledge-gap review capability is defined; unknown capabilities fail closed. Existing professional-review and job-recovery capabilities have different purposes. |
| Operations Support | AM `docs/governance/P3-08-operations-support-workspace.md:3` | Job recovery does not confer knowledge-gap review or publication authority. |
| Knowledge Runtime | Architecture `docs/15-Architecture-Revisions/MONERGY-ASSISTANT-01A/MONERGY-ASSISTANT-01-Architecture.md:44` | Read-only, optional knowledge runtime; must not receive feedback persistence. |
| Audit | Architecture `docs/07-Security-Governance/R6/Audit-Architecture.md:3` | Audit evidence is not an authoritative domain-content store. Feedback text cannot be stored there. |

The older persistence matrix lists twelve service boundaries at its historical
baseline; it is cited for ownership semantics only, not as a replacement for the
current fourteen-business-service topology.

## Historical decision request — answered by Decision-01

1. Designate the existing backend owner whose accepted scope will include explicit
   general help-topic intake, restricted review, aggregate counters and retention.
   Approve its non-customer persistence namespace, migration authority, workload
   authentication boundary and backup/retention handling. No fifteenth service,
   Gateway database, Audit content store or Knowledge Runtime database is proposed.
2. Establish a distinct reviewer entitlement in addition to platform authentication,
   including who can assign/revoke it, where that authority resides and how the owner
   revalidates it on every review action. Ask Monergy access is not this entitlement.
3. Identify the existing approved audit path for reviewer actions/privacy deletion
   and the approved-change/published-version evidence that can authorize Resolved.
4. Separately authorize any necessary Architecture/AM amendment before changing
   those repositories. Preserve 01A's historical acceptance.

Until that decision is accepted, do not implement persistence, counter recording,
submission/review APIs, migration definitions, reviewer UI or an in-memory substitute.
The read-only Ask Monergy path is independently feasible; it has not been implemented
in this assessment and the combined revised 01B candidate is not ready.

## Historical assessment execution record

The accepted 20-entry package remains unchanged, with canonical digest
`0e4020190ff2e204ab582fdcf1075a8ef90562845b502ddd695ecd9ed213039a`.
No new acceptance receipt, retrieval index, audience policy, Runtime or feedback
artifact was constructed. Expert's current Application lock remains `995d0a30991ad2a14a464ade62e0ed948f3e6914`.
MONERGY-ASSISTANT-01A remains COMPLETE; `acceptedThrough=T1-06` and Business accepted = 0.

No tests, new test suites, builds, lint, TypeScript, model/canonical validation,
Runtime startup, Docker builds/startup, browser acceptance, profile preparation,
credential generation, migration application, retention jobs, external AI calls,
Azure changes, workflow dispatch or merge were performed for this revised brief.
An earlier superseded brief attempted dependency installation before cancellation;
that is not functional verification or evidence for an 01B candidate.

Only this decision note, the NOT RUN testing handoff and a local blocked tracker
entry are prepared. No PR is represented as a complete implementation candidate.
Existing retained environments, databases, volumes, credentials, archives, worktrees,
operator changes and accepted evidence remain untouched.

## Decision-01 implementation transition

The historical BLOCKED state transitioned to DESIGN AUTHORIZED / IMPLEMENTATION IN
PROGRESS and then to implementation candidate prepared. The new forward Architecture
amendment and separate AM platform module supply the authority previously missing.
Application now contains Ask Monergy, the typed identity bridge, isolated AI feedback,
platform Audit and opt-in launcher/bootstrap source. Expert contains the read-only
Runtime and separate constructed retrieval/audience/receipt artifacts.

See [implementation boundaries](MONERGY-ASSISTANT-01B-IMPLEMENTATION.md),
[candidate dependencies](MONERGY-ASSISTANT-01B-CANDIDATE-DEPENDENCIES.json) and
[deferred testing](MONERGY-ASSISTANT-01B-TESTING-HANDOFF.md). No functional result,
runtime readiness, migration success or acceptance is claimed. Final four candidate
identities and draft PRs are recorded externally after commits. Existing 01A acceptance,
accepted-source locks, acceptedThrough=T1-06 and Business accepted=0 are unchanged.
