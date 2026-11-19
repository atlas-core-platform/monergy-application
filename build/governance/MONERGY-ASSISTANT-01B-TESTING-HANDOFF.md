# MONERGY-ASSISTANT-01B — separate testing handoff

Status: **IMPLEMENTATION CANDIDATE PREPARED — ALL TEST EXECUTION NOT RUN**.
Date: 2026-11-19.

This is a coverage plan, not a runnable test harness or acceptance receipt.
[Decision-01](MONERGY-ASSISTANT-01B-OWNERSHIP-DECISION.md) resolves ownership and reviewer
authority. A separate assignment must implement tests and execute them against the
final four committed candidates. Implementation does not establish product acceptance
or readiness to merge. Do not run these commands as part of implementation.

## Required inputs before testing

- Decision-01 forward amendment: AI Intelligence feedback, AM platform authorization,
  Platform Control Plane identity and separate platform Audit. Default reviewer denied.
- Exact committed Architecture, AM, Application and Expert candidate HEADs/trees recorded externally
  after implementation. Do not insert a commit's own final SHA in its committed files.
- Immutable 01A package plus a separate constructed acceptance receipt; candidate
  retrieval/index, intent/claim audience policy and deployment catalogue identities.
- Expert accepted source locks remain Application `995d0a30991ad2a14a464ade62e0ed948f3e6914`,
  Architecture `80cb94953c2d67c9eee568d333ae3435532af0d8`, AM
  `85cb70159a1a8f700c2e15d191fb8097651c4b78` until separately directed reconciliation.
- New future profile `assistant01b`, suggested free host port 4350; explicit Expert
  worktree. Do not reuse retained profiles or regenerate retained credentials.
- Protected future fixtures: normal password-authenticated OrgAdmin and DSA, existing
  platform authentication, platform principal without reviewer entitlement, authorized
  reviewer, revoked/expired sessions, T001/T002 separation and disposable feedback data.

## Planned source commands — NOT RUN

Run only after implementation and separate testing authorization, in the corresponding
isolated candidate worktree. Add the new test suites in that later task; the paths of
new suites and owner migrations are not invented here.

```powershell
# System Expert: existing full suite and model validation, after new test implementation.
node --test
node bin/monergy-expert.js validate
# The later tester must use the final catalogue/export command and repeat compilation
# with the explicitly accepted source roots, not an unaccepted Application candidate.

# Application: existing source-quality commands; still require the relevant .NET suites.
pnpm --filter @monergy/customer-web test
pnpm --filter @monergy/customer-web exec eslint src tests vite.config.ts --max-warnings=0
pnpm --filter @monergy/customer-web typecheck
pnpm --filter @monergy/customer-web build
dotnet build build/uat/Monergy.UatGateway/Monergy.UatGateway.csproj --configuration Release
```

The actual focused .NET filters, new React/Runtime suites, owner migration command,
retention fixtures and browser harness must be finalized from the implemented source.
Do not treat missing tests or unspecified owner commands as passed checks.

## Future LOCAL command shape — NOT RUN

```powershell
.\build\uat\Invoke-MonergyUat.ps1 `
  -Action Start -Profile assistant01b -Port 4350 `
  -EnableOnboarding -EnablePersonaLogin -EnablePersonaTesterActivation `
  -EnableAssistant -SystemExpertRoot '<exact clean Expert candidate worktree>'
