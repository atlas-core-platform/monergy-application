# MONERGY-ASSISTANT-01B — testing execution, 19 November 2026

**Status: TESTING IN PROGRESS — NOT ACCEPTED / NOT MERGED.** This is the separate
TEST-01 execution record. The earlier implementation-only
`MONERGY-ASSISTANT-01B-TESTING-HANDOFF.md` remains unchanged, including its historical
NOT RUN statements. This report is an interim evidence snapshot for the coordinator
to finish after the final source gates and physical Gate D. It is not a GO decision.

## Gate disposition

| Gate | Disposition at this snapshot |
| --- | --- |
| A — corrections, source checks and compilation | **Recorded source checks PASS; coordinator closure pending.** Third complete Web run passes 1,256/1,256 across 96 files, 0 fail/skip. Full lint is 232 files, 0 errors/warnings; TypeScript and production/bundle build pass; all 274 frozen source hashes match. F1–F7 focused checks, AM and Expert suites pass. The final affected Gateway Release build passes with 0 warnings/errors after seven added duplicate-success regressions. Candidate freeze and final scans remain separate. |
| B — disposable integration | **Recorded suites PASS:** Application backend 612/612 (new final Assistant run 133/133 plus 479 unchanged applicable regression results) and AM PostgreSQL 170/170. Seven added ambiguous-success cases passed after genuine RED evidence; no prior suite was silently relabeled as rerun. This is isolated owner/database evidence, not an integrated persona deployment. |
| C — freeze candidate and governed dependencies | **PENDING.** AM and Expert corrections are committed and pushed to their existing draft PRs; their recorded exact heads remain open/unmerged with zero all-event workflow runs. Application remains an evolving isolated candidate. Final Application identity must be recorded externally after commit, not inside its own commit. |
| D — new LOCAL profile, installed Chrome, deployment continuity | **NOT RUN.** Do not infer 14-service readiness, persona/browser results, full-profile outages, restart or disable/re-enable from unit/integration evidence. Gates A–C must finish first. |
| CTO acceptance, merge, Actions, Azure | **NOT PERFORMED / NOT AUTHORIZED by this testing record.** |

`MONERGY-ASSISTANT-01A COMPLETE`, `acceptedThrough=T1-06` and **Business accepted=0**
remain unchanged. Existing owner boundaries and the accepted Default Access Pack are
preserved. There is no external model or customer-specific business lookup in scope.

## Source identities and authority

The starting remote identities are the TEST-01 verified candidates. Later local
dependencies are separately identified; they do not replace accepted authority.

| Repository / draft PR | Starting HEAD / tree | Current candidate disposition |
| --- | --- | --- |
| Application #266 | `da2f3f6eb11cd2ee42e54450286de7ee7e4490b0` / `0afc2e4320607b5908010df7ed2f083cd49e7d8f` | Final HEAD/tree/branch and publication state to be recorded externally after Gate C. No self-referential final SHA is recorded here. |
| AM #32 | `fddce958102941585f74e656186f544f13954f46` / `ebc7b7b64d126f247bd174cf8837de959b9e604b` | Published `b517ce2326200bd7e32f81af467ca704bb6fee00` / `7d09b3ceca6763417a2bbc0f7fba57d215fbe101`, branch `codex/monergy-assistant-01b-platform-feedback-review`; draft/open/unmerged, 0 all-event workflow runs at the recorded readback. |
| Architecture #27 | `90ea223fc37d47582010d603589506e1660dd659` / `2b1d5225aaf765df1fffe787daa8f1d55694030d` | Forward ownership amendment unchanged by this test execution. |
| Expert #212 | `d0caa5f08884497ba50a6e30ded0d967e02ef21e` / `9591eb1e85bab99367b50ba9697bf929c68966fa` | Published `1eca90c3b566dc0922442f9760998d6844faa32b` / `a5f10d97b289ccfa9c78be3937a3c84e7484d38c`, branch `codex/monergy-assistant-01b-knowledge-runtime`; draft/open/unmerged, 0 all-event workflow runs at the recorded readback. |

The AM/Expert publication readback is recorded in
`.artifacts/assistant-01b-testing/remote-interim-published-backends.json`, observed at
2026-11-19 08:14:05 UTC, with no workflow event filter. Application #266 still points
to its starting implementation head; the tested corrections are not published there
yet. The coordinator must record final clean-worktree/diff results, exact remote
heads and all-event workflow counts after final Application publication. The interim
readback is not a claim that Gate C or physical acceptance has completed.

