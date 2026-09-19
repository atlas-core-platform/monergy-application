# D05 frontend test-lifecycle remediation

Status: closure pending remediation verification and new exact-head CTO approval.
Evidence level: SIMULATOR. No business implementation or dependency change.

## Preserved history and authority

- Reviewed application candidate: `827a7e26a515196047666d05c04ce46b218d2274`.
- Initial acceptance: `e669bf248ac2a8f362c538cbddf2e2d5a65174ef`, tree
  `9d786d44684419495eaf481534cae646a3189d22`. It remains in history, not amended.
- Runs `35471222663`, `35474527894`, `35474529730` remain FAIL. The latter
  two are independent push/PR executions, not retries. Exact artifact identities
  and SHA-256 digests are retained in `d05-acceptance.json`.
- CTO authorized only frontend test-lifecycle remediation, diagnostic/probe
  evidence and renewed verification. PRs #3/#9 stay draft and unmerged.

## Proven lifecycle path

Locked versions: Ant Design **6.6.3**, `@rc-component/util` **1.13.0**,
`@rc-component/form` **1.8.6**, React/React DOM **19.3.0**, Testing Library
React **16.3.3**, user-event **14.6.7**, Vitest **5.0.0**, jsdom **26.1.0**,
Node **24.21.0**, pnpm **12.4.1**. No pins or lockfiles changed.

`Vs02Experience` renders four `Form.Item` fields. Ant's `ItemHolder` debounces
errors/warnings through `form/hooks/useDebounce`; `ErrorList` can also debounce
them. `useDebounce` invokes `useDelayState(value)` with `{ ms: value.length ? 0 : 10 }`.
The exact locked hook uses `setTimeout(() => setValue(nextValue), delayConfig.ms)`
at line 39. Its cancellation function runs when another update replaces work;
the hook registers **no unmount cleanup effect**. Its alternative default branch
uses requestAnimationFrame, but that is **not the observed failure path**.

Both original test files explicitly execute Testing Library `cleanup()`.
Vitest globals are not enabled, so the imported afterEach hooks are authoritative;
we do not rely on Testing Library discovering a global afterEach. Diagnostic
observations confirm an empty DOM after cleanup. All original tests used real
timers; user-event calls and asynchronous UI assertions were awaited.

A single Form + Form.Item + Input mount/unmount is sufficient to demonstrate
the problem without user-event, fetch, hover, tooltip, notification or typography
interaction. Two 10 ms state callbacks survive root unmount. In one instrumented
execution of the actual VS-02 suite:

| Existing test                                         | Pending useDelayState callbacks after cleanup |
| ----------------------------------------------------- | --------------------------------------------: |
| Initial empty-state rendering                         |                                             8 |
| Successful reference flow                             |                                             0 |
| Classified reference failure                          |                                             0 |
| Required Customer ID validation and axe accessibility |                                             1 |

Every surviving callback was the form debounce's 10 ms timeout. The final
validation test clears Customer ID, submits, awaits validation feedback and
awaits axe. Form deregistration/notification during cleanup can leave a final
debounce. A real Node timeout can then invoke React after jsdom has disposed
`window`, producing the retained hosted failures. Passing seven assertions does
not make that failed test process acceptable.

Classification: a test-environment lifecycle ownership gap exposed by the locked
third-party form hook's uncancelled delayed work, not Financial Rules or Monergy
frontend business behavior. The correction does not claim to fix the dependency
hook's production unmount behavior.

## Bounded correction

Only VS-02 component tests own a Vitest clock from before mount through teardown.
Auto advancement allows asynchronous UI/axe work to progress normally; user-event
gets its supported `advanceTimers` adapter. Teardown unmounts React, executes
pending test-owned callbacks inside async React `act`, asserts zero pending
timers and zero remaining roots/portals, then restores real timers and globals.
There is no clear-all-timers, arbitrary sleep, ignored exception, skipped test,
retry or dependency patch. All seven original tests and assertions are retained.

`FormLifecycle.diagnostic.tsx` is a separate three-test diagnostic, not a change
to the seven-test suite count. It demonstrates the original surviving callback,
proves cleanup executes rather than discards queued work, and distinguishes
delivered watcher messages from retained native MessagePort resources.

The optional Vitest async-resource diagnostic also reports a retained native
MESSAGEPORT from `@rc-component/form`'s `useNotifyWatch.macroTask`: it posts a
one-shot message but does not explicitly close its ports. The isolated observer
proves both mount/unmount watcher messages have delivered by corrected teardown
(zero pending messages). This is retained as a **separate unresolved dependency
resource observation**, not misrepresented as a post-environment callback or a
fixed resource leak. No ports in product code are patched/closed. The diagnostic
closes only its own observed channels after delivery. Unknown resource types,
timer leaks, unhandled errors or callback failures fail the probe.

## Verification protocol (results are generated, not preclaimed)

```powershell
pnpm --filter @monergy/customer-web exec vitest run --config tests/lifecycle.config.ts
pnpm --filter @monergy/customer-web test
pnpm test:browser
```

For this D05 delivery branch only, the existing push/PR workflow first executes
the isolated diagnostic and **20 consecutive fresh-process seven-test runs** on
Linux. `build/probe-frontend-lifecycle.mjs` is bounded remediation evidence, not
a product NFR or permanent test-count target. It uses supported reporters and
async-resource diagnostics, requires exit zero, 7/7 assertions, no unhandled
errors, and four successful VS-02 lifecycle cleanup records per execution.
It preserves native-resource observations without swallowing exceptions and
stops on the first failure. No attempt is retried. Full hosted verification is
blocked until its probe succeeds; non-D05 branches retain the original pipeline.

Only after the hosted probe passes, run the full local application gates against
the same committed head. The existing full push and PR jobs then provide fresh
12-image build/identity/SBOM/scan evidence. Compare exact CVE/package/version/
severity/fix-state/fix-version tuples against candidate run `35471427802`.
Any delta stops closure, including an available-fix change. Local Docker remains
separately BLOCKED if unavailable. No old workflow substitutes for new-head proof.

Generated acceptance verification and probe summaries identify actual head/tree;
the post-commit review evidence lists remediation commit(s), changed files,
workflow/artifact identities and final results. No self-referential commit hash
or unexecuted successful result is written into this source document.

## Architecture and readiness preservation

Architecture candidate/remote/PR #9 remains
`e4ab9a1bf62891c73b400eab9faa0fe78bc200f5`. The sole 390-added/0-deleted-line
source append remains untouched, unstaged, uncommitted and non-evidence:
SHA-256 `32CB4B0A6DDDDEC46FF486E923C2949AA21D87166680E94997D7010176588AD5`.
Stash `8667213f8630eb305b94a9546fb3adf361d6f9c4` is preserved separately.

SG-01 READY; SG-02 CONDITIONALLY_READY; SG-03/SG-04 BLOCKED. OD-08/C-10 remain
open; VS-03 remains CONDITIONALLY_READY. No client methodology, stronger
integration evidence, durable persistence, publication, deployment, merge,
tag, architecture closure or subsequent deliverable is authorized.
