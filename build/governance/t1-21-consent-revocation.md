# T1-21 — Stop a recorded customer sharing grant

**Status:** SOURCE IMPLEMENTATION CANDIDATE — focused runtime tests and Expert sync unverified.
**Application base:** `5599e63ec57baca0322b80c1fb9e584beb729aa9` (T1-20 session-safety PR #68).
**Requirements:** original Monergy §§6, 23 and 39; normalized Consent family (MON-CON); AR-003 COV-005; M2-WS02-E03-F01.
**Business result:** an authenticated owning Customer may explicitly revoke exactly one recorded, unexpired customer-advice consent grant through the existing Consent Service owner.

## Boundaries
- Existing Consent Service: `DELETE /local/v1/consents/customers/{customerId}/grants/{grantId}`, with `requestId` and current `expectedVersion`. Owner resolves current session/tenant/customer; provider work is not involved.
- Frontend: `/consent` customer page only. A grant's technical recipient and grant references are shown prominently before the destructive action. Recipient **name is unavailable**; the customer must positively recognize those exact references before enabling the confirmation. No unrecognized recipient should be revoked by guessing.
- Optimistic concurrency: use freshly loaded Consent revision; a conflicting concurrent grant/revoke fails closed. No auto-repeat mutation.
- Owner reply: require matching grant/customer/recipient, operation Revoked, version increment and evidence reference. Hide pre-revocation list and fetch current owner state. If response/reload fails, show an **unknown** outcome and provide refresh rather than alleging success/failure.
- Session boundary: no old response/notice may appear in another customer/tenant/session; abort in-progress request during unmount/context switch.
- Only unexpired unrevoked records have an action. Existing expired and revoked grants remain read only.
- Consent grants do not create business roles, tenant-wide access, live provider connections or authorization bypasses. Source facts remain separate from Financial Profile truth.

## Exact tests and evidence
- Button unavailable before recognition; cancel never mutates.
- Confirm uses exact grantId, customerId, recipient and currently shown expectedVersion with new 32-character idempotency request key.
- Matched receipt triggers owner reload; display Revoked only after confirmed owner state.
- Unknown 409/503/lost acknowledgement and mismatched receipts never infer success or retry automatically.
- Expired/revoked grants never present a stop action.
- Existing customer/session late-response checks remain covered.
- LOCAL cross-tenant, owner and concurrent-revision tests required; no GitHub Actions.
- The System Expert must show consent GET vs DELETE in How It Works/How Things Connect, retain the current owner and version dependency, and distinguish T1-21 candidate source from accepted T1-20 source.

## Explicitly separate subsequent scope
Recipient-name resolution using approved owner information; customer grant/edit workflow; external provider authorization/disconnect; production identity/provider/cloud/retention policy decisions (OD-09 etc). This unit makes no claims about them.

**Definition of done:** focused UI+API tests, live local owner-backed verification, accurate Expert source-matched synchronization, and traceability before acceptance. Source merging alone is not product acceptance.