| Preserved authority | Identity |
| --- | --- |
| Accepted Application | `995d0a30991ad2a14a464ade62e0ed948f3e6914` / tree `5e40ffa758c008609de02c7ecb621223b6e7c9ab` |
| Accepted AM | `85cb70159a1a8f700c2e15d191fb8097651c4b78` / tree `ab5482549fbc8a27a5b6eaa583687906e80cbe4b` |
| Accepted Architecture | `80cb94953c2d67c9eee568d333ae3435532af0d8` / tree `77c271846eeb3044170b5d97075560919f748305` |
| Accepted Expert | `0bd252a317b1be3e3754b7ab8cdb855aa3561362` / tree `c7deb6f91fc038323009c3a92b9d5589afc05bde` |
| Immutable 20-entry knowledge canonical digest | `0e4020190ff2e204ab582fdcf1075a8ef90562845b502ddd695ecd9ed213039a` |
| Accepted current-source canonical digest | `9fa50f95c60719a90cc8537f653bc179b8c994c5c1d88cf47845d9da5c73d8e1` |
| Accepted embedded-AM verification receipt | `030f9558350aa383e0b19805fb7fcd2c2783adc4cc9434d51f19d3a601da3267` |
| Runtime catalogue byte SHA-256 | `abb3c5be496cc8fe783eb02ab89e9b0d22a6c02795e06e279c8051d8376cf497` |
| Retrieval revision | `8cd9cd53fcaff5485c3de17b01c472937f789af5ac5386c531cc2d99ddbd50cb` |
| Audience revision | `7e8c37afc97fe3ebb2780c8afcb9b163c8b251c0246a8a4b8c24f109bac551b0` |

The governed Application candidate embeds **57/57 AM source files, 0 mismatches**,
including the separate platform recovery migration. This candidate verification is
distinct from Expert's verification of **48/48 accepted AM files**. Neither replaces
the immutable accepted manifest. Candidate dependencies are also recorded in
`build/governance/MONERGY-ASSISTANT-01B-CANDIDATE-DEPENDENCIES.json`.

## Evidence locations and execution results

Paths below are local, relative to the named repository's isolated worktree. Raw
evidence remains ignored and is not a secret-bearing profile or a published archive.

- **A:** Application `.artifacts/assistant-01b-testing/`.
- **M:** AM `.artifacts/assistant01b-testing/`, with owner report
  `docs/MONERGY-ASSISTANT-01B-Testing-2026-11-19.md`.
- **E:** Expert `.artifacts/assistant-01b-testing/2026-11-19/`, with owner report
  `docs/MONERGY-ASSISTANT-01B-testing-2026-11-19.md`.
- Application backend totals and hashes: **A:`backend-evidence.json`**.
- AM counts/builds/sensitive scan: **M:`am-testing-summary.json`**.
- Expert exact identity and artifact raw/canonical digests: **E:`final-expert-evidence.json`**.

Repeated and subset runs are not added together as unique test counts.

