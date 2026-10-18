# T1-46 — Customer-created scoped sharing permissions

Status: IMPLEMENTED_FOCUSED_NATIVE_VERIFIED. Base: Application `3e5c252e0c3d9f99589f183be0cc2f29abedf29e` (T1-45 / PR #95), paired Expert `f34b260ea151ee54774513f75eb3bcd14a20767a` (PR #47). The paired T1-46 Expert receipt records the exact final candidate/tree/merged source. Business/production acceptance and actual PostgreSQL UAT remain open.

## Customer journey and trace

AR-003 COV-005 and the Master §§6,23,39–40 retain customer-controlled grant/view/revoke as distinct actions. Consent & privacy now supports creating a new customer-advice permission for an existing organization recipient, reviewing the resulting record and using the existing Stop sharing action. This fills the missing customer grant action; modification/renewal/reconnect and provider consent remain separate open workflows.

Share information starts with no scopes selected. The customer enters the recipient's registered organization email, chooses supported operations and a 1/7/30/90-day duration, reviews exact recipient/scope/expiry and explicitly confirms. Success requires an exact request/email/receipt binding and a fresh Consent read showing the same grant, recipient, capabilities and expiry. The page takes the returned revision into subsequent revocation. Tests exercise create → verified view → stop sharing in the same customer UI.

A pending request cannot be submitted twice. Refresh/revocation and sharing are mutually exclusive. Conflicting/unconfirmed outcomes require an explicit owner refresh before another change. Late options, writes and readbacks cannot repopulate a changed customer/session/API context. Revisited form details are preserved; closing before confirmation creates no grant. Leaving during a submitted operation does not imply rollback. Existing revocation busy state is now scoped to its API/context, and its current-context ref is updated on commit rather than during render. Shared owner response validation rejects duplicate grants and malformed revocation receipts.

## Authority and identity

GET `/local/v1/consents/customers/{customerId}/sharing-options` returns owner-bound current revision, purpose and implemented operation choices. POST `.../sharing` accepts request ID, expected revision, recipient email, selected capabilities and expiry; actor/customer/tenant/purpose overrides are not accepted in the JSON body. Current C&I session and exact customer ownership are checked before recipient resolution, again before persistence and after its receipt. Recipient membership is checked before and after commit. A post-commit denial is an unconfirmed outcome and does not claim rollback or a distributed transaction.

C&I owns exact normalized-email resolution through workload-only POST `/internal/v1/consent-recipients/resolve`. It uses the existing dedicated RecipientLabelToken, distinct from context/session/provisioning credentials; there is no public directory/list/autocomplete endpoint. Its query is confined to the validated tenant C&I connection and returns one existing principal. Consent's client validates tenant, actor format and exact email response binding. Unknown/inactive recipients give a generic sharing-recipient-unavailable result; self-consent rejects. No identity, ownership, role or AM assignment is created and no message/email is sent to the recipient.

The customer-facing catalog contains only implemented financial information read, document read, permitted search and basic report read/generate operations. It excludes updates, future AI and provider management. Consent is a necessary purpose/scope/expiry record; effective access still requires current AM permissions/assignments and the protected owner's checks. Organization administration alone confers no financial sharing authority.

## Persistence and integration

The existing Consent GrantAsync transaction retains current optimistic revision, canonical capability ordering, operation fingerprint/replay, active-grant conflict, immutable owner/version binding, grant insert, revision update, receipt, audit and outbox atomicity. New code coordinates existing owner ports and this store; no schema, migration, runtime grant, AM role/capability or external provider is added. The current Gateway customer-Consent GET/POST route already covers both new paths and enforces same-origin/no-store at ingress. New owner endpoints also reject foreign origins and unknown JSON fields.

The existing one-current-grant rule remains: change/renewal of an expired-but-unrevoked or active grant is not silently implemented as a second grant or automatic revoke. Consent outbox persistence is not proof of canonical Audit delivery acknowledgement; that existing boundary remains explicit.

## Verification

64 focused backend cases pass, including new sharing policy/coordination/actual-loopback-HTTP checks and existing Consent policy/materialization/tenant-boundary regressions. Tests cover invalid/overbroad scopes, bad email/expiry/operation/revision, foreign tenant/session, owner/delegate separation, dedicated recipient credential, malformed/foreign/missing/self recipient, membership/ownership changes around commit, exact receipts, optimistic conflicts, strict JSON and origin checks. Separate real loopback C&I lookup checks enforce token, normalized email and response binding. Dependency doubles do not execute PostgreSQL SQL.

32 customer UI checks pass: 17 new sharing cases and 15 existing Consent regressions, including create/view/revoke, no default scopes, explicit recognition, input validation, retained draft on back, cancellation, duplicate suppression, conflict/uncertainty refresh, wrong receipt/email/recipient/scope/expiry/duplicate readback rejection, late responses after context/cancel and exact minimal API payload/headers.

TypeScript, all changed frontend source/test lint, Vite production build, C&I and Consent service builds and affected Gateway build pass; .NET builds have zero warnings/errors. Diff checks pass. Initial nullable test assertion and existing changed-file lint findings were corrected. A new context-transition test exposed retained closed-dialog content and a test-library title-ID collision; closed dialog content is now destroyed and the regression passes. These fixes were verified before the passing final runs. No dependency changes or development GitHub Actions.

Actual PostgreSQL grant/replay/concurrent transaction and tenant-recipient SQL UAT remain NOT RUN; no supported PostgreSQL/non-root runtime is available here. Native checks use pinned .NET SDK10.0.401 and available Node24.19.0; unchanged repository Node24.21.0 and full PowerShell Verify remain unverified. Full registration, published agreements, MFA, remaining Settings/Consent lifecycle, live provider/email delivery, business and production acceptance remain open. Original 151 requirements and 81 features stay authoritative without an inferred completion percentage.
