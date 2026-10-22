# T1-116 — System Expert recording remediation

Date: 2026-10-22

## Observed behavior

A 99-second local recording of System Expert v0.5.0 exposed three product defects:

1. Three examples presented by **Ask Monergy** returned the unsupported-question fallback: document-to-financial-data, organization-access administration and document reprocessing.
2. **Explore Monergy** still described Consent and AI Intelligence as planned-only even though the original Phase 1 implementation journeys are complete in the current build.
3. The shell displayed **Local Monergy code differs** because Run Monergy compared the checkout with obsolete Application and Architecture revisions.

The recording's final connection refusal was separately diagnosed. The address bar was manually changed from System Expert port `4310` to `4713`. The linked Application UAT workspace remains `http://127.0.0.1:4173/`; no product link emitted port `4713`.

## Remediation contract

The paired System Expert change must:

- return deterministic, source-bounded answers for every example question it presents;
- describe implemented Consent and basic governed AI/Q&A behavior without implying production acceptance;
- align local source comparison with this merged Application revision and Architecture revision `90934b51b2b203795acf95da2252a2cf1a1d1f4c`;
- retain `4173` for the Application workspace and `4310` for System Expert; and
- add regression checks for the example questions, capability labels and exact source locks.

## Boundaries

This is a System Expert correctness and usability repair. It does not change Application runtime behavior, accepted architecture, production readiness or the original Phase 1 implementation checklist. Counts remain **29 Complete, 0 Partial, 0 Pending**. Live providers, native-tool verification, full authenticated UAT and OD-11 remain separate acceptance work.

## Local verification

- JavaScript syntax checks pass for the changed Expert services.
- All five shipped Ask Monergy examples produce a deterministic answer or deployment guide; none returns `UNKNOWN`.
- Consent and AI Intelligence resolve to implemented current-build labels.
- Exact source-lock assertions pass.
- The recovered Expert checkout's historical runtime-control test still discovers its adjacent Application checkout despite receiving an intentionally missing explicit path; that pre-existing isolation behavior is reported and is not counted as a passing full-suite run.

No development GitHub Actions are requested; commits and merges use `[skip ci]`.