| Executed suite/check | Executed / passed / failed / skipped | Evidence |
| --- | --- | --- |
| Application Assistant contract/owner PostgreSQL suite, final F5 duplicate-success run | **133 / 133 / 0 / 0** | A:`f5-duplicate-success-final.trx`; includes the preceding nine failure-envelope cases and seven added successful-response ambiguity cases. |
| Application architecture regressions | **58 / 58 / 0 / 0** | A:`Monergy.Architecture.Tests-accepted-regression.trx` |
| Integration Gateway regressions | **197 / 197 / 0 / 0** | A:`Monergy.IntegrationGateway.Tests-accepted-regression.trx` |
| C&I regressions | **96 / 96 / 0 / 0** | A:`Monergy.CustomerIdentity.Tests-accepted-regression.trx` |
| Routing/authentication/Audit regressions | **118 / 118 / 0 / 0** | A:`access-routing-auth-audit.trx` |
| Gateway owner-client lifetime regressions | **10 / 10 / 0 / 0** | A:`gateway-client-lifetime.trx` |
| Application backend total above | **612 / 612 / 0 / 0** | A:`backend-evidence.json`; final 133-case run plus 479 unchanged applicable results. Historical 605-case receipt preserved as A:`backend-evidence-before-duplicate-success.json`. |
| AM unit suite | **101 / 101 / 0 / 0** | M:`am-testing-summary.json` and referenced TRX |
| AM complete PostgreSQL suite | **170 / 170 / 0 / 0** | M:`am-testing-summary.json`; platform 83, tenant 87; 7m9s. |
| Expert full suite | **972 / 972 / 0 / 0** | E:`expert-full-final.log` |
| Expert Runtime/retrieval focused subset | **181 / 181 / 0 / 0** | E:`runtime-final.log`; subset, not 181 additional unique tests. |
| Expert model validation | **67 / 67 / 0 / 0 checks** | E:`model-validation-final.log` |
| Initial full Customer Web | **1,254 / 1,236 / 18 / 0** | A:`web-full.json`, `web-full-failures.json`; this run **failed**. |
| Second full Customer Web, pinned Vite 8.3.3 | **1,256 / 1,252 / 4 / 0** | A:`web-full-final.json`, `web-full-final-failures.json`; this run **failed**. Two Customer Management and two Processing Status 15-second timeouts; all 115 EvidenceExperience cases pass. |
| Bounded full-Web timing remediation, two complete affected suites | **61 / 61 / 0 / 0** | A:`query-timing/query-remediation-evidence.json`; CustomerManagement and ProcessingStatus. Changed-file ESLint 0/0, TypeScript PASS, diff-check PASS. Does not replace the complete Web suite. |
| Third full Customer Web after bounded query correction | **1,256 / 1,256 / 0 / 0**, 96 files | A:`web-confirmed-gates.json`; complete serial run, unchanged timeouts. Earlier failed runs and the 61-case diagnosis remain below. |
| Assistant React/API final focused run | **31 / 31 / 0 / 0** | A:`ui-stale-finalization-green.log`; Drawer 11, Action 5, API 15. |
| Corrected accepted-auth fixture suites | **41 / 41 / 0 / 0** | A:`accepted-auth-fixtures-green.log`; CustomerProfile 23, SelfSessions 18. |
| F7 runtime identity/parser checks | **13 / 13 / 0 / 0 checks** | A:`runtime-identity-final.log` |
| F7 Windows/Linux protected-file readability | **4 / 4 / 0 / 0 checks** | A:`runtime-permissions-physical-final.log` |
| Protected reviewer CLI serialization | **8 / 8 / 0 / 0 checks** | A:`reviewer-command-cursor-fix.log`; mocked Docker, no grant. |
| Browser harness privacy/activation/reporting/expiry guards | **30 / 30 / 0 / 0 checks** | A:`harness-source-evidence.json`; strict harness ESLint 0/0, TypeScript PASS. Includes protected JSON parse-diagnostic/ACL regressions and five bounded private failure-capture guards. Discovery is 67 prepared scenarios, **0 executed**; finite normal plan 59. |
| All Gate D PowerShell helper syntax | **10/10 parse checks, 0 errors** | A:`harness-powershell-parse.json`; actual Docker/SQL/TLS/profile operations remain NOT RUN. |
| Windows protected-profile checks | **PASS**, script does not publish a case total | A:`windows-profile-permissions.log` |
| Legacy profile/launcher regressions | **PASS**, script does not publish a case total | A:`legacy-profiles-final.log`; mocked Docker/readiness, not real retained-profile startup. |
| Full Customer Web ESLint, confirmed final Web gate | **0 errors / 0 warnings**, 232 files | A:`web-confirmed-gates.json`; final Web source verified. |
| Changed lifecycle/auth-fixture ESLint | **0 errors / 0 warnings** | A:`ui-stale-finalization-eslint.log`, `accepted-auth-fixtures-eslint.log` |
| Customer Web TypeScript, confirmed final Web gate | **PASS**, exit 0 | A:`web-confirmed-gates.json`; final Web source verified. |
| Customer Web production/bundle, confirmed final Web gate | **PASS**, 1,613 modules; initial 152.96 kB / 39.28 kB gzip; lazy isolation PASS | A:`web-production-confirmed.log`, `web-confirmed-gates.json`; Vite 8.3.3 / Rolldown 1.2.11. All 274 source hashes match; 0 drift. |
| Expert canonical equality | **6/6 repeat byte equality and 6/6 accepted equality** | E:`canonical-equality.json`, `compile-a.log`, `compile-b.log` |
| Expert catalogue exports | **2/2 equal; require-clean PASS** | E:`export-validation.json`, `export-clean.log` |
| Expert Runtime image/payload | **Build PASS; 15/15 source hash equality** | E:`runtime-image-build-final.log`, `runtime-image-inspection.json`, `runtime-image-equality.json` |

Release builds of Gateway (including A:`gateway-f5-duplicate-release.log` after the final successful-response ambiguity correction), AI
Intelligence, Audit, LOCAL Audit host, LOCAL Access Audit host, Platform Control Plane,
DatabaseMigrator and TenantProvisioningOwner each report **0 warnings / 0 errors** in
their A:`*-release.log` files. AM service/migrator builds also pass with zero warnings
and errors. Expert Roslyn extractor passes with zero warnings/errors; Explorer builds
1,484 modules with its existing chunk-size advisory retained. No global zero-warning
Explorer claim is made.

