# T1-94 — Audit Log (P1-29)

Status: current product journey source functionality complete. Physical PostgreSQL and full authenticated live UAT remain unverified; business acceptance is unchanged.

## Administrator journey

Organization Settings > Audit Log exposes the current tenant's minimized product request/result evidence and existing canonical domain events. Current Access Management administrator authority is required before reads and confirmed after results. Search is literal; filters cover action, outcome, user, customer and UTC time range. Counts and ordered cursor pages use at most 50 rows. Fresh exact evidence detail and an explicitly reviewed JSON export of the current filtered page complete the journey. Export is not a whole-ledger dump; use its nextCursor for subsequent pages. Browser requests abort on context changes and reject foreign or malformed metadata. Same-origin and no-store protections remain in force. No document contents, financial values, raw payloads, tokens, IP or device data are collected or exported.

## Evidence and event scope

The local Product Gateway records admitted tenant-scoped product, identity, access, consent, evidence, classification, job, audit, contract and UAT API ingress. Required categories cover login, consent grant/revoke, document upload/view/update or processing, report generation, data export and other API access. Authentication is resolved through Customer & Identity; customer ownership is resolved through its owner. Login request evidence has no invented actor; successful login records only the identity owner's returned actor. Failed or unresolved identity remains explicitly unauthenticated/unrecorded. Document identity is extracted only from bounded metadata returned by the owner. Route resource references remain separate from confirmed document identity.

A minimized REQUESTED event is durably acknowledged before execution. A distinct RESULT must be acknowledged before the buffered response is released. Both records are immutable and exact replay is idempotent. Result identity must match the request, allowing only previously unknown owner-confirmed fields and successful login actor enrichment. HTTP_SUCCEEDED describes only an HTTP response; ACCEPTED (202), HTTP_FAILED and UNKNOWN retain their actual meanings. A request without a result is unresolved. If audit confirmation fails after an owner commits, the Gateway returns unavailable without automatic retry or invented rollback. Lost receipts can leave a recorded result despite an unavailable browser response. Operators inspect the request reference and owner state.

Existing canonical domain events retain their own provenance and RECORDED meaning. Missing legacy human/customer/document fields remain Unrecorded, not fabricated backfill. IP/device collection is deliberately disabled. Tenantless invalid requests have no legitimate tenant audience and cannot select a tenant database. Direct engineering host access, future provider/AI routes and broader security telemetry are outside this current product-ingress scope. This implementation does not claim audit coverage for unimplemented future journeys or reconstruct historical identities.

## Persistence and operational composition

Audit remains the owner. Migration0004 adds append-only audit.product_activity in each protected tenant-bound Audit database with SELECT/INSERT runtime grants and no UPDATE/DELETE/TRUNCATE. Runtime checks reject mutable grants. PostgreSQL uses request advisory locks, exact replay checks, request/result identity matching, repeatable-read counts and cursor queries with literal parameterized filters. Existing canonical evidence and Access envelope identities are read without rewriting them. A dedicated transport token admits product evidence. The local UAT composition generates that token and enables Gateway capture. Startup migration, physical SQL/grants/restart and real database execution are not claimed as verified here.

## Verification

330 affected backend checks pass: 301 Access Integration (including 16 capture checks) and 29 Audit/schema/owner checks (including 27 new audit-owner cases). Actual HTTP tests use in-memory Audit and explicit current-authority/identity doubles. They cover tenant separation, immutable replay, mismatched result identity, literal filters and cursor scope, current administrator revocation, missing legacy identity, bounded metadata, unavailable audit before execution, withheld responses after execution, lost receipt and accepted/failed outcome semantics. Capture category fixtures use current route shapes.

25 focused UI/navigation checks pass, including 19 new Audit Log cases covering detail, filters, dates, pagination, forged metadata rejection, export review/download and late tenant response disposal. TypeScript and changed-file ESLint pass. Gateway and Audit host builds pass with zero warnings. Other backend, native OCR and full UI regression results are carried forward, not counted as fresh passes. Physical PostgreSQL/migration/query/grant/restart, Compose and full live authenticated browser UAT NOT RUN.

## Delivery reconciliation

After paired merges,94 numbered units comprise93 implementations plus1 governance closure. P1-29 current product Audit journey advances Pending to Complete:original29 now5 Complete,19 Partial,5 Pending;24 incomplete. Complete means delivered source functionality separately from business acceptance. Broader unfinished producer journeys and verification gates remain open.151 requirement IDs,81 features,126 Phase1 memberships and original source phases/acceptance are retained. acceptedThrough remains T1-06;approvedForExpertIngestion remains false. No new dependency/provider/deployment or GitHub Actions run.
