# MONERGY-ASSISTANT-01B — implementation and operator handoff

This is an implementation candidate under `MONERGY-ASSISTANT-01B-DECISION-01`.
It is not an acceptance receipt. Builds, tests, new test-suite implementation,
lint/TypeScript, runtime, Docker, migrations, credentials and grants are deferred.
The separate [testing handoff](MONERGY-ASSISTANT-01B-TESTING-HANDOFF.md) retains the
full future acceptance coverage. Final commits/trees and draft PRs belong in the
external handoff, not self-referential commit metadata.

## Source authority

| Accepted baseline | Commit |
| --- | --- |
| Application | `995d0a30991ad2a14a464ade62e0ed948f3e6914` |
| Access Management | `85cb70159a1a8f700c2e15d191fb8097651c4b78` |
| Architecture | `80cb94953c2d67c9eee568d333ae3435532af0d8` |
| System Expert | `0bd252a317b1be3e3754b7ab8cdb855aa3561362` |

Forward Architecture amendment candidate: `90ea223fc37d47582010d603589506e1660dd659`,
tree `2b1d5225aaf765df1fffe787daa8f1d55694030d`, draft Architecture PR #27,
`docs/15-Architecture-Revisions/MONERGY-ASSISTANT-01B/MONERGY-ASSISTANT-01B-Feedback-Authority.md`.
Candidate dependencies do not replace Expert's accepted-source locks. The immutable
20-entry 01A package keeps canonical digest
`0e4020190ff2e204ab582fdcf1075a8ef90562845b502ddd695ecd9ed213039a`.
MONERGY-ASSISTANT-01A stays COMPLETE, `acceptedThrough=T1-06`, Business accepted=0.

## User and service boundaries

`apps/customer-web/src/assistant/` supplies the shared lazy Ask Monergy drawer,
safe browser adapters and restricted Questions to improve view. Tenant integration
is in the authenticated shared SessionShell; platform integration is inside the
authenticated Platform Administrator shell. Public/login/welcome pages have no
action. Questions, answers and unsent suggestions live only in component memory.
Closing/remounting, session expiry, tenant/workspace/authority change abort pending
work and destroy that state. Ordinary feature denial does not log out a valid user.

Gateway tenant routes are `/assistant-api/local/v1/{status,answers,help-topics}`.
Platform routes are `/platform-api/local/v1/assistant/{status,answers,help-topics}`
and `/platform-api/local/v1/assistant/review/topics[/{gapId}]`. Existing trusted
hostname routing determines the tenant/platform boundary. Current C&I session and
AM membership are required for tenant use; typed current platform context is
required on the platform side. Browser body fields never establish scope.

Only `{contractVersion,question}` and the fixed route workload credential go to
the dedicated Expert Runtime at internal port 5122. No customer identity, session,
workspace, browser headers or feedback is forwarded. The Gateway independently
checks the pinned catalogue, exact claims/provenance, route audience, question,
versions, sizes, closed shape and `NOT_USED` before publishing a plain projection.
No financial owner, AI generation endpoint or model provider is called for answers.

The Runtime serves only live/ready and the two fixed internal answer routes. Its
immutable acceptance receipt, intent index and claim audience policy are separate
from accepted canonical knowledge. It cannot write feedback or publish knowledge.
The five platform-only intents, related aliases and mixed comparison questions are
denied in product scope without a partial Organization Administrator fallback.

## Data owners and migrations

| Module | Application/companion source | Separate PostgreSQL namespace |
| --- | --- | --- |
| Feedback | `services/ai-intelligence/AssistantFeedback/` and `services/ai-intelligence/migrations/assistant-feedback/` | `ai_feedback` |
| Reviewer authority | AM `src/Monergy.AccessManagement/PlatformAuthorization/` and `tools/Monergy.AccessManagement.Migrations/platform-migrations/` | `am_platform` |
| Platform evidence | `services/audit/PlatformAudit/` and `services/audit/migrations/platform/` | `audit_platform` |

Profile-qualified databases share the existing PostgreSQL engine but have separate
owner/runtime credentials and migration journals. AM platform migration selection
never invokes tenant bootstrap or Default Access Packs. There are no cross-owner
SQL transactions or foreign keys, synthetic tenants or T001/T002 substitutions.
Audit runtime cannot update, delete or truncate immutable evidence. Existing tenant
Audit, tenant AM and Financial Q&A schemas, data and readiness remain separate.

The optional `profile.assistant` descriptor is consumed by a separate
`--local-uat-assistant-bootstrap` source command and optional Compose initializer.
No business service depends on that initializer succeeding; unavailable stores
remain unavailable only to their optional modules. Migration definitions are source only in this assignment.
No database, user, grant or schema was provisioned while preparing this candidate.

## Independent reviewer authority

AM defines only `platform.assistant.feedback.review`, an administrative platform
capability outside tenant grants and tenant evaluation. Default state is no grant.
Platform login, service workload credentials, migration and EnableAssistant do not
grant it. Tenant grant APIs explicitly reject the platform capability.

The platform identity issuer binds a short-lived opaque context to principal,
realm, current credential version, expiry, operation and resource. AM resolves that
context through a protected internal contract. Context references never enter the
feedback/audit database or Knowledge Runtime. AM performs current evaluation for
each review, and the feedback owner independently checks binding, freshness and
current revisions before restricted release or privileged commit. Revocation does
not require platform logout. Identity/AM failures deny review without disabling help.