Representative executed commands are `node --test`, `node bin/monergy-expert.js validate`,
the repository's `dotnet test`/Release build commands, and
`pnpm --filter @monergy/customer-web exec vitest run`. The final two auth suites used
`--maxWorkers=1`. Lint uses `eslint ... --max-warnings=0`; TypeScript uses `typecheck`;
production output uses the repository `build` command and its bundle gate. Exact
commands/results remain in the referenced owner logs and summary records.

## F1–F7 corrections and regression evidence

| Finding | Bounded correction and tests | Result / evidence |
| --- | --- | --- |
| F1 | Gateway independently rejects an exact unresolved canonical claim presented as ANSWERED. Positive platform UNRESOLVED/no-claims and product audience denial remain. `tests/assistant/Monergy.Assistant.Tests/GatewayKnowledgeTests.cs`. | **PASS source/contract**, included in 133. Genuine red retained A:`f1-red.trx`; final A:`f5-duplicate-success-final.trx`. |
| F2 | Runtime binds accepted AM manifest exactly; fixtures mutate only that receipt field. Expert `tests/assistant-runtime.test.js`, artifact validation. | **PASS**: genuine 1 pass/2 fail before fix, then 3/3; complete final Expert 972/972. E:`runtime-final.log`, owner report. Accepted receipt unchanged. |
| F3 | Retention scheduling is independent from Audit delivery/configuration. Actual PostgreSQL covers expired/unexpired 30/180/90-day records, missing/invalid configuration and unreachable transport. `FeedbackPhysicalTests.RetentionWorksDespiteBrokenAuditTransportConfiguration`; `OptionalHostPhysicalTests`. | **PASS disposable integration**, included in 133; no retained history used. |
| F4 | Existing AI/AM owners expose bounded protected event discovery/status/recovery, including resource-less list/denial evidence. Recovery keeps event identity, audits resumption, bounds cycles and does not replay mutations. `AuditRecoveryPhysicalTests`, `OwnerSecurityPhysicalTests`, AM platform physical tests. | **PASS disposable integration**: AI and real Audit receipt handling in 133; AM recovery/CLI/capacity in 170. Wrapper 8/8. Physical browser/operator sequence remains Gate D NOT RUN. |
| F5 | Post-dispatch uncertainty remains UNKNOWN; only exact unique owner rejection envelopes claim REJECTED. Successful owner responses also reject duplicate fields recursively before typed deserialization. Same operation recovers the durable result without duplicate topic, mutation or expiry renewal. Protected nested review reads fail closed on ambiguity. Drawer freezes payload and uses a separate conversation epoch after authority loss. | **PASS**: preceding malformed failure-envelope red 2/9 pass, 7 fail → green 9/9; added ambiguous-success valid red 0/7 → green 7/7 within complete 133/133. UI stale-finalizer red 2 fail → focused 31/31. A:`f5-duplicate-success-evidence.json`, `f5-duplicate-success-final.trx`, earlier `f5-envelope-{red,green,final}.trx` and `ui-stale-finalization-{red,green}.log`. |
| F6 | Duplicate references compared as UUID values, normalized consistently; dashed/undashed/case self-reference rejected while valid different-topic reference succeeds. `FeedbackPhysicalTests.SelfDuplicateUuidVariantsAreRejected`, `DifferentDuplicateReferenceIsNormalizedAndAuditHasNoQuestionText`. | **PASS disposable integration**, included in 133; concurrency/expired/deleted/invalid-target checks retained. |
| F7 | Runtime configuration uses validated non-root invoking Linux UID/GID and protected Windows bind access; root/unsupported identities fail closed. Secret directory/file permissions remain narrow. `Test-AssistantRuntimeIdentity.ps1`, `Test-AssistantSecretReadability.ps1`. | **PASS 13 identity + 4 physical file checks**; Windows ACL and legacy scripts pass. Installed full-profile security/readiness remains Gate D NOT RUN. |

AM capacity is deliberately fail-closed: at 1,000 pending obligations **review and
Revoke return 503**. A rejected Revoke is not successful revocation. Sixteen reserved
slots permit only durable audited recovery; original event identity is retained.
Actual delivery must reduce pending capacity before a normal Revoke commits. Recovery
also denies if its own bounded capacity or durable storage is unavailable. The 170-case
suite proves this sequence and all original AM action/outcome classes.

## Historical handoff matrix dispositions

All **43 original coverage rows (31 base + 12 Decision-01)** are retained below by name.
**PASS-S** means source/unit/
contract evidence; **PASS-I** means disposable owner/database integration; neither
means installed-browser or full-profile acceptance. **PARTIAL** explicitly preserves
the missing portion. All physical deployment portions remain Gate D NOT RUN.

