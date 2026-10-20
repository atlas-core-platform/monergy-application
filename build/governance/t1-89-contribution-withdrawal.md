# T1-89 — Goal contribution withdrawal

Application base `4b4313a25fad2b6202e699c1dabe0c6e6771e061`; Architecture `90934b51b2b203795acf95da2252a2cf1a1d1f4c`.

Master §§2/4.1/20, BO-19, COV-024 and READY M2-WS04-E02-F03 retain Financial Profile ownership of customer-managed goal records. This bounded lifecycle increment lets a customer explicitly withdraw an erroneous contribution report. MON-FIN-010 remains original phase2+. Conditional Goals & calculation explanations, OD-08/C-10, source phases, business acceptance and stage gates remain open.

POST /product-api/v1/goal-contributions/{goalContributionId}/withdrawals and matching /local/v1/customers/{customerId}/goal-contributions/{goalContributionId}/withdrawals accept operationId, expectedRevision and an explicit reason. The reason is nonblank, trimmed, at most500 characters and contains no control characters. Financial Profile appends a CUSTOMER_WITHDRAWN version with the exact previous amount/date, goal ID/revision and immutable target/currency snapshot. Server actor/time identify the new version. It does not delete history, reverse a payment, move money, verify an allocation or compute progress. Withdrawn reports remain visible in lists, details and history. New corrections and repeat withdrawals are rejected; exact original create/correction/withdrawal receipts remain replayable. Reinstatement is not implemented.

The existing migration0022 append-only records/revisions store the new status/reason without a schema change. Existing payloads deserialize with a null reason. In-memory locking and PostgreSQL transaction/advisory locks check the current version and terminal status after operation replay checks. Changed-payload, cross-kind and cross-report operation reuse fail closed. Concurrent correction/withdrawal permits one new revision. Runtime SELECT/INSERT grants remain; physical execution is unverified.

Current owned customer/session, operator tenant binding and matching Self profile.read/update are required for writes. Read-only history remains available. Strict32KiB owner/Product ingress rejects missing, unknown, duplicate, null and oversized input. Product independently reads the exact expected version, binds the complete withdrawal receipt to that report, actor, operation, reason and next revision, reads back the exact saved version, then reconfirms current authority/context. Forged receipts/readbacks fail closed. Later goal/currency edits do not change the withdrawal source.

The customer explicitly opens withdrawal review, sees the saved report and enters a reason before confirming. Uncertain results retain identical operation, expected revision, reason and report through close/reopen and changed goal currency. New mutations remain blocked until confirmation. Confirmed400 permits renewed editing;409 requires refresh/review. Removed capability or changed customer/API context hides the form and discards late confirmation. History distinguishes reported and withdrawn versions without interpreting them as bank transactions.

## Verification

1418 Financial Profile checks pass: all1374 prior cases retained/rerun plus44 withdrawal cases. Actual owner/Product HTTP uses authority doubles and in-memory persistence. Cases cover terminal status, exact receipts and history, competing revisions, tenant/customer and current Self boundaries, operation reuse, strict ingress, lost confirmation, changed goal currency and forged withdrawal/readback rejection.

104 affected UI checks pass:50 Goal Contributions,30 Financial Goals and24 Financial Information. This retains the previous89 and adds15 withdrawal cases. TypeScript, changed-source/test ESLint and Vite pass. Gateway and physical test-project builds pass with zero warnings/errors. Physical regression extends restart/replay/history/terminal correction checks and compiles only. PostgreSQL/migration/query/grant execution, scanner/Compose and full authenticated integrated browser UAT NOT RUN.

Other source/document/domain UI evidence is carried forward, not rerun or counted as fresh passes. No package, service, provider, GitHub Actions or deployment is added. No central Audit delivery is claimed.

After paired merges:89 numbered units=88 implementations+1 governance closure. Original29 journeys remain0 complete/19 partial/10 pending. Goals remain broader scope. Original151 IDs,81 features,126 Phase1 memberships, source phases and business acceptance remain unchanged; acceptedThrough T1-06 and approvedForExpertIngestion false are retained.

Progress, required contributions, allocation/reconciliation, reinstatement and achievement lifecycle, financial reversals, Financial Rules methodology/OD-08/C-10, Search/Reports integration, complete financial records, professional/provider workflows, central Audit and physical/full UAT remain pending.
