# T1-47 — Customer sharing changes and renewal

Status: IMPLEMENTED_FOCUSED_NATIVE_VERIFIED. Base: Application `90b46072c5ba3393ae500cc44d22d55e9d23da6f` (PR #96), paired Expert `86ae8e45823fdf2ecb60fa9741ddbf56c1c82dde` (PR #48). The paired Expert receipt records final candidate/tree/merged source. PostgreSQL UAT and business/production acceptance remain open.

## Customer result and trace

AR-003 COV-005 and Master §§6,23,39–40 advance for customer-controlled changes and renewal of delegated customer-advice sharing. Consent & privacy offers Change sharing for an unexpired, unrevoked record and Renew sharing for an expired, unrevoked record. Revoked grants cannot be renewed through this action. The recipient stays fixed to the selected grant's actor; changing a recipient requires a separately reviewed sharing action.

The dialog loads the exact current owner-bound grant and revision, displays its recipient label/reference, previous permissions and expiry, then asks the customer to select the complete replacement scope and a 1/7/30/90-day duration. No replacement scopes are selected by default. The customer reviews and explicitly recognizes the recipient, new scope and expiry before confirming. Closing before confirmation changes nothing. Pending submissions prevent duplicates; other sharing/refresh/revocation actions remain mutually exclusive. Back preserves the draft.

Success requires two exact bound owner receipts (old grant Revoked, new grant Granted), distinct evidence and grant references, and a fresh overview showing the new actor/scope/expiry. An old record returned in that recent-100 overview must be revoked and bound to the same actor. The old record can fall outside that bounded overview; its exact revocation receipt still identifies it. Subsequent Stop sharing uses the returned final revision. Unconfirmed/conflicting outcomes suppress the old list and require explicit refresh, with no automatic retry or rollback assertion. Late reads/writes cannot cross the originating customer/session/API context.

## Owner boundary

- GET `/local/v1/consents/customers/{customerId}/grants/{grantId}/sharing-options` returns the current exact grant and customer options. Exact target lookup is independent of the recent-100 overview.
- POST `.../grants/{grantId}/replacement` accepts only requestId, expectedVersion, capabilityIds and expiresAt. The body cannot supply recipient email/actor, customer, tenant, owner version or purpose. Strict JSON and same-origin/no-store checks apply.
- Session-derived tenant/actor and exact current customer ownership are rechecked before persistence and after its receipt. Current recipient AM membership is checked before and after. The recipient actor comes only from the exact customer-owned stored grant; no directory search, principal creation, role/assignment or recipient message occurs.
- Supported scope remains the five implemented financial-read/document-read/search/basic-report operations from T1-46. Consent does not create effective AM access. Owner or recipient changes after commit can leave an unconfirmed outcome, never a distributed rollback guarantee.

## Atomic persistence

The existing per-tenant Consent database and runtime grants are sufficient; there is no migration or dependency change. ReplaceAsync locks the same tenant state row as grant/revoke, validates the expected revision and operation fingerprint, and checks the old grant's exact customer/actor/purpose/current owner/version. Only an unrevoked target is replaced, including an expired target. Old capability/expiry/owner fields remain immutable.

In one transaction it sets the old grant's revoked_at, inserts the new grant under the existing one-current-grant unique index, appends existing Revoked and Granted audit/outbox records at revisions v+1 and v+2, stores one composite operation receipt, and advances state to v+2. Either all persist or none do. Existing readers share-lock the same state row, so they cannot observe a half replacement. No new audit operation kind is fabricated. Outbox persistence does not establish external canonical Audit delivery acknowledgement.

Identical stored operation replay returns the same composite receipt before currentness/revision/expiry revalidation in the store; changed payload/target/owner version under that request ID conflicts. The customer API still requires a current session/owner/member and valid future expiry. This is not unconditional replay across changed authority or expired client intent. Concurrent new operations at the same expected revision conflict. A revoked target cannot be resurrected by a fresh operation. Failed later writes dispose the transaction without committing either grant change.

## Native evidence

92 selected backend cases pass: 28 new replacement policy/coordination/real-loopback-HTTP cases plus the 64 sharing/Consent/materialization/tenant-boundary regressions. They cover current/expired exact targets, fixed recipient, unsupported/duplicate scopes and expiry/overflow, wrong customer/target/purpose/self, both receipt bindings/evidence, owner/member changes around persistence, strict JSON/origin/session boundaries. Dependency adapters are not PostgreSQL evidence.

48 customer UI checks pass: 16 new replacement cases and 32 prior create/view/revoke cases. They cover change and renewal → verified view → Stop sharing at the new revision, no selected defaults, fixed recipient, exact old/new receipts, malformed evidence, old-active/scope readback rejection, foreign target, duplicate pending changes, late context response, cancellation, conflict refresh and minimal payload/session headers. Initial dialog-animation assertions and lint/analyzer issues were corrected before final passing runs.

TypeScript, all changed frontend source/test lint, customer Vite production build and Consent build pass. Diff checks pass. No development GitHub Actions or dependency/pin changes. .NET SDK10.0.401 matches the pin; available Node24.19.0 differs from unchanged Node24.21.0. Full PowerShell Verify remains NOT RUN.

Five authored physical PostgreSQL cases compile but are NOT RUN here. They exercise active/expired replacement and replay/effective scope, concurrent revision conflict and revoked-target refusal, rollback of grants/revision/receipt/audit/outbox after injected receipt failure, and owner-version/tenant denial. To run only in isolated migrated Consent fixtures with existing runtime grants, provide `T147_CONSENT_T001_OWNER` and `T147_CONSENT_T001_RUNTIME` and run:

```bash
dotnet test tests/persistence/Monergy.AccessIntegration.Tests/Monergy.AccessIntegration.Tests.csproj --filter FullyQualifiedName~PostgresSharingReplacementTests
```

The fixture deliberately resets its isolated Consent database; no production/shared data is permitted. Live PostgreSQL UAT, provider authorization/connection/reconnect, recipient workspaces, full registration/agreements/Settings/MFA and production acceptance remain open. Original 151 requirements/81 features remain the denominator; this unit does not imply complete Consent or Phase 1 acceptance.