| Original area | Actual disposition and evidence |
| --- | --- |
| Accepted knowledge | **PASS-S**: E full/Runtime suites, accepted-source verification, canonical equality; 20 entries and historical locks unchanged. |
| Retrieval | **PASS-S**: E:`runtime-final.log`, `tests/assistant-retrieval.test.js`; all accepted entries/audiences, normalization, aliases, ambiguity, unsupported/unresolved/conflict and no model fallback. |
| Audience | **PASS-S/I**: E Runtime/retrieval and A:`GatewayKnowledgeTests`; five platform-only/mixed questions fail closed without partial fallback. |
| Runtime input | **PASS-S**: E Runtime tests enforce closed object, duplicate/escaped duplicate keys, UTF-8/bounds/query and forbidden-identity transport. |
| Runtime credentials | **PASS-S/I**: E route-purpose tests; A:`OwnerSecurityPhysicalTests.EachWorkloadPurposeIsDeniedInEveryUnrelatedOwnerFamily`; deployment/log disclosure portion NOT RUN. |
| Runtime output/readiness | **PASS-S**: E artifact/contract tests, pinned image equality and F2; actual integrated Runtime readiness NOT RUN. |
| Gateway auth | **PASS-I**: A owner security host/session tests, routing/auth regressions; normal physical persona login remains NOT RUN. |
| Gateway boundaries | **PASS-S/I**: A host/session boundary and existing trusted-entry regressions; retained/new-profile physical matrix NOT RUN. |
| Gateway validation | **PASS-S/I**: `GatewayKnowledgeTests`, F1, minimal request/route credential, forged envelope/revisions/provenance/UTF-8/deadline checks. |
| Browser projection | **PASS-S**: A:`AssistantApi.test.ts`, `AskMonergyDrawer.test.tsx`, Gateway public projection; installed Chrome inspection NOT RUN. |
| UI shells | **PARTIAL**: source integration and Action tests pass; OrgAdmin/DSA/Customer/all available persona and KYC browser matrix NOT RUN. |
| UI lifecycle | **PASS-S**: A focused31 includes lazy/action lifecycle, abort/unmount, authority clearing, late completion, no storage writes, unchanged polling, no feature403 logout; full browser persistence/tenant matrix NOT RUN. |
| Question limits | **PASS-S/I**: `AssistantContractTests.RateLimitsSeparateIdentityAndPurposeAndPermitOnlySameOperationRecovery`; real browser rate-limit journey NOT RUN. |
| Aggregate counters | **PASS-S/I**: bounded counter pressure plus known-topic explicit-submission/anonymous-outcome separation in owner tests; deployed log/request observation NOT RUN. |
| Explicit submissions | **PASS-S/I**: closed limits/protected fields, deliberate separate confirmation in React, actual durable owner success and F5 unknown-state checks. |
| Submission privacy | **PASS-S/I**: protected-input rejection, restricted intake, content-free Audit, privacy deletion; full-profile log canary scan NOT RUN. |
| Submission retries | **PASS-I/S**: actual commit/replay/concurrency/expiry/rate-limit tests plus same-operation frozen React retry; no duplicate success or expiry extension. |
| Reviewer authority | **PASS-I/S**: real AM/AI current checks, default deny, same-session revocation, transaction-boundary race and malformed Allow; normal-browser operator grant/revoke sequence NOT RUN. |
| Review lifecycle | **PASS-I/S**: physical owner classification/assignment/status/duplicate/privacy operations, UUID-value F6, paging and replay/concurrency; browser workflow NOT RUN. |
| Resolution | **PASS-I**: approved synthetic published fixture is required; arbitrary reference/open change cannot resolve. No actual Monergy publication or acceptance occurred. |
| Retention | **PASS-I**: F3 30/180/90-day deletion and surviving unexpired state, stable expiry and privacy deletion. Historical backup deletion is not claimed. |
| Owner persistence | **PASS-I**: real owner migrations/least-privilege SQL and immutable Audit; separate AM schema/journal; full deployed restart NOT RUN. |
| Failure isolation | **PASS-I/S**: optional host malformed/missing/unavailable settings, Runtime isolation, bounded counter loss, truthful UNKNOWN. Full-profile faults and recovery NOT RUN. |
| Physical personas | **NOT RUN Gate D**: normal password OrgAdmin/DSA/customer/platform login and reviewer lifecycle still required. |
| Physical topology | **NOT RUN Gate D**: no 14/14 claim; Runtime separately ready, internal5122/no host mapping still to verify in new profile. |
| Physical question set | **NOT RUN Gate D**: source question/audience cases pass but installed-browser real-owner question matrix has not run. |
| No external models | **PASS-S/I for tested contract**: internal NOT_USED/minimal payload and owner isolation; full-profile request/provider-call observation NOT RUN. |
| Log privacy | **PARTIAL**: source/expanded static scans and isolated content-free audit assertions pass; full Gateway/Runtime/owner synthetic-canary logs NOT RUN. |
| Restart/readback | **PARTIAL**: isolated AM executable restart/retention tests pass; same full-profile restart, both audiences and disable/re-enable NOT RUN. |
| Source quality | **Recorded source suites PASS; final freeze/scans pending**: third complete Web 1,256/1,256, lint 0/0, TypeScript and production build pass, 274 source hashes/0 drift. AM101+170 and Expert972/model67 pass; backend612 pass, including final 133-case run and Gateway Release 0 warnings/errors. No integrated LOCAL deployment acceptance is implied. |
| Hygiene/governance | **PARTIAL**: accepted locks preserved, point-in-time sensitive/diff checks pass; AM/Expert publication and qualified preservation readbacks recorded. Final expanded scan, Application publication/workflows, clean freeze and post-Gate-D preservation remain pending. |

