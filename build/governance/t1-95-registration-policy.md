# T1-95 — Approved Phase 1 registration policy

Decision provenance: on 21 October 2026 the user approved the proposed P1-01 flow in this implementation thread: email registration with OTP verification, password sign-in and email-verified password recovery, tenant-scoped customer creation, and acceptance of operator-published consent/privacy/terms before activation.

This settles that bounded authentication choice under MON-ID-001–004. It does not close all OD-10, approve production identity infrastructure, select a mail vendor, supply legal text, or approve KYC. Existing LOCAL/CI composition restrictions remain.

Implementation must use Customer & Identity for principal/customer ownership, credentials, challenges and sessions; Access Management alone grants the existing Customer business role without administrative authority. No cross-service database access or fabricated administrator session. Enrollment must be resumable after partial owner completion and must not restore a subsequently disabled membership. A verified email cannot claim an imported identity or another customer's ownership through registration.

Registration is enabled only for explicitly configured tenant bindings with current published agreements and configured real email delivery. Authentication and recovery are tenant-bound, rate-limited, non-enumerating and auditable. Recovery must invalidate old credentials and sessions, with race checks around session issuance. Credentials/codes never appear in logs, audit evidence, URLs, persistent browser storage or API responses. No sample legal text or test email transport is installed in product DI.

Scope status: implementation in progress. No completion, acceptance or merge is claimed by this policy record.
