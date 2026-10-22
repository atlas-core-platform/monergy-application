# Monergy local UAT — Docker Desktop

This profile runs the shared administration workspace and 13 service boundaries on your computer. Access Management, Customer & Identity and Audit use separate PostgreSQL databases for each tenant. Policies, imported identities, sessions, delivery receipts and canonical access audit persist across Stop/Start.

Business reference adapters remain illustrative. AI remains a scaffold. Consent, Evidence, Job Management and Document Intelligence provide the guarded LOCAL/UAT customer-processing journey; Search and Reporting enforce current AM decisions before their reference business operations. The processor accepts structured text and the financial information CSV and bounded XLSX templates, candidate facts require explicit customer review before updating Financial Profile, and runtime business data remains non-production. Reporting reference data is volatile. This profile is not Production acceptance or a replacement for the separately verified D11 persisted-reporting profile.

## Start on Windows

Prerequisites: Git, PowerShell (Windows PowerShell 5.1 or PowerShell 7), Docker Desktop running **Linux containers**, and permission to pull the pinned public build images/packages. No host Node.js, .NET SDK or PostgreSQL installation is required. Allow enough Docker resources for 13 .NET processes and the build; 8 GB Docker memory is a practical starting allocation, subject to measurements on your machine.

In your `monergy-application` checkout:

```powershell
git switch main
git pull --ff-only
.\build\uat\Invoke-MonergyUat.ps1 -Action Start
```

Open **http://127.0.0.1:4173/access**. The first build downloads the pinned toolchains and may take several minutes. Start waits for service readiness; it does not silently recreate retained data.

Show your local administrator key only when you need to sign in:

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Credentials -Tenant T001 -Actor A900
```

Use tenant **T001** and that key. T002 is an isolated second tenant with a different key. A900 is the initial TenantAdmin. A100 and A300 are local fixture members with **no initial business permissions**; an administrator must intentionally assign any business role. `Credentials -Actor A100` or `-Actor A300` selects those local accounts. Keys authenticate only these local reference identities; CSV onboarding creates durable owner identities, not identity-provider accounts or email invitations.

Keys are generated once, stored in an access-restricted local profile, and excluded from Git and image build context. Do not share the `.artifacts/uat` profile directory. Do not delete its credentials while retaining the corresponding database volume. Refreshing the browser requires signing in again; browser storage and URLs never contain the bearer session.

## Administration UAT journey

1. **Permissions → Roles:** create a permission such as `search.query.execute` with Tenant scope, then a role containing it. TenantAdmin alone does not grant business access.
2. **Users:** assign the role to a person. A membership change invalidates their existing sessions; sign in again to test the new policy. Local business browsing currently shares the administrator workspace session. You can assign a business role to your administrator for browser testing; programmatic acceptance separately verifies non-admin actors.
3. **Groups:** organize people. A group never grants permissions by itself.
4. **Users → Create user / Import users:** add one user with email and an optional business role or groups. For a team, download the CSV template, preview invalid and valid rows, stage, process and activate. No access is granted while an import is pending. The C&I owner stores the identity receipt; activation is all-or-none.
5. **Resource access:** test a Resource-scoped permission with one resource granted and another denied. Resource grants complement the assigned role; they cannot create an unassigned capability.
6. **Security & Sessions:** review issued/revoked/expired sessions; revoke all sessions for a member. Revocation immediately advances the access version and later marks durable session rows via delivery. A stored “Issued” label is not proof of current authorization.
7. **Access activity:** see the resulting operation, actor, policy revision and canonical Audit evidence. Delivery is asynchronous; refresh after a committed change.
8. **Operator tools at `/operations` → Connected services / Release readiness:** inspect all 13 health results, actual implemented scope and explicit Production blockers.
9. **Isolation:** sign out, connect to T002 with its own key and confirm that T001 people, roles and audit activity are absent. A T001 session cannot be used with a T002 header.
10. **Restart:** Stop and Start, then verify the policy and audit history remain. Read-only Verify confirms all 13 service health endpoints; it does not mutate your UAT policies.

Access Management has a dedicated navigation menu and guide. Onboarding history is under Users. Platform Search, Reports, Evidence and System Expert are absent from this module; the platform workspace remains at `/`. Operator diagnostics remain separately available at `/operations`.

Search and Reports use the in-memory current session across internal navigation. The server checks tenant, customer ownership, actor/session binding and current permission on each request. Reference customer `reference-customer` belongs to T001; `other-customer` belongs to T002. Sample business data is available only where its reference owner supplies it; no fallback crosses tenants. Unadapted contracts fail closed.

## Operations

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Status
.\build\uat\Invoke-MonergyUat.ps1 -Action Verify
.\build\uat\Invoke-MonergyUat.ps1 -Action Stop
.\build\uat\Invoke-MonergyUat.ps1 -Action Start
```

