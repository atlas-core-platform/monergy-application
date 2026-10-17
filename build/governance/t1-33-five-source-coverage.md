# T1-33 — Five-Capability Source Coverage in Customer Action Centre

**Status:** SOURCE IMPLEMENTATION CANDIDATE; local React/UAT acceptance not executed. Application baseline `0dd4faca302c2bf9d64279ebe1f92cf69ecc9300` (T1-32 merged).

## Business functionality
The existing current-customer Action Centre coverage model now recognizes individually reviewed Financial Profile source fact types: `INCOME`, `EXPENSE`, `LOAN`, `ASSET`, `INVESTMENT` (n of 5). Missing Asset/Investment source information is surfaced as bounded Documents follow-up only, not a claim that a person owns nothing, has no investments, or has a current portfolio/asset valuation. Existing processing attention remains read-only.

## Boundaries
- Current user/session/tenant resource-authorized Financial Profile remains the sole owner of trusted facts.
- Evidence document count and Job Management recent processing state must load before a full no-attention view; any unavailable owner input remains unknown rather than clear.
- No new API, schema, permission, security assertion, calculation, valuation, provider, score, task list or AI advice.
- This is source coverage, not financial completeness.

## Required focused verification (pending)
```powershell
pnpm --dir apps/customer-web exec vitest run tests/ProductActionCentreExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL/UAT journey: reviewed Asset and Investment source facts from Financial Profile → Action Centre 5/5; missing-type case and unknown-owner case; verify tenant-scoped read. No development GitHub Actions.

## Expert
Synchronized source knowledge required at `monergy-system-expert/work/t1-33-five-source-coverage-sync`, with How It Works/Connects to Financial Profile, Evidence, Job Management and preserved unaccepted source-state boundary.
