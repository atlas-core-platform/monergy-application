# Monergy local UAT — Docker Desktop

This profile runs the shared administration workspace and 13 service boundaries on your computer. Access Management, Customer & Identity and Audit use separate PostgreSQL databases for each tenant. Policies, imported identities, sessions, delivery receipts and canonical access audit persist across Stop/Start.

Business reference adapters remain illustrative. Consent and AI are scaffolds; Document Intelligence and Job Management have no connected business consumers. Search and Reporting enforce current AM decisions before their reference business operations. Reporting reference data is volatile. The VS-02 simulator is deliberately unavailable in this profile until its complete tenant-aware worker journey is connected. This profile is not Production acceptance or a replacement for the separately verified D11 persisted-reporting profile.

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
2. **People:** assign the role to a person. A membership change invalidates their existing sessions; sign in again to test the new policy. Local business browsing currently shares the administrator workspace session. You can assign a business role to your administrator for browser testing; programmatic acceptance separately verifies non-admin actors.
3. **Groups:** organize people. A group never grants permissions by itself.
4. **Imports:** download the CSV template, preview invalid and valid rows, stage, process and activate. No access is granted while an import is pending. The C&I owner stores the identity receipt; activation is all-or-none.
5. **Resource access:** test a Resource-scoped permission with one resource granted and another denied. Resource grants complement the assigned role; they cannot create an unassigned capability.
6. **Sessions & security:** review issued/revoked/expired sessions; revoke all sessions for a member. Revocation immediately advances the access version and later marks durable session rows via delivery. A stored “Issued” label is not proof of current authorization.
7. **Access activity:** see the resulting operation, actor, policy revision and canonical Audit evidence. Delivery is asynchronous; refresh after a committed change.
8. **Connected services / Release readiness:** inspect all 13 health results, actual implemented scope and explicit Production blockers.
9. **Isolation:** sign out, connect to T002 with its own key and confirm that T001 people, roles and audit activity are absent. A T001 session cannot be used with a T002 header.
10. **Restart:** Stop and Start, then verify the policy and audit history remain. Read-only Verify confirms all 13 service health endpoints; it does not mutate your UAT policies.

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

## Trust and delivery boundaries

Only workspace port 4173 is published, bound to host loopback. PostgreSQL and owner endpoints have no published ports. Each service runs as its own process/container with its own mounted configuration. Service containers share the workspace network namespace so existing LOCAL-only HTTP adapters continue using loopback. The gateway forwards an explicit browser route/header allowlist; it never forwards workload credentials or exposes internal provisioning/event ingress.

Bootstrap uses owner credentials in a one-shot job. Running owners use separate, tenant-specific runtime credentials without DDL; each delivery worker receives only its tenant outbox credential and destination tokens. Containers use a read-only filesystem, temporary bounded `/tmp`, dropped capabilities and no Docker socket. On Linux, the process UID matches the operator so restricted mounted configuration is readable. Windows Docker Desktop uses UID 1654.

The AM runtime source is an immutable dependency under `dependencies/`, pinned to its exact green commit/run, whole-archive SHA-256 and every Git blob hash. The build verifies it before extraction. This LOCAL/CI runtime dependency is distinct from the verification-only `build/am05/fixtures` source.

## Automated acceptance and Production limits

`Local Docker UAT acceptance` runs **after pushes to main** and on manual dispatch. It creates its own disposable `ci` profile, builds the actual containers, checks all 13 boundaries for allow/deny/isolation, calls the actual Search owner, tests revocation with delivery stopped, restart persistence, disablement, owner outage recovery and browser session/audit/service journeys. It uploads only allowlisted credential-free results and a signed-in workspace screenshot. The destructive Python acceptance script requires an explicit disposable instance identifier; never point it at operator UAT data. Existing AM owner integration checks continue covering durable CSV onboarding, lost acknowledgements and migration grants.

Production remains blocked until real IdP/MFA/invitation delivery, workload identity/TLS, event transport, managed secrets, provider/Consent/business consumers, managed database backup/restore, monitoring, HA/DR, capacity and release approval are implemented and accepted. The new local gateway, tenant adapters and bootstrap reject an unsupported Production environment. Readiness of local processes does not upgrade D01–D16 or SG acceptance.