Stop removes containers and their network, preserving the named PostgreSQL volume and matching credential profile. Reset is explicitly destructive:

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Reset -ConfirmReset
```

Use `-Profile name -Port 4183` on the first preparation to create a separate instance. Continue specifying the same profile thereafter. Its stored port is authoritative. Profile directories and instance-specific Compose project names prevent another instance from adopting its retained volume. A new bootstrap rejects unexpected databases or a different instance marker.

If startup fails, use Status first. The command reports failed containers without printing credentials. With the instance project name shown by Docker Desktop, inspect the failing container there. Bootstrap failures usually indicate migration failure or a mismatched retained volume/profile. Do not “fix” them by granting runtime users DDL rights or deleting data. A port conflict requires choosing a new profile/port or freeing the current port.

If Windows reports `Access is denied` opening `compose.env` or another generated profile file, update the checkout, then repair the selected profile using the same Windows account that created it:

```powershell
git pull --ff-only
.\build\uat\Invoke-MonergyUat.ps1 -Action RepairPermissions
.\build\uat\Invoke-MonergyUat.ps1 -Action Start
```

RepairPermissions changes only filesystem permissions and verifies file reads. It never reads or rewrites credentials, changes file owners, rotates keys, invokes Docker, or removes a database volume. On Windows the launcher applies explicit SID-based access for the current operator (full control) and local SYSTEM (read access); unrelated users receive no grant. Existing profiles are also repaired before Prepare/Start reads their keys. Status, Verify, Credentials and Stop do not modify profile permissions. If repair itself fails, preserve the profile and capture the file ACL and error for diagnosis; do not reset the environment or grant Everyone access. Start checks Compose configuration before building or starting containers.

## Trust and delivery boundaries

Only workspace port 4173 is published, bound to host loopback. PostgreSQL and owner endpoints have no published ports. Each service runs as its own process/container with its own mounted configuration. Service containers share the workspace network namespace so existing LOCAL-only HTTP adapters continue using loopback. The gateway forwards an explicit browser route/header allowlist; it never forwards workload credentials or exposes internal provisioning/event ingress.

Bootstrap uses owner credentials in a one-shot job. Running owners use separate, tenant-specific runtime credentials without DDL; each delivery worker receives only its tenant outbox credential and destination tokens. Containers use a read-only filesystem, temporary bounded `/tmp`, dropped capabilities and no Docker socket. On Linux, the process UID matches the operator so restricted mounted configuration is readable. Windows Docker Desktop uses UID 1654.

The AM runtime source is an immutable dependency under `dependencies/`, pinned to its exact green commit/run, whole-archive SHA-256 and every Git blob hash. The build verifies it before extraction. This LOCAL/CI runtime dependency is distinct from the verification-only `build/am05/fixtures` source.

## Automated acceptance and Production limits

MWP-03 Midnight is a review candidate on `work/mwp-03-monergy-midnight`, not a
released change. After its hosted checks pass, review it locally by fetching and
switching to that branch in a clean checkout, then running the existing `Start`
command. Start rebuilds the UI while retaining the selected profile's data and
credentials; no Reset is needed. Verify that direct links and reload show only
the connection screen before authentication, then review Users, roles, permissions,
groups, resources, activity, sessions, sidebar collapse, drawers and sign-out.
See [the candidate record](../governance/MWP-03-midnight-access-candidate.md) for
scope, evidence and remaining acceptance checks.

The single manually dispatched **Candidate readiness** workflow selects Docker UAT only when the complete candidate delta affects runtime, application, contract, shared-platform or UAT composition paths. It creates its own disposable `ci` profile, builds the actual containers, checks all 13 boundaries for allow/deny/isolation, calls the actual owners, proves Job Management audit delivery, tests revocation with delivery stopped, restart persistence, disablement, owner outage recovery and browser session/audit/service journeys. It uploads only allowlisted credential-free results and signed-in screenshots. The destructive Python acceptance script requires an explicit disposable instance identifier; never point it at operator UAT data. Tenant Access owner integration remains an impact-selected job inside the same workflow.

The runtime image verifies onboarding storage with a non-root UID during its build.
For onboarding profiles, bootstrap repeats the create/replace/readback check against
the mounted volume before provisioning. It uses temporary probe files and preserves
existing registry, setup and database state. A permission failure stops startup;
do not delete an operator profile or its volume to recover access. Failure diagnostics
are redacted before upload as `uat-diagnostics.log` in the acceptance artifact.

A separate Windows job exercises the actual launcher under Windows PowerShell 5.1 and PowerShell 7. It verifies private ACLs and native Docker Compose file reads, deliberately denies access to existing credentials/configuration, and proves that repair preserves file contents, write times and owners. These checks do not start Linux containers on the Windows runner; the Docker process acceptance job remains Linux-based.

Production remains blocked until real IdP/MFA/invitation delivery, workload identity/TLS, event transport, managed secrets, provider/Consent/business consumers, managed database backup/restore, monitoring, HA/DR, capacity and release approval are implemented and accepted. The new local gateway, tenant adapters and bootstrap reject an unsupported Production environment. Readiness of local processes does not upgrade D01–D16 or SG acceptance.

## Upload malware checks

Onboarding starts the pinned ClamAV security sidecar. Evidence upload readiness requires current engine/signature metadata; rejected or unavailable checks cannot accept an upload. See [Document security setup and live acceptance](Document-Security.md) for resource needs, retained definitions and the authored live-engine test. Docker/engine acceptance has not run in the implementation workspace.

T1-57 XLSX template instructions and limits: [Financial-Information-XLSX.md](Financial-Information-XLSX.md). Existing original-byte malware gating and explicit review/adoption apply.

T1-58 adds bounded printed-English Insurance Policy OCR in PNG, upright JPEG and single-page scanned PDF. Required labels are Policy Date, Sum Assured, Premium/Premium Amount and explicit matching currency. Both literal source figures require review; this does not establish active cover, asset/cash value or premium frequency. See build/governance/t1-58-insurance-policy-ocr.md for exact limits and verification.

T1-59 adds bounded printed-English Investment Statement OCR in PNG, upright JPEG and single-page scanned PDF. Required labels are Statement Date, Purchase Value, Current Value and explicit matching currency. Both literal dated figures require independent review; this does not establish live prices, returns, gains, verified positions or asset valuation. See build/governance/t1-59-investment-statement-ocr.md for exact limits and verification.

T1-60 adds bounded printed-English Loan Sanction Letter OCR in PNG, upright JPEG and single-page scanned PDF. Required labels are Sanction Date, Sanctioned Amount/Sanction Amount, EMI/EMI Amount and explicit matching currency. Both source terms require independent review; no verified offer, disbursement, current debt or repayment status is established. Prior reviewed outstanding-balance figures retain their distinct meaning. See build/governance/t1-60-loan-sanction-ocr.md.

T1-61 supports printed-English Property Sale Deed OCR in PNG, upright JPEG and single-page scanned PDF. An exact early title, Deed Date/Sale Deed Date, Purchase Value and explicit supported currency are required. Purchase value requires explicit review and does not verify ownership, authenticity or current value. The product ASSET source category maps to the canonical PROPERTY owner type for these exact extraction versions; Passport/source history/Search/basic Reports retain the asset-source presentation and original provenance. See build/governance/t1-61-property-sale-deed-ocr.md.

## T1-62 property registration document OCR

Supported printed-English property registration PNG/JPEG/single-page PDF yields one explicitly stated purchase figure dated by registration, with review before adoption. Registration date is not purchase date. See [bounded verification receipt](../governance/t1-62-property-registration-ocr.md). Full property records and integrated physical UAT remain open.

## T1-63 vehicle purchase invoice OCR

Supported printed-English vehicle purchase invoice PNG/JPEG/single-page PDF yields one explicitly stated purchase figure dated by invoice, with review before adoption. The canonical owner type is VEHICLE; source views retain ASSET presentation. See [bounded receipt](../governance/t1-63-vehicle-purchase-ocr.md). Full vehicle records, current valuation/ownership verification and integrated physical UAT remain open.

## T1-64 financial source-category compatibility

Insurance/Tax review now maps to canonical owner types and back for Passport/history/Search/basic Reports. The shared boundary preserves property/vehicle behavior; untyped generic ASSET adoption remains unsupported. See [bounded receipt](../governance/t1-64-financial-source-category-boundary.md). Physical/integrated UAT remains open.

## T1-65 reviewed generic asset types

Current and recovered review require an explicit Property/Vehicle choice for selected generic text/CSV/bounded XLSX asset figures. The Gateway validates choices against selected owned sources. Financial Profile retains the canonical source type across operation IDs/history; current and historical customer views display it. See [bounded receipt](../governance/t1-65-reviewed-asset-source-types.md). Other asset kinds, full asset records, ownership/current value verification and physical/integrated UAT remain open.

## T1-66 customer-reviewed loan records

Financial information > Loan information supports explicit creation/readback of source-linked loan terms, including lender/type, sanction/outstanding/as-of, rate/EMI/tenure and start/end dates. Financial Profile migration0003 adds append-only records and scoped operation receipts. Run normal owner bootstrap migrations before using the physical adapter. Current Self read/evidence/update authority is required. Unknown or conflicting requests fail closed; uncertain saves replay the same request. [Receipt](../governance/t1-66-reviewed-loan-records.md). Editing/lifecycle, central Audit Service integration, physical migration/UAT and full financial truth remain open. No aggregate debt or lender verification is implied.

T1-66 owner routes also require the existing operator customer-to-tenant binding before PDP checks; tenant-colliding customer IDs cannot cross the source boundary.

## T1-67 reviewed loan corrections and history

Run owner bootstrap migrations through0004 before using the physical loan adapter. Customers can explicitly correct saved terms with version checks; prior versions, original source/currency and creation receipts remain immutable. Current lists show latest terms; history has bounded older-version pages. Current Self read/document/update access and fresh exact-version confirmation remain required. Stale edits require refresh/review; unconfirmed saves retry the identical operation/body/version. [Receipt](../governance/t1-67-reviewed-loan-corrections.md). Closing/lifecycle, central Audit integration and physical/integrated UAT remain open.

## T1-68 reviewed loan status

Review loan status records a customer-reported Active or Closed state with an explicit date; Active can reopen a previously Closed record. Existing records remain Status not reviewed until explicit review. Existing append-only migrations0003/0004 retain status/history; no new migration is required. Closing does not clear a balance/EMI, change contractual dates or verify lender repayment. Corrections preserve status/date; stale or uncertain requests follow the existing exact-version retry/refresh rules. [Receipt](../governance/t1-68-reviewed-loan-lifecycle.md). Central Audit, broader lender/financial truth and physical/integrated UAT remain open.

## T1-69 reviewed investment records

Financial information > Investment source figures includes customer-reviewed holding records with explicit name/institution/type, purchase/current values and value-as-of date. Both values use an exact reviewed investment source's currency; source figures are not autofilled. Run owner bootstrap migrations through0005 for the physical adapter. Current Self read/document/update authority, ready owned customer and fresh owner readback are required. Unconfirmed saves retain their exact request across close/reopen and missing current source. [Receipt](../governance/t1-69-reviewed-investment-records.md). Corrections/lifecycle, institution verification, portfolio/quantity/pricing/net worth, central Audit and physical/integrated UAT remain open.

## T1-70 reviewed investment corrections and history

Run owner bootstrap migrations through0006 for physical investment corrections. Editing appends an expected-version correction and preserves original source/currency/creation receipt. Latest lists show current values; read-only history retains every version in bounded pages. Current Self read/document/update controls and exact-version confirmation remain. Stale edits require refresh/review; uncertain submissions retry the same operation/body/version. [Receipt](../governance/t1-70-reviewed-investment-corrections.md). Lifecycle, provider/portfolio truth, central Audit and physical/integrated UAT remain open. Unchanged T1-69 source/document regressions are carried forward.

## T1-71 reviewed income records

Financial information > Income supports explicit reviewed records with payer/type, period total, frequency, reported financial-year label and reporting dates. Exact reviewed income source/currency and owner CUSTOMER_REVIEWED status remain; no amount autofill, annualisation or year/calendar inference. Run owner migrations through0007 for physical storage. Current Self/read/document/update and ready owned-customer controls plus fresh immutable owner readback apply. Uncertain saves retain their request across close and missing current source. [Receipt](../governance/t1-71-reviewed-income-records.md). Corrections/history, reconciliation, verification, comparable snapshots, central audit and physical/integrated UAT remain open.

## Durable original documents

Onboarding profiles can opt into tenant-specific PostgreSQL Evidence databases and versioned S3 storage through `-EvidencePersistenceConfigurationFile`. See [Evidence persistence](Evidence-Persistence.md) for the configuration, provisioning and retained-profile behavior. Existing temporary profiles remain supported.
