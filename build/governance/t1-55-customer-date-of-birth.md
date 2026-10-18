# T1-55 — Customer profile date of birth

Base Application `755bc8f7bc5eccddbb0b7b816af0a69c0fd09a09` (PR104), Expert
`96ca9bd547be5346b60e12c212c20932966140e9` (PR56), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-55-customer-date-of-birth`.

## Planned scope and implemented behavior

Master §13 explicitly lists customer DOB alongside name/mobile/email/PAN/KYC.
This advances the existing My Profile gap under MON-ID-005/012 and MON-FIN-002;
it does not create a new requirement or complete the full profile/KYC journey.

The authenticated owner may set, change or clear an optional self-declared date
at My Profile, including from guided setup. GET/PUT
`/identity-api/local/v1/me/customer-profile` adds `dateOfBirth` as an exact
YYYY-MM-DD string or null. Null means unknown, not a default date. No timestamp,
time-zone conversion, age eligibility policy or KYC verification is inferred.
The owner accepts real Gregorian dates from0001 through the current UTC calendar
day; empty strings, impossible dates, noncanonical/timestamp forms and future
dates fail400 before repository access. Malformed stored responses fail503.

Existing session/membership/tenant/actor/customer binding, intrinsic self-service
scope, strict JSON, no-store/origin protections and before/after ownership checks
remain. A browser cannot choose actor/customer or assert a KYC result. DOB is
exposed only on the own-profile endpoint, not the shared customer directory,
product context, Search, Reports or delegated Consent APIs. UI loads/saves retain
scope epochs and abort/stale-response suppression.

## Owner persistence and replay

Additive C&I migration `0008_customer_date_of_birth.sql` adds a nullable date
column with finite-range/nonfuture UTC constraints. Existing rows remain null.
Existing profile table INSERT/UPDATE privileges cover the column; no ownership
or new broad runtime grant is added. Both normal owner bootstrap (migration
directory) and hash-checked additive verifier overlay include the migration.
SHA256:`4179a20b68f57980a949dbe96b277ec9c94d554ee0395d8c8c39eccc8c340dfd`.

The current per-customer transaction/version check and append-only receipt remain.
Explicit field presence and the date/null value participate in the payload
fingerprint. Omission leaves stored DOB unchanged and uses the exact legacy
fingerprint shape so old omitted-field operations retain replay identity.
Explicit null and omission differ. The presence marker is owner-derived from
JSON setter invocation and never browser-controlled; normal record normalization
preserves it. DOB values are not copied into receipt rows or logs. Mutation,
version advancement and receipt commit are atomic in the adapter SQL; actual
physical PostgreSQL proof remains pending.

The UI offers optional date entry and explicit clearing. It validates returned
calendar values and requires a matching operation/version receipt, fresh own
profile and exact selected date readback before success. Uncertainty requires
explicit reload. The saved date does not change customer ownership/profile
semantics, Consent grants or the T1-54 setup prerequisites.

## Verification and remaining acceptance

**195 backend/session checks pass**, including19 new DOB cases plus all selected
profile, setup/admission, agreement, email, self-session, sharing and tenant/
protocol regressions. New cases cover leap dates/null, strict invalid forms,
future/UTC boundaries, omission versus clearing through JSON and record cloning,
invalid owner responses, and actual loopback HTTP valid/invalid/type/scope/KYC
payloads. Repository dependencies are explicit doubles, not physical SQL.

**55 UI checks pass**, including8 new DOB cases plus profile, guided setup,
agreement, email and App regressions. They cover exact date save/readback, null
clear, future-date rejection/discard, malformed/missing date responses and a
mismatched-date readback that cannot claim success. TypeScript, changed source/
test lint and Vite pass. C&I and Database Migrator builds pass with zero warnings/
errors. Migration hash, additive overlay source wiring, tracker counts and diff
checks pass. SDK10.0.401 matches its pin; Node24.19.0 is available, not proof of
pinned Node24.21. No new dependency/lockfile or development GitHub Actions.

Physical PostgreSQL date mapping/constraints/replay/concurrency/rollback/runtime
grants, PowerShell/Compose and integrated browser UAT are **NOT RUN**. Existing
SMTP, live Docker/ClamAV and broader document coverage remain open. PAN/KYC,
public registration/account creation, authentication-policy and other remaining
profile/Settings work remain planned. No business/legal/production acceptance.

After paired merge,55 numbered units comprise54 implementation units and1
governance closure. The original29 deliverables remain0 complete,17 partial and
12 pending journeys; Profile stays Partial. Original151 IDs/81 features and126
Phase1-applicable requirement membership remain unchanged. The paired Expert
receipt records exact merged Application source/tree and compilation identity.
