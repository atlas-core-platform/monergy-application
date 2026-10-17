# T1-41 — Tax source coverage in Action Centre

Application base `990a5b1db8b4643d4f305eac8f41340e011d398c` (Tax Reports #89). Requirements: Master §§13,20,39,41; Financial Passport / bounded customer follow-up.

Action Centre now counts all seven currently supported Financial Profile source categories. A reviewed TAX fact satisfies Tax source coverage. If other reviewed information exists but Tax is absent, the customer sees a bounded Documents follow-up when document read is permitted. Absence does not establish taxes owed, refunds, filing status or deadlines. Processing unavailable/permission states still prevent an invented all-clear.

No new endpoint, owner, permission, schema, assessment, calculation, provider, filing or recommendation. The existing Financial Profile and Evidence reads remain current-customer authorized.

Local verification: **10 Action Centre component tests passed**, including seven-category clear state, the missing-Tax follow-up, current access-package limits, and unavailable processing state. Customer TypeScript and modified-test lint passed. Source lint has 3 pre-existing findings, with unchanged rule/message signatures against the base; no new lint finding. No GitHub Actions.

System Expert records Financial Profile → customer Action Centre → Documents with exact source revision, focused native evidence, and owner-UAT/full graph compilation still **NOT RUN**. No complete business feature or product acceptance is inferred.