The Decision-01 extension matrix is separately covered; it is not omitted or assumed
complete from the table above.

| Original boundary | Actual disposition and evidence |
| --- | --- |
| Default authority | **PASS-I**: AM default deny and A platform-help/reviewer-denial isolation; live browser proof NOT RUN. |
| Operator lifecycle | **PASS-I/S**: AM explicit grant/revoke, expected revision/replay/conflict and same-session re-evaluation; wrapper8/8. Normal platform browser sequence NOT RUN. |
| Tenant separation | **PASS-I**: AM direct tenant permission/resource-grant/evaluation APIs reject platform capability, including the added tenant regression. |
| Typed identity | **PASS-I**: AM forged/unknown/stale/expiry/credential-version/context/operation/resource/workload failures; real issuer-bridge integration in A owner tests. |
| Workload separation | **PASS-I/S**: all nine purpose credentials denied in unrelated owner families; operator secret restricted to AM configuration/CLI. Full-profile mounts/browser inspection NOT RUN. |
| Audit non-enrichment | **PASS-S/I**: routing/Audit regressions and dedicated platform evidence avoid ProductAuditCapture/customer enrichment; deployed request/log count NOT RUN. |
| Atomicity | **PASS-I**: actual SQL rollback of state/idempotency/outbox, full pending capacity, permission errors and commit-ack uncertainty. |
| Release evidence | **PASS-I**: durable release evidence and current AM recheck; revoke while blocked on owner transaction prevents release/commit. |
| Audit delivery | **PASS-I**: matching immutable receipts, conflicting event reuse, lost acknowledgement, bounded retry/recovery and resource-less events; no fabricated delivered state. |
| Optional modules | **PASS-I**: actual optional host/config failures and tenant owner isolation; full integrated profile faults remain NOT RUN. |
| Privacy | **PASS-I/S**: identity-free counters/intake contract, content-free audit, controlled deletion/retention and static scans; full-profile log scan NOT RUN. |
| Persistence | **PASS-I**: three separate owner stores/journals/runtime credentials, no fake tenant/cross-owner transaction, SQL privilege denials; full-profile restart NOT RUN. |

## Fixtures, harness corrections and failed-run accounting

Disposable PostgreSQL runs use separate loopback fixtures: AM port 15488 and Application
port 15489, image `postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722`.
Storage is tmpfs, with no retained named volume mounted. Randomized test databases,
owner/runtime roles and private generated credentials are outside retained profiles.
Credentials are kept outside evidence in protected ignored fixture directories and
are never copied into this report. F7 uses separate disposable permission fixtures.

- The initial full-Web result remains **1,236 pass / 18 fail / 0 skip of 1,254**.
  CustomerProfile's blanket product-shaped HTTP 200 response broke current multi-workspace
  discovery; the corrected fixture allows only exact product context, explicitly denies
  all nine other owners, verifies two owner context reads and preserves the mounted
  profile assertion. SelfSessions now supplies trusted tenant entry/configured sign-in,
  explicitly chooses the local-key alternative and proves no private sessions request
  or content before or after that choice. Genuine red 2/2 preceded green 41/41.
- The other 16 full-Web failures passed unchanged in bounded rechecks: the direct
  serial selector ran 13 cases, with 12 passing and one timing out. Three nested
  Platform cases were not selected by that suite-qualified filter. A separate run
  of those three and the same remaining Evidence PROPERTY case passed **4/4**.
  No assertion or timeout was modified. These selected rechecks do not replace the
  final complete Web suite. Evidence: A:`web-failures-serial-corrected.json` and the
  coordinator's subsequent four-case run. An earlier over-escaped pnpm selector
  executed **zero** tests with 226 deselected; that is not passing evidence.
