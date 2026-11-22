# Assistant 01B Gate D harness — prepared, not executed

This new harness complements the dated Gate A/B test receipts. It is not an
acceptance receipt. Do not run it before the final source/build/isolated integration
gates pass and all four corrected candidates are frozen. No existing profile,
credential or named volume is a fixture. No browser test replaces owner transport,
authorization or PostgreSQL with a mock. Chrome tracing, video and screenshots are
disabled; the custom reporter exports only fixed scenario IDs, statuses and durations.
Playwright can still write raw error-context/page snapshots. All such artifacts,
including transient output and last-run state, are confined to a unique directory
under the matching private fixture's `playwright-raw` folder. They are excluded
from evidence and archives. The wrapper prepares operator/System-only ACLs (0700
on Linux); direct configuration validates the same directory and its protected
parent before execution. Discovery creates/reads no fixture or artifact directory.
Recreated output directories inherit private permissions from that parent.

## Start and fixture preparation

Inspect `.artifacts/uat/assistant01b`, port 4350 and Docker inventory first. A name
or port collision requires a different clearly named profile, never Reset. Record
the new profile's instance ID and exact candidate identities outside the source tree.
Retained environment inventory precedes profile creation.

On this Windows host, the installed Chrome resolves the LOCAL hostnames, but
Playwright's Node API transport initially failed with `getaddrinfo ENOTFOUND` for
`platform.localhost`. The phase wrapper therefore opts into a process-only DNS
preload for exactly `platform.localhost`, `t001.localhost`, `t002.localhost` and
`unknown.localhost`, resolving their IPv4 sockets to `127.0.0.1`. The original URL
and HTTP Host remain unchanged, so the Gateway must still reject the unknown
host. All other names use the original resolver. Both explicit execution flags
are required; discovery is inert. Existing `NODE_OPTIONS` are preserved and all
temporary options are restored after the phase. This changes no hosts file,
system DNS, browser host rules, product routing or runtime image. The focused
resolver tests and the actual hostname-based public readiness check are recorded
in the `harness-local-dns-*` evidence files; they do not replace browser acceptance.

```powershell
# Run only after Gates A–C PASS and root's physical-start authorization.
pwsh -File build/uat/Invoke-MonergyUat.ps1 -Action Start -Profile assistant01b -Port 4350 `
  -EnableOnboarding -EnablePersonaLogin -EnablePersonaTesterActivation -EnableAssistant `
  -SystemExpertRoot E:/Projects/Atlas/monergy-system-expert/.codex-worktrees/assistant-01b

pwsh -File build/uat/assistant-01b.fixture.ps1 -Profile assistant01b `
  -ExpectedNewInstance '<new profile instance ID>' -ConfirmNewProfileAfterGateC

