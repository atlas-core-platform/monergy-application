# T1-56 — Protected self-declared customer PAN

Base Application `bc7af14721411aa395b284f58f340ad18bd4fd92` (PR105), Expert
`56ac0780d00e823994fefdf04867ba88905f2981` (PR57), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-56-protected-customer-pan`.

## Planned owner and customer behavior

Master §13 explicitly lists PAN on the customer model. This advances the open
My Profile field under MON-ID-005/012, MON-FIN-002, and bounded MON-SEC-002 /
MON-AUD-001 implementation. A self-declaration does not establish KYC verification,
identity validity, tax eligibility, external PAN validation or full profile closure.

My Profile exposes Manage PAN only after the own profile is verified; opening the
panel explicitly reads masked PAN state. No full identifier is returned or
prefilled. Format checks accept an ASCII five-letter/four-digit/final-letter value,
normalized to uppercase with outer spaces removed; controls/homoglyphs and malformed
values fail before PAN persistence. Recording/replacement requires explicit
confirmation and acceptance of the exact current published tenant agreements.
Missing publication/acceptance disables recording; removal does not require it.

GET/PUT `/identity-api/local/v1/me/pan` derives exact scope from current session,
membership and owned-customer authority. Strict JSON, no-store, origin checks and
bounded LOCAL request controls apply; callers cannot select actor/customer or
assert KYC. Current scope is checked before and after dependencies/write. A denial
after commit never claims rollback. The separate PAN version does not update
name/DOB/profile/ownership versions, Consent, roles or setup prerequisites.

## Encryption, persistence, replay and audit

C&I-held configured 32-byte keys derive separate AES-256-GCM encryption and HMAC
operation keys per key ID. Each value uses a fresh96-bit nonce and128-bit tag;
associated data binds tenant/actor/customer/version/opaque verification reference.
Ciphertext movement/tampering or missing keys fails closed. No plaintext PAN enters
SQL parameters, receipt rows, audit rows or deliberate application logs. Sensitive
request ToString is redacted. Temporary cryptographic byte buffers are zeroed;
managed request strings are not claimed to be immediately erased from memory.

Three additive C&I tables hold current ciphertext/tombstones, append-only operation
receipts and bounded storage read/record/remove/replay audit metadata. Existing
owner database binding and runtime-grant checks remain. Runtime may insert/update
PAN state, insert receipts/audit and read these tables; it cannot delete/truncate
PAN state or update/delete/truncate receipts/audit. Customer ownership SQL remains
read-only. Normal bootstrap and source-hash overlay include migration0009;
SHA256:`e64a53733c88c3e715e6982267eb20b4f044d1f9e5a3c778988b6638c6af1399`.

Per-customer transactional locking, exact ownership and expected PAN version guard
writes. Ciphertext/state/version, operation receipt and write audit commit together.
Keyed fingerprints bind scope, expected version, normalized value/null and agreement
revision. Exact same-operation replay returns the original opaque receipt; changed
payload/binding conflicts. Replay uses its recorded key ID, never a fallback to the
current key. Retained old keys support existing ciphertext and replay; missing old
keys fail closed. Complete production rotation/KMS/retention is not claimed.

Removal sets active ciphertext/key to null while preserving monotonic version and
opaque receipt identity. MVCC/WAL/backups or other historical storage are not
promised erased. This is a bounded profile operation, not account closure or a
retention-policy implementation. PAN audit metadata does not complete the canonical
product-wide sensitive-event matrix or external Audit integration.

The UI clears the full draft at save start, close and fresh reload, uses a password
input with no visibility toggle/autofill, and suppresses stale API/customer/session
responses. Success requires matching operation, exact version, opaque reference and
fresh owner mask/null. Uncertain results require explicit reload; no automatic
mutation retry occurs. The full input is not placed in browser persistent storage.

## Configuration and verification

Default off. `-EnableCustomerPan` requires a named onboarding LOCAL profile; it
retains a dedicated random protected-profile key and writes `CustomerPan.Enabled`,
`CurrentKeyId=local-v1` and its key mapping only into protected C&I config. The
launcher does not print the key. Approved legal publication remains operator input.
See `build/uat/Customer-PAN.md`. The route is within Development-only tenant
integration; production TLS/key-custody/deployment acceptance is not established.

**231 backend/session checks pass**, including36 new PAN cases and retained
profile/setup/agreement/email/session/sharing/tenant regressions. Tests cover
randomized authenticated encryption, tampered scope/version/reference/bytes,
retained/removed old keys and keyed fingerprints, configuration failure, format and
explicit confirmation, missing agreements, removal, malformed state/receipts,
ownership changes and actual HTTP scope/KYC/origin/no-store/no-plaintext responses.
Repository dependencies are explicit doubles, not physical database proof.

**70 UI checks pass**, including15 PAN cases plus profile/setup/agreement/email/App
regressions. They cover explicit opening and confirmation, mask/opaque readback,
full-draft clearing while pending, removal without current legal acceptance,
unavailable configuration, invalid/foreign/unmasked responses, uncertainty/reload,
receipt/readback mismatch and late-save suppression after customer/API changes.
TypeScript, all changed frontend lint and Vite pass. C&I/Gateway/migrator builds
pass with zero warnings/errors. Migration source hash, overlay/grant wiring,
tracker counts and diff checks pass. No new dependencies/lockfiles or development
GitHub Actions. SDK10.0.401 matches the pin; available Node24.19.0 does not prove
pinned Node24.21.

Physical PostgreSQL ciphertext/date mapping, transaction/replay/concurrency/
rollback/audit/runtime-grant tests, PowerShell/Compose and integrated browser UAT
are **NOT RUN**. No real identifier or external verification service is used.
Public registration, authoritative KYC, production transport/key custody, actual
SMTP, live Docker/ClamAV, broader documents and remaining Settings remain open.
No business/legal/production acceptance is inferred.

After paired merge,56 numbered units comprise55 implementation units and1
governance closure. Original29 deliverables remain0 complete,17 partial and12
pending journeys; Profile remains Partial. Original151 IDs/81 features and126
Phase1-applicable membership remain unchanged. Exact merged Application source/tree
and compiled knowledge are recorded in the paired Expert receipt.