- Two new UI stale-finalizer cases genuinely failed before the conversation epoch fix;
  final 31/31 includes continuing status polling and no logout. Four new-test lint
  callback issues were corrected, and final changed-file lint is clean.
- The second complete Web run remains **1,252 pass / 4 fail / 0 skip of 1,256**.
  Instrumentation traced the four timeouts to repeated global role/name queries:
  `getComputedStyle` consumed 90.4%–94.3% of diagnostic elapsed time; retry POST to
  receipt readback took under 1 ms. No owner operation or 15-second refresh callback
  was stalled. Temporary instrumentation was removed before the permanent correction.
  CustomerManagement and ProcessingStatus queries now use the exact visible
  drawer/modal title and alert context, retaining explicit role, accessible name,
  visibility and enabled assertions. Exact retry payload/receipt checks, the 50-row
  paging fixture and cursor/read-count checks, and permission-conflict clearing
  assertions remain. Both complete affected suites pass **61/61**; ESLint reports
  zero errors/warnings and TypeScript passes. The 15,000 ms timeout is unchanged;
  no CSS mocks, fake timers, sleeps, product edits or global harness changes were
  introduced. A:`query-timing/query-remediation-evidence.json` also retains the
  temporary instrumentation/selector corrections. The third complete Web run now
  passes **1,256/1,256**, with zero failed/skipped tests; full lint, TypeScript and
  production/bundle gates pass with 274 source hashes unchanged. The 61-case focused
  result is not added to the full-suite total, and both historical failed runs remain
  part of the execution record.
- AM's complete pre-correction tenant run had 81/86 pass and five stale pack-adoption
  expectations. Only test expectations were updated for the already accepted pack 2.12
  job-recovery permission and Operations Support assignment; exact identities and
  unchanged roles remain asserted. Accepted pack source/permissions were not edited.
  Final complete PostgreSQL 170/170 retained existing timeouts and no skips.
- Application's unchanged tenant-setup concurrency test encountered one 5-second lease
  contention failure, then passed focused unchanged and in the complete 58-case suite.
  Backend harness fixes use valid closed AM request shapes, accepted CURATED/DERIVED
  claim fixtures and unprivileged lock observation; they do not weaken owner authority.
- The first nested duplicate-response harness attempt used an invalid page limit and
  is retained but excluded from valid RED evidence. The corrected test uses a fresh,
  resource-bound read with limit 1 and asserts durable owner `FeedbackRead` evidence.
  The corrected seven cases failed before the production fix and passed in the final
  133-case run. The owned disposable PostgreSQL fixture was stopped afterward; all
  240 named volumes remained identical, with no retained profile/container mutation.
- Reviewer wrapper discovery now uses a valid UUID cursor instead of a fake opaque
  string. Its eight checks validate serialization only and perform no Docker grant.
- Expert's early test parse failure, duplicate-field genuine red cases, concurrent
  generated-asset build interference and missing inspection pin are recorded in its
  dated owner report. Final stable 972/972 supersedes those failures; accepted assets
  and assertions were preserved, not regenerated to make a false match.

## Dependency, scan and preservation boundaries

Expert's six distinct Vite advisories (twelve existing alerts across manifests) are
triaged in its dated owner report with advisory IDs and reachability. The bounded
development dependency patch 8.3.0→8.3.3 is tested; Runtime excludes Vite and node_modules.
This is not a complete ecosystem vulnerability audit or a claim that remote alerts are
closed. Application now pins Vite 8.3.3 and its minimum compatible Rolldown 1.2.11
with the corresponding Oxc types/platform bindings. Frozen install passes.
A:`web-dependency-delta.json` verifies 365 packages before/after, eighteen related
entries replaced and zero same-version integrity/content changes. Unrelated pins
are unchanged. The final full-Web/build gates now pass; the final expanded-output
scan remains required.