$fixture = '<absolute protected .artifacts/assistant-01b-private-fixtures/browser-assistant01b/fixture.json>'
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase activate -ConfirmGatesABC -Visible
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase setup -ConfirmGatesABC -Visible
# Launch auth-live-expiry in a separate protected background runner now. It keeps
# a real A700 browser/session alive for the owner-returned thirty minutes while
# baseline/reviewer/owner-fault phases run. Do not stop Runtime/disable/restart until
# that natural expiry case finishes. Do not run auth-capture as well.
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase auth-live-expiry -ConfirmGatesABC -Visible
pwsh -File build/uat/assistant-01b.snapshot.ps1 -Profile assistant01b -Label before-browser -ConfirmGatesABC
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase baseline -ConfirmGatesABC -Visible
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase reviewer -ConfirmGatesABC -Visible
# The existing deterministic Cohort O fixture is copied to this new profile only.
# Its key, TLS material and raw receipts remain protected and are not evidence.
pwsh -File build/uat/assistant-01b.qa-provider.ps1 -Action Prepare -Profile assistant01b `
  -ExpectedNewInstance '<new profile instance ID>' -ConfirmGatesABC
pwsh -File build/uat/assistant-01b.qa-provider.ps1 -Action Start -Profile assistant01b `
  -ExpectedNewInstance '<new profile instance ID>' -ConfirmGatesABC
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase qa-setup -ConfirmGatesABC
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase unknown-outcome -ConfirmGatesABC
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase owner-faults -ConfirmGatesABC
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase workspace-switch -ConfirmGatesABC
# Run rates last, after the lifecycle phases below.
```

The fixture includes all ten T001 tenant reference personas, A950 platform and
T002 A900/A100. Each receives a distinct random password in an ACL-protected file.
The activation phase obtains a fresh owner-issued protected LOCAL welcome link,
opens the real welcome UI, sets that person's password and verifies no session is
automatically established. It never uses a tester credential as a sign-in session,
falls back to password recovery, or rotates an existing credential. A reused
fixture is an explicit stop, not permission to reset it.
Before requesting a Platform welcome, the harness independently requires the
actual credential owner's `activated=false` status. Already-active or unknown
status fails before issuance because the accepted Platform owner permits password
replacement through a new activation ticket. Partial runs must not rotate it.

The setup phase reviews the existing provisioned organization/access on the real
setup page. It does not modify the Default Access Pack. If its current state cannot
be completed with the existing reviewed setup path, report the exact precondition.
Do not fabricate a successful setup receipt.

## Physical phases and orchestration

The `owner-faults` phase invokes only the protected new-profile owner-fault helper,
with restore in `finally`, after an actual reviewer grant and successful read.
The other fault/restart phases require separately recorded physical actions confined
to the new profile. Never stop all AM/Audit/AI business containers to claim their
optional database boundary was isolated. The lost-reply phase reaches the real owner
with `route.fetch` before dropping a response; it does not fabricate owner behavior.

| Phase | Physical precondition | Browser verification |
| --- | --- | --- |
| baseline | 14 business services ready; Runtime separately ready; no reviewer grant | 11 persona logins, product/platform guidance, wrong hosts/tenant, public hiding, keyboard/lazy/clear, safe labels, explicit suggestion/replay, privacy rejection, logout |
| auth-live-expiry | New A700 normal login; separate browser process remains alive for actual thirty-minute expiry | Canary remains before expiry, live drawer/session clears at real deadline, old status/answer requests return401; no changed clocks, SQL or TTL settings |
| auth-capture / auth-expiry | Alternative only when the long-lived browser cannot run; capture early and read back after real returned expiresAt | Actual server expiry only; browser timer-driven clear is explicitly not claimed by this fallback |
| credential-recovery | Baseline complete; new-profile A800 only | Separate welcome browser sets own new password using real protected recovery; old live drawer clears on401, old password fails and new normal login works; protected journal/fixture retain the new credential |
| reviewer | Baseline suggestion exists; current AM operator status available | Default denial, explicit real A950 grant, restricted list and resource-less history, real revoke and same-session denial/content removal |
| unknown-outcome | Baseline topic exists; owner and AM healthy | Real submission/review commits before reply is dropped; UI preserves operation/payload, retry returns one durable outcome without revision/expiry renewal |
| owner-faults | Baseline reviewed topic; new-profile owner-fault helper | Actual pre-fault grant/read, independently isolated optional runtime-role failures, denied or durably pending mutation, ordinary help/login, restoration/readback; pending Audit event becomes Accepted |
| workspace-switch | Active new-profile A200 Customer and normal A900 administrator | Existing owner membership API temporarily adds only TenantAdmin while preserving Customer role/assignments, real chooser and owner reauthorization, then optimistic owner restoration in finally |
| rates | A850 has not submitted suggestions previously; limiter elapsed or restart recorded | At most 20 questions/minute, five submissions/hour, Retry-After, T002 namespace independent |
| qa-setup | New protected deterministic QA provider prepared/started | Actual Platform configuration, Customer Request Access/A900 approval, fresh session, explicit question permission, configured HTTP200 INSUFFICIENT_DATA with zero claims and actual LOCAL provider call; ordinary Ask causes zero provider-call drift |
| qa-continuity | Provider configuration and Customer approval already succeeded; provider transport correction deployed to the same profile | Read-only A950 exact safe-provider readback, fresh A100 login, existing Fulfilled access, Ask provider drift zero, configured Q&A HTTP200 INSUFFICIENT_DATA/zero claims and exactly one real LOCAL provider call; no provider PUT, access submission or approval |
| runtime-outage | Stop only this profile's knowledge-runtime | Help unavailable; customer and platform login/session remain valid |
| feedback-outage | Only ai_feedback runtime DB access unavailable, other AI routes healthy | Answers still work; submission has no false receipt; valid session remains |
| am-outage | Only am_platform store unavailable; tenant AM healthy | Platform reviewer unavailable; ordinary product/platform help and tenant login work |
| audit-outage | Only audit_platform store unavailable; tenant Audit healthy | Ordinary help/login remain usable; retain pending evidence without claiming rollback |
| restart | Restore faults, snapshot owner rows/counts/receipt hashes, restart same profile | Original passwords, both audiences recover, blank conversation |
| disabled | Disable optional module using existing launcher; same profile/state preserved | Customer/platform sign-in works; Ask Monergy absent |
| reenabled | Re-enable same profile against exact clean Expert candidate | Both audiences recover; no conversation history |

The finite normal sequence is activate, setup, baseline, reviewer, qa-setup,
unknown-outcome, owner-faults, workspace-switch, runtime-outage, restart, disabled,
reenabled, credential-recovery, rates, plus auth-live-expiry running alongside the
early phases: **59 prepared scenarios**. The supported workspace fixture uses
only normal A900 owner APIs; if its original membership precondition fails, record
that explicitly and do not overwrite it. The separate
feedback-outage/am-outage/audit-outage phases remain diagnostic alternatives and
need not duplicate the three stronger owner-faults cases. All phases together
discover 68 scenarios (including two fallback auth capture/readback cases and the explicit continuity-only resume);
discovery is not execution or a pass count.

The original `qa-setup` attempt completed its governed provider PUT and Customer
Request Access approval before encountering a real missing trusted-host transport
defect in the provider client. Preserve that historical failed attempt and its
successful setup operations. Do not repeat `qa-setup`, provider configuration,
access requests or grants to obtain a green result. After the bounded product
correction is frozen and deployed, run the new one-case `qa-continuity` phase.
It must read the existing safe provider view against the exact protected fixture
metadata, then prove current access and continuity using a fresh normal login.
This one required resumption result supplements the historical attempted setup;
it must not relabel that original attempt as passing. No database, provider state
or credentials are reset. The normal 59-case plan remains the original plan;
this actual interrupted run records one additional continuity-only case.

```powershell
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase qa-continuity -ConfirmGatesABC
```

The new natural-expiry test has its own dynamically bounded deadline, at most31
minutes, because the accepted C&I schema gives normal sessions a fixed30-minute
lifetime. It is not a timeout increase for a failing test. The A700 session and
owner-returned expiry stay in the private fixture. Running the fallback capture
phase also makes live expiry refuse a duplicate capture; choose one path. The
fallback must wait for the real expiry and cannot claim browser timer evidence.
Complete this live phase before runtime-outage or any launcher lifecycle action;
an unavailable module is not evidence that a session naturally expired. The example
runner command must be launched in a separate nonblocking process when overlapping
other phases, with only its sanitized stdout in evidence and raw artifacts private.
Platform sessions have an accepted fixed8-hour lifetime; no short-TTL option
exists. Their expiry evidence remains the separate integration tests, not an
unexecuted eight-hour browser claim.

A800 recovery is separate from initial activation. It uses only the existing
Development tester recovery-link endpoint and the user's `/welcome` password UI.
Both possible passwords, request identity and progress are flushed to a protected
exclusively created journal before mutation; a concurrent invocation cannot replace
it and dispatch another recovery. Later atomic updates verify the same request and
prior state. Fixture sibling files inherit validated private parent permissions.
Confirmed recovery updates only this new profile's
fixture. `ConfirmationDispatched` is durable before submission; only HTTP200 plus
the exact `{recovered:true}` owner receipt advances to `PasswordConfirmed`. A lost
or malformed receipt remains indeterminate. If a response/write fails, preserve
that journal and stop: do not silently
repeat recovery, rotate again, or overwrite a concurrent credential change. No
retained credential, membership, Default Access Pack or database is altered.

The membership helper persists a protected baseline, retains A200's exact existing
Customer businessRoleId/active state, and restores only the temporary TenantAdmin
flag through the same owner/current policy version. It never edits a Default Access
Pack, role permissions, customer relationship or database row directly. A concurrent
unrelated membership change makes automatic restoration fail explicitly. The modal
drawer masks workspace controls, so the physical flow honestly closes the drawer
before the actual switch. It proves fresh owner authorization and blank reopened
help; in-flight context cancellation is separate React evidence, not that click flow.

QA continuity is deliberately an empty-profile result. No document, OCR adoption,
direct financial SQL fact or non-empty SOURCES result is created or claimed. It
demonstrates that an authenticated, explicitly permitted real QA request remains
functional while optional Assistant owner stores fail. Ask Monergy itself must
leave the deterministic provider counter unchanged. This LOCAL contract does not
approve a Production provider or model.

During each optional owner database fault, the same scenario additionally requires
HTTP200 aggregate readiness with14 business services, normal A900 authentication,
the tenant AM administration context and the real tenant Audit log read. This
distinguishes platform-module isolation from an unverified assumption that the
tenant service remains usable. These are read-only existing owner routes.

Example runner after each independently verified fault or lifecycle action:

```powershell
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase runtime-outage -ConfirmGatesABC
# Restore the same Runtime container, then independently recheck readiness.
pwsh -File build/uat/assistant-01b.run.ps1 -FixtureFile $fixture -Phase restart -ConfirmGatesABC
pwsh -File build/uat/assistant-01b.snapshot.ps1 -Profile assistant01b -Label after-restart -ConfirmGatesABC
```

After **each** launcher restart/disable/re-enable, reapply only
`assistant-01b.qa-provider.ps1 -Action Start` with the same profile/instance before
browser continuity checks. The launcher removes the QA sidecar/trust overlay as an
orphan. Never run Prepare again or regenerate provider keys, passwords or TLS.
Compare owner restart snapshots before browser reviewer grants add new policy/audit
history. Customer restart/disabled/reenabled cases require actual configured QA200
INSUFFICIENT_DATA as well as ordinary sign-in; an absent fixture is not a pass.

Run read-only privacy checkpoints after activation, after baseline, after owner
faults, **before every Compose Stop/recreation/disable**, and after final re-enable:

```powershell
pwsh -NoProfile -File build/uat/assistant-01b.privacy.ps1 -Profile $Profile `
  -ExpectedNewInstance $Instance -Label '<unique lowercase checkpoint>' -ConfirmGatesABC
```

