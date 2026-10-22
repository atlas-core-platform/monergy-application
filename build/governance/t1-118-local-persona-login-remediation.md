# T1-118 — retained LOCAL persona login remediation

Date: 2026-10-22

## Recording evidence

The supplied 72-second LOCAL UAT recording showed correctly selected retained-profile keys for T001/A100 and T001/A300. Both attempts stopped at the public connection screen with the generic authentication message: A100 at the Financial Workspace and A300 at Organization Settings.

## Root cause and correction

1. The launcher advertised retained A100/A300 keys but treated the original `members-initialized` file as a permanent completion receipt. A retained profile could therefore skip missing Access Management members and reject both otherwise-valid keys before route authorization. `Start` now performs a versioned, additive reconciliation for the advertised local members in T001 and T002. It reads the owner state after writes and records success only when every expected member exists. The new receipt is one-time, so later administrator removals remain preserved.
2. A key copied from a terminal can contain leading or trailing line-ending whitespace. The browser now removes only surrounding whitespace before sending the local reference credential and still clears the form immediately after submission. Invalid credentials remain invalid.
3. `Credentials` now prints the selected actor's LOCAL persona label to the host stream while returning only that actor's secret on the output stream. Documentation distinguishes the Organization Administrator, primary/additional Customer and Financial Advisor/configured-reviewer fixtures and their correct routes. No role or customer access is inferred from possession of a key.

## Authorization behavior retained

- A900 can enter `/access` because it is the explicitly bootstrapped Organization Administrator.
- A100/A200 require Customer role/resource access and customer ownership before `/` opens.
- A300 is not an administrator. `/access` returns the explicit administrator-required state. Advisor/reviewer features remain subject to their own current role, resource, consent and named-reviewer controls.
- Tenant isolation, current membership checks, subject-version invalidation and one-key/one-actor binding are unchanged.

## Verification

- Customer Web focused tests: 21 passed (`AccessExperience`, `CustomerLoginJourney`).
- Customer Web TypeScript: passed.
- Changed-file ESLint: passed.
- PowerShell 7 launcher compatibility and retained-profile suite: passed, including old-marker repair, T001/T002 membership readback, credential preservation and replay without replacing operator state.
- `git diff --check`: passed.
- Docker Desktop and the operator's retained PostgreSQL volume are not available in this execution environment. The exact physical confirmation is the normal non-destructive `Start` after pulling the merge; Reset is neither required nor permitted for this repair.