```

`EnableAssistant` and `SystemExpertRoot` are implemented opt-in source parameters.
Do not execute this example yet. The later testing phase must record
candidate identities before Prepare/Start and use owner-approved migration definitions.

## Coverage and evidence matrix — every result NOT RUN

| Area | Required later verification | Result |
| --- | --- | --- |
| Accepted knowledge | Receipt/package digest equality; immutable 20 entries; historical provenance and current accepted source locks | NOT RUN |
| Retrieval | Every entry in permitted audience; NFKC/case/punctuation; reviewed aliases; ambiguity/ties; unknown, unresolved, unsupported and conflict; no semantic/model fallback | NOT RUN |
| Audience | Five platform-only questions, role comparison, aggregate and mixed questions; no partial tenant fallback; missing intent/claim metadata fails closed | NOT RUN |
| Runtime input | Exact contractVersion/question fields; 8 KiB/2,000-character bounds; JSON object; no query overrides, identifiers, cookies or browser headers | NOT RUN |
| Runtime credentials | Distinct 32-byte-or-better credentials; fixed-time checking; cross-route credential denial; no browser/log disclosure | NOT RUN |
| Runtime output/readiness | Closed canonical answers, source/index/knowledge pins, permitted claims, NOT_USED; corrupt/stale refusal; no operator endpoints or persistence | NOT RUN |
| Gateway auth | Trusted hosts/current sessions/active membership; hostname/session match; current platform authority; no browser-selected scope | NOT RUN |
| Gateway boundaries | Platform-to-tenant and tenant-to-platform 404; raw/unknown hosts rejected; T001/T002 isolation; revoked/expired sessions denied | NOT RUN |
| Gateway validation | Minimal forwarded payload; fixed destination/no redirects; exact claims/text/provenance; disallowed claims rejected independently of declared scope; closed sizes/fields | NOT RUN |
| Browser projection | Plain state messages/source labels; no raw repository/SHA/path/provenance/workload/model metadata; no-store | NOT RUN |
| UI shells | OrgAdmin, DSA, Customer and all accepted tenant workspaces/KYC if available; platform shell; hidden before auth and on welcome/password pages | NOT RUN |
| UI lifecycle | Lazy loading, suggestions, About this answer, no processing badge; abort/reset on switch/sign-out/expiry/tenant/platform change; ignore late responses; no browser persistence | NOT RUN |
| Question limits | Separate authenticated tenant+actor/platform namespaces; 20/minute; bounded expiry and Retry-After; no question text in state | NOT RUN |
| Aggregate counters | Server-observed validated outcomes only; bounded daily categories/topics/revisions; no text/hash/identity/IP; separate submission counts; unclassified is not a single topic | NOT RUN |
| Explicit submissions | Separate user confirmation, editable general question, closed 500-character/2 KiB input; no attachments/scope/outcome claims; server versions; only persisted success acknowledged | NOT RUN |
| Submission privacy | Secret/identifier/injection/customer-detail rejection; restricted intake; no raw text in logs/audit; privacy deletion; screening limitations acknowledged | NOT RUN |
| Submission retries | Idempotency and isolated five/hour principal limits; no duplicate persistence or retention extension | NOT RUN |
| Reviewer authority | Platform authentication plus independently current reviewer entitlement; non-reviewer and tenant denials; revoke authority during pending actions | NOT RUN |
| Review lifecycle | Opaque IDs, bounded paging/filters, concurrency; classify/assign/duplicate/status/privacy deletion; restricted intake separate from approved topics | NOT RUN |
| Resolution | Approved change plus published resolving version plus verification evidence; no auto-publication; pasted SHA/open PR alone cannot resolve | NOT RUN |
| Retention | 30-day unreviewed text, 180-day approved topic, 90-day aggregates; no renewal on read/retry; owner backups documented; disposable fixtures only | NOT RUN |
| Owner persistence | Least-privilege runtime grants, owner-only migration, workload auth and reviewer audit without content; no Gateway/Runtime/Audit content database | NOT RUN |
| Failure isolation | Runtime outage affects help only; counter drop/storage failures do not block answers; submission failure honestly not saved; review-store outage remains bounded | NOT RUN |
| Physical personas | Normal tenant password login for OrgAdmin/DSA; accepted platform sign-in; tester credential never substitutes for session | NOT RUN |
| Physical topology | 14/14 business services, Runtime separately Ready; internal 5122 has no host mapping; correct registered tenant/platform hosts | NOT RUN |
| Physical question set | Role/Consent/integration/calculation-owner guidance, five restricted questions, absent topic, unsafe input, customer-specific question with no owner-data lookup | NOT RUN |
| No external models | Source/runtime/request evidence of no AI-generation/Integration AI/provider call, internal NOT_USED; no inference from UI wording | NOT RUN |
| Log privacy | Exact ordinary/submitted questions and protected credentials absent from Gateway/Runtime/owner logs; report counts without reproducing secrets/content | NOT RUN |
| Restart/readback | Same new profile, unchanged credentials and revisions, preserved permitted feedback only, no conversation history; both audiences recover | NOT RUN |
| Source quality | Full/new Expert tests, model/canonical suites, Explorer compatibility, relevant .NET/routing/React tests, full ESLint/TS/production/bundle and Gateway Release | NOT RUN |
| Hygiene/governance | Static changed/expanded-artifact scans, diff checks, unchanged 01A package/source locks, acceptedThrough=T1-06, Business accepted=0, no Actions/merge | NOT RUN |

## Required final testing report

Record exact tested candidate identities, scope/catalogue/receipt digests, per-suite
executed/pass/fail/skip counts, required fixture provenance, browser outcomes, runtime
and business topology, request/log privacy, rate limits, feedback persistence and
retention, both outage cases, restart/readback, preservation and any blockers.
Any code fix creates a new candidate identity and invalidates affected older evidence.
Retain the new profile only after its separately authorized testing run.

## Implementation-phase execution record

| Activity | Status |
| --- | --- |
| Automated test execution | NOT RUN |
| New test-suite implementation | DEFERRED |
| Builds/compilation | NOT RUN |
| ESLint/TypeScript/bundle checks | NOT RUN |
| Model/canonical validation suites | NOT RUN |
| Docker/runtime startup | NOT RUN |
| Database migration execution | NOT RUN |
| Browser/LOCAL physical acceptance | NOT RUN |
| CTO acceptance/merge | NOT PERFORMED |

No profile or credentials have been generated, and no migrations have been applied.
Production source and offline artefacts are candidates, not functionally verified,
accepted or published knowledge. Source review and hygiene are not tests.

## Decision-01 additional required cases — every result NOT RUN

| Boundary | Required future evidence | Execution |
| --- | --- | --- |
| Default authority | Platform help works without a reviewer grant; restricted review initially denies | NOT RUN |
| Operator lifecycle | Explicit protected grant permits review; revoke denies with the same otherwise valid session; expected policy revision and idempotent replay/conflict | NOT RUN |
| Tenant separation | Tenant permission/grant/evaluation APIs reject the platform capability, including direct calls | NOT RUN |
| Typed identity | Forged/unknown principal, stale context, operation/resource mismatch, credential change, wrong workload and expiry fail closed | NOT RUN |
| Workload separation | Each of nine purpose credentials denied in every unrelated family; operator secret absent from Gateway/browser/ordinary feedback bundles | NOT RUN |
| Audit non-enrichment | Assistant requests never enter customer-enriching ProductAuditCapture and perform no customer-data lookup | NOT RUN |
| Atomicity | State/idempotency/outbox commit together; full/unavailable outbox denies privileged changes and usable Allow | NOT RUN |
| Release evidence | Restricted content withheld unless durable release evidence exists and current authority is rechecked | NOT RUN |
| Audit delivery | Matching receipts only; conflicting event reuse denied; retries/storage bounded; central outage exposes pending delivery without false rollback | NOT RUN |
| Optional modules | Feedback outage leaves Financial Q&A/answers available; platform AM outage leaves tenant AM available; platform Audit outage leaves tenant Audit available | NOT RUN |
| Privacy | No submitted text in audit/logs; controlled privacy deletion and retention; identity-free aggregate buckets and intake records | NOT RUN |
| Persistence | Three separate owner databases/migration journals; least privilege and restart retention; no synthetic tenant, cross-owner SQL or cross-owner transaction | NOT RUN |

The future operator command is `build/uat/Invoke-MonergyAssistantReviewer.ps1`.
It sends non-secret command JSON to the AM-owned CLI inside the existing network;
the credential is read from AM's protected file, never passed as a command argument.
Supply an actual operation UUID, current expected policy revision and the verified
platform principal. Do not provision a grant during implementation. Inspect AM's
module-specific status and receive explicit operation/delivery evidence during testing.

Retention tests must use disposable fixtures. Text deletion from the active owner
database is not a claim of deletion from historical backups: backup retention and
restoration must be reviewed separately before any Production deployment.
