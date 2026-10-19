# T1-83 — Vehicle corrections and immutable history

Application base: `0f321e9b22c47fd983c7fe8d61d6ff44f5227407`. Architecture: `90934b51b2b203795acf95da2252a2cf1a1d1f4c`.

Customers can correct reviewed vehicle name, type, optional registration reference, separate purchase/current values and value date using the expected saved revision. Null, empty and blank registration normalize to null; references retain the 40-character bound and are not verified. Original exact canonical VEHICLE source revision, currency, provenance and evidence/document identity remain immutable. Product classification remains ASSET/VEHICLE. Older value dates may be explicitly reviewed; revision order is review history, not financial chronology.

Creation DTO, normalized fingerprint and deterministic identity remain unchanged. Legacy stored receipts default to revision 1. Creation and correction replays return their exact original saved versions after later corrections. Scoped operation receipts and expected revisions prevent changed-payload reuse and competing edits. Latest list/read, exact version reads, immutable history pages of 50 and older cursors are available. Migration `0016_reviewed_vehicle_corrections.sql` adds owner-local append-only revisions with runtime SELECT/INSERT grants and UPDATE/DELETE revoked.

Owner and Gateway require current owned-customer Self/profile/document access, plus update access for corrections. Strict bounded ingress rejects duplicate, unknown, missing and invalid fields. Correction confirmation binds complete normalized values, expected+1 revision, operation/actor/customer and original source metadata, followed by exact fresh saved-version readback and current authority/context reconfirmation. History binds original immutable identity and validates contiguous descending revisions, bounds, cursor and version-1 tail. UI retains uncertain operation/payload/expected revision across retry and reopening, permits correction without current source figures, blocks stale edits, and protects session/capability changes and ignored cancellation. Nullable references open as blank, can be added or cleared, and display Not supplied in details/history.

## Verification

- 916 Financial Profile checks pass, including all 863 prior cases retained and rerun and 53 new vehicle correction/history cases. Actual owner/Gateway HTTP uses explicit current-authority doubles and in-memory owner data.
- 49 affected UI checks pass: 27 Vehicle records (all 17 prior cases plus 10 new) and all 22 Financial Information cases. Direct Vitest JSON report confirms 49 passed/0 failed.
- TypeScript, changed-source/test ESLint and Vite production build pass. Gateway and physical persistence test-project build with zero warnings/errors. Physical regression extended for restart/exact read/replay/history/conflict/grants; compiled only.
- Physical migration/PostgreSQL/query/grant tests, live scanner, Compose and full integrated authenticated browser UAT NOT RUN.
- Unchanged T1-69 VS02/real OCR 573, reprocessing 40, Reporting 64, Search 27, Documents 111; T1-70 Loan 22/Investment 20 UI; T1-75 Income 23/IncomeComparison 13 UI; T1-76 Expense 23/ExpenseComparison 13 UI; T1-79 Liability 23/Snapshot 18 UI; T1-81 Property 25 UI carried forward, not rerun or counted as fresh passes.

## Scope and remaining work

Master sections 13/14/20; COV-022; MON-FIN-010/011/012/014; M2-WS04-E02-F02; original P1-15 Assets and P1-22 asset-position foundation. No registration/ownership/valuation verification, liability/loan links, broader lifecycle, complete asset snapshot, multi-source reconciliation, financial arithmetic, totals/net worth, Search/Reports record integration or central Audit delivery. Financial Rules retains calculation ownership. No package/service/Actions/provider/deployment or source-phase/business-acceptance/stage-gate advancement. MON-FIN-010 remains source phase 2+.

After paired application/Expert merges: 83 numbered units = 82 implementations plus 1 governance closure. Original 29 journeys remain 0 complete / 19 partial / 10 pending. Assets and asset snapshot remain Partial. Business acceptance remains T1-06; current source is runtime-unverified.