The helper binds the exact new project and scans each current container from its
actual creation time, including bootstrap logs before browser fixture creation.
It compares protected credentials and synthetic content only in memory, writes
counts/categories/hashes, and fails on missing required containers or incomplete
bounded log coverage. Reports are immutable at
`.artifacts/assistant-01b-testing/physical/<profile>/privacy-<label>.json`.
Reapply the preserved QA overlay before disabled/final checkpoints when configured.
Removed-container logs cannot be recovered: a scan after recreation does not replace
the required checkpoint before it. Unknown/unretained transient tickets have pattern
coverage, not a fabricated claim of exhaustive exact-value comparison.

Review grants use only `Invoke-MonergyAssistantReviewer.ps1`; its operator key
remains in the AM-only secret file. The normal platform browser session does not
receive the operator key or the issuer context. The reviewer phase uses the real
operator status policy revision and always attempts revoke in `finally` after a
successful grant. A failed revoke is a failure and requires owner recovery; it
must never be reported as successful.

## Complete disposition ledger

Every row below needs a final result/evidence link in the testing report. Existing
Gate A/B receipts must match the final candidate or have an explicit unchanged-code
applicability note. None of the unexecuted browser cases below is pre-labelled PASS.

| Handoff coverage | Gate A/B evidence | Additional Gate D evidence required |
| --- | --- | --- |
| Accepted knowledge, all 20 entries, retrieval, audience, Runtime input/credentials/readiness | Expert full/focused contract/artifact suites; immutable digests | Real two-audience question set, five restricted questions/mixed questions, Runtime health and internal-only port |
| Gateway auth/boundaries/validation | Gateway/owner tests including all mutation shapes and nine workload purposes | Trusted hosts, normal sessions, tenant mismatch, default review denial; expiry/credential-change physical fixture if not reused from exact identity owner tests |
| Browser projection/UI shells/lifecycle | React suites, race/abort tests | All persona scenarios, keyboard/close/lazy/storage; actual workspace switch when a multi-workspace fixture exists; explicitly record unavailable fixture |
| Limits/counters | Rate/owner PostgreSQL tests | rates phase; owner query of bounded identity-free categories/counts; no raw text/identity in aggregates |
| Explicit suggestions/privacy/retries | Real transactions + lost response/COMMIT acknowledgement tests | Explicit checkbox and empty suggestion entry, real persistence/replay, definite rejection, no logout |
| Reviewer authority/lifecycle/resolution | AM and owner actual PostgreSQL/HTTP tests, revoke races, unpublished resolution denial | Protected grant/revoke with same actual session, UI read; live resolution remains blocked without real approved publication (positive isolated synthetic proof is not publication) |
| Resource-less/AM audit recovery | Actual PostgreSQL/HTTP capacity, paused delivery, audited recovery, stable receipts | Real new-profile outbox/central receipt reconciliation; if exhausting retries physically, use only owned new-profile event IDs and owner recovery routes, not fake gap IDs |
| Retention/owner persistence | Real migrations/grants/30/180/90 day fixtures | Separate owner DB/journal/runtime roles in new profile; read-only privileges; pending evidence and preserved record/expiry hashes before/after restart |
| Optional failures | Malformed-config, host isolation, owner outage tests | Runtime + each optional owner-store fault, unrelated host/login/readiness remains usable, recovery snapshots |
| Physical personas/topology | Existing identity regressions | Fourteen business services/topology, Runtime separately ready; non-root/read-only/cap-drop/no-new-privileges; no host 5122 or persistent Runtime volume |
| No models/log privacy | Source/transport tests, exact NOT_USED | Record safe request family counts; inspect sanitized Gateway/Runtime/owner logs for synthetic ordinary/submitted canaries and actual private secrets in memory; emit counts only |
| Restart/readback/disable | Persistence integration tests | Same instance/passwords/DB/volume IDs and owner record hashes, no conversation; disable/re-enable without deleting state |
| Source quality/hygiene/governance | Complete final A/B/C receipts | Current frozen source match, archive scan, retained profiles/volumes/operator changes unchanged, no Actions/merge |

### Explicit execution limits

The snapshot command records safe container/volume/readiness metadata, not database
contents or provider stores. It alone does not prove persisted feedback/receipt
byte equality; that requires bounded owner-store readback. Likewise, browser request
inspection alone does not prove absence of server-side owner/provider calls; pair it
with actual service log/request counters and source/transport evidence. Do not use
an empty log or a UI badge as proof of zero external model calls.

Expiry, credential change, same-session revocation, multi-workspace switch, paused
delivery recovery, interrupted-response UI, and all optional-fault/restart continuity
rows need explicit dispositions. Source/isolated evidence can support the report but
must not be relabelled physical browser evidence. Missing fixture => BLOCKED/NOT RUN,
never silent test.skip. Retention of live owner records does not establish deletion
from backups; Production backup policy remains separately gated.

No retained profile start, reset, cleanup, volume deletion or credential change is
authorized by this harness. It must not select `t105`, any A–P profile, or the retained
pre-Azure environments. It ends with the preserved new test profile and exact evidence,
not a merge or acceptance promotion.