`build/uat/Invoke-MonergyAssistantReviewer.ps1` is a protected LOCAL operator command
for the single grant/revoke capability. It sends an operation ID, current expected
policy revision, target principal and bounded reason to the AM CLI via stdin. The
CLI reads its distinct credential from an AM-only protected file, validates the
principal with the issuer and records the operator workload truthfully. It never
impersonates a human. There is no browser grant UI. The command has not been invoked.

## Workload purposes

| Purpose | Producer → recipient | Protected LOCAL material |
| --- | --- | --- |
| Product answers | Gateway → Runtime product route | `knowledgeProduct` |
| Platform answers | Gateway → Runtime platform route | `knowledgePlatform` |
| Intake/counters | Gateway → AI feedback ingest family | `feedbackIngest` |
| Review | Gateway → AI feedback review family | `feedbackReview` |
| Identity validation | AM → platform identity issuer | `identityValidation` |
| Reviewer evaluation | AI feedback → AM platform evaluator | `platformEvaluation` |
| Grant/revoke | Protected operator → AM command family | separate `assistant-operator-control` file |
| Feedback audit | AI feedback → Audit platform inbox | `feedbackAudit` |
| Authorization audit | AM → Audit platform inbox | `amAudit` |

Future preparation generates independent cryptographically random 32-byte values.
The operator credential is excluded from profile.json, Gateway and ordinary feedback
configuration and is mounted only by AM. Credentials stay server-side; there is no
new host port. Fixed-time comparisons, closed contracts and fixed destinations bind
the workloads. Existing model, tenant audit and financial-Q&A credentials are not reused.

## Feedback and audit lifecycle

Only explicit 500-character general-topic submissions are stored. They use a closed
2 KiB contract, deliberate confirmation, five/hour authenticated namespace limits and
idempotency. Server-derived audience and active versions are authoritative. Failed
persistence is reported as not saved, independently of the authenticated session.
Secret/identifier/unsafe/customer-detail screening is defense in depth; it is not
proof that arbitrary text is safe. New text remains restricted until privacy review.

Counters contain only scope, bounded outcome, governed topic or UNCLASSIFIED,
knowledge/retrieval revisions, UTC day and count. They do not contain question text,
question hashes, principals, customer/tenant IDs, IP or headers. UNCLASSIFIED is not
evidence of one repeated question. Recording is best-effort and bounded.

Review permits classification, assignment, duplicate links, status and controlled
privacy deletion. Restricted intake and approved general topics remain distinct.
Resolved needs a protected approved-publication receipt binding the active knowledge/
index version and structured approved-change/verification references. An opened PR,
pasted SHA or reviewer text is insufficient. Missing proof leaves Ready for verification.
No feedback can publish canonical knowledge automatically.

Default expiry is 30 days for unreviewed text, 180 days from original creation for
reviewed general topics and 90 days for daily counters. Read/retry cannot renew it.
Operational deletion is not a promise to delete historical backups ahead of their
separately approved lifecycle. Retention source is implemented but has not run here.

Owner state, idempotency and minimal audit outbox records commit atomically. AM cannot
release usable Allow without a durable audit obligation. Restricted reads establish
durable release-attempt evidence before content returns; this is not a claim a human
read the response. Central Audit accepts only producer-allowlisted versioned platform
events, returns matching receipts and rejects conflicting event reuse. Pending delivery
is recoverable; a central outage does not imply rollback of an already committed owner
operation. Bounded/full outboxes fail closed for further privileged operations.

Audit carries opaque event/operation/resource IDs, current actor/workload authority,
decision/revision metadata, bounded action/outcome/reason and timestamps. It never
carries questions, answers, reviewer free text, tenant/customer IDs or credentials.
Assistant routes are not added to customer-enriching ProductAuditCapture.

## Optional enablement and preserved disable

The source launcher adds `-EnableAssistant -SystemExpertRoot <clean committed Expert>`
only for a new isolated onboarding profile, proposed name `assistant01b`, port 4350.
It pins Expert HEAD/tree/catalogue bytes and refuses drift. No fixed workstation drive
letter is assumed. The future Docker overlay gives Runtime non-root/read-only execution,
dropped capabilities, bounded CPU/memory, no persistent volume and no published port.
Its readiness is separate infrastructure; the business topology remains fourteen.

An optional `-AssistantPublishedResolutionsFile` accepts an operator-reviewed immutable
receipt pinned by byte SHA-256, mounted only by the feedback owner. Omit it by default;
no live gap is asserted resolved. The source never generates approval evidence.

For a prepared Assistant profile, `-DisableAssistant` disables configuration and omits
the Runtime overlay on the next separately authorized Prepare/Start. It does not delete
the database descriptors, credentials, feedback, audit or volumes. Re-enable validates
the same retained source and credential pairing. No implicit retained-profile upgrade,
key rotation, Reset, prune or storage recreation is part of this feature.

## Verification status and limitations

All functional execution remains NOT RUN. No pass counts, operational readiness,
Production security assurance or acceptance are claimed. The later task must add tests
without weakening existing suites, compile every affected owner and the Gateway,
perform full Web checks and run physical isolation/persistence/audit/retention UAT.
LOCAL in-memory request limits do not claim distributed Production rate limiting.
The credential/session issuer remains the existing single LOCAL platform authority;
federation and multi-administrator provisioning are outside scope. Knowledge copy
changes remain separately governed; immutable accepted wording is not silently rewritten.