Gate D harnesses are prepared but **not executed**. Source guards verify private
Playwright artifacts (including automatic error-context files), truthful result
counts and refusal to rotate an activated Platform credential. Owner helpers bind
the exact new profile, separate runtime roles and module markers. Protected profile,
fixture and QA metadata parse failures emit sanitized diagnostics; synthetic malformed
JSON and broad-ACL regressions pass. The prepared optional-database fault scenarios
now additionally require actual A900 tenant AM/Audit reads and14-service business
readiness while each isolated optional store is unavailable; none has run yet.
Source receipt hashes are in A:`harness-source-receipt-hashes.json` (SHA-256
`b238bc37ed5f593c136dd586ec2c833dc6a52d49036b2b47bdb5fbbc3cd2d8a9`).
The final 30 guard cases include bounded failure-detail capture only inside the
verified private Playwright run directory. Public reports omit error contents;
capture failure fails the run. Prior receipts and the initial protected-directory
sandbox failure are preserved separately; the actual Windows ACL run passed with
scoped authorization. The reporter's intermediate TypeScript return-signature error
was corrected before the final strict helper TypeScript pass.
The optional Q&A
continuity helper reuses the existing deterministic LOCAL provider fixture through
normal governance and per-question permission; its intended positive evidence is
an empty-profile `INSUFFICIENT_DATA` response, not a sourced financial answer.
Source review corrected the shell-less image CA read and preserved QA TLS overlay
restoration across Assistant disable/re-enable. These source preparations are not
Docker, owner-state or browser passes.

The prepared privacy checkpoint has also received independent source review. It
requires every expected base service and the applicable Assistant/QA services, scans
each current container from its creation timestamp, and fails on missing, truncated
or unreadable logs. Raw logs and protected comparison values remain in memory; only
counts, categories and hashes are exported. These checks have **not executed** against
a new profile. Deleted/rotated historical logs cannot be reconstructed, and transient
uncaptured sessions/tickets have pattern coverage rather than exhaustive exact-value
coverage; no physical privacy PASS is claimed.

Expert point-in-time scans: 21 changed source files and 15 expanded payload files,
**0 findings**. Application's earlier snapshot covered 84 source, 94 expanded Web
and 125 evidence files with 10 generated fixture secrets, **0 findings**. The broader
A:`sensitive-pre-freeze-20261119T085856Z.json` records 88 changed source files,
94 expanded Web files and 229 evidence files: 57 generated secret values/388 encoded
variants from 31 task-owned private inputs, **0 findings**. No retained credential
stores or raw browser artifacts were read. Final A:`sensitive-candidate.json` and
the external source-freeze record must match the final candidate.
AM's sanitized summary records its separate source/evidence scan with zero confirmed
secret findings. Later source/build additions require a final rescan. Static scanning
does not substitute for the Gate D request/log canary matrix.

Baseline A:`preservation-start.json` records 239 retained Docker volumes. Read-only
reconciliation at 2026-11-19 08:09 UTC found **239/239 retained, zero missing** and one
additional unused anonymous volume from
the PostgreSQL image's declared `/var/lib/postgresql` VOLUME in the first F7 Linux
permission fixture. It is retained; it is not a deleted or replaced Monergy volume.
No retained profile/database/object version/credential was reset, pruned, rotated or
recreated by these tests. A:`preservation-readonly-final-review.json` records zero
running containers, matching original 4/4 repository status inventories, and unchanged
original `.gitignore` and tracker SHA-256 values. Original HEADs match their own
baseline-time reflog entries; `operator-start.json` did not itself record HEADs.
A:`profile-directory-inventory-final-review.json` records 46 inspected roots and 54
historical profile directories, no `assistant01b` profile, and one historical linked
UAT directory deliberately not traversed. Port 4350 has no listener at this snapshot.
Only directory names and safe container fields were read; no protected configuration
or credential contents were inspected. Volume identity preservation does not prove
database-row/object-byte equality, and original untracked `build/demo` payloads have
status-inventory rather than complete byte-hash evidence. This point-in-time inventory
also does not independently prove that no transient start occurred earlier. Final
post-Gate-D readback remains required. Historical cohorts, accepted worktrees/stashes
and tracker/operator changes remain outside the modification scope.

After recorded PostgreSQL suites completed, the two explicitly disposable tmpfs
test containers were stopped to free memory. Inspection confirmed the expected
pinned PostgreSQL image and zero mounts/named volumes for both. Their ephemeral
database contents are not retained evidence; complete TRX/log/hash receipts remain.
A:`disposable-postgres-stopped.json` records the exact fixture names and stop action.
No retained container or named volume was changed by that action.

## Remaining completion record

The coordinator must append the final affected Gateway source/integration results;
freeze and record exact candidate identities; complete new-profile provenance, installed Chrome
actor/question/authorization matrices, 14 business services plus independent Runtime,
privacy/outage/restart/disable-reenable evidence; then capture final scans, preservation,
draft PR/workflow and diff status. Do not replace a missing physical result with a
source assertion or a previously accepted unrelated cohort count.

Until those required gates complete, this is **not** a tested candidate ready for
CTO re-review. No acceptance receipt, merge, main synchronization, Production/business
acceptance, external-provider approval or Azure implementation is implied. Active-store
retention/privacy deletion does not establish historical backup deletion. Synthetic
publication-proof fixtures do not publish Monergy knowledge.
