# T1-26 — Bounded Loan Source Coverage in Customer Action Centre

**Source candidate, not runtime accepted.** Base Application main `82beb92f21df6fb6875e8618b8a0c1d520322b1a` (T1-25).

## Business outcome
Customer Action Centre reports supported-source coverage for three currently authorized Financial Profile fact types: `INCOME`, `EXPENSE`, and `LOAN`. A missing Loan source fact is a data coverage state only, **not** a claim that the customer has no outstanding liabilities or an eligibility recommendation.

Existing current-session authorized Financial Profile, Evidence library and Job Management status remain the only inputs. The no-attention state requires all three supported types and all three owner datasets to be known. Missing service information remains unknown, not zero/clear.

## Implementation
- Existing current Financial Profile `facts` array determines three type counts and supported coverage `n of 3`.
- A missing Loan source category offers a nonurgent Documents review path if authorized, not unsolicited debt advice.
- Income/Expense, processing attention, access-denied and unknown states remain supported.
- No new tables, APIs, score, calculations, provider access, Loan balance aggregation, role or consent semantics.

## Focused verification required
```powershell
pnpm --dir apps/customer-web exec vitest run tests/ProductActionCentreExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL UAT must confirm one reviewed Loan source fact changes Action Centre coverage from 2/3 to 3/3 without inferring outstanding balances. Recheck customer access/unknown owner failures. No GitHub Actions.

## Expert
Update How It Works for Action Centre read-only computed coverage of three supported source categories; How It Connects existing Financial Profile (fact ownership) / Evidence / Job Management / Access Management. Pin exact candidate source revision, preserve historical accepted deltas and distinguish source merge from runtime acceptance.
