# Basic KYC configuration and use

This Phase 1 route is a customer-requested manual evidence review. Its accepted state is **Manual review accepted**, never government, Aadhaar, bank or regulated-provider verification. No external KYC provider is selected or silently enabled.

During onboarding, the operator supplies a private JSON configuration file keyed by the supported tenant. Each tenant object contains `Notice` (the operator-published review notice, 20–4000 characters) and `Reviewers` (1–20 objects with existing `ActorId` and a meaningful `Name`). Do not use an invented legal notice. Reviewer IDs must identify real users in that tenant. A reviewer cannot approve their own customer submission. An organization administrator is not automatically a reviewer.

Pass the file on every Prepare/Start invocation that should enable this route:

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile onboarding -EnableOnboarding -KycConfigurationFile 'E:\Private\monergy-kyc.json'
```

Keep the other existing flags and configuration files needed by your profile. Without KYC configuration the customer sees an explicit unavailable/setup state; no approved status is seeded. Missing notice/reviewer fields produce a controlled configuration error. Tenant configuration is copied to Customer & Identity only. The internal Evidence endpoint uses the existing local service address, not an arbitrary customer-provided URL.

The selected reviewer additionally needs the existing Advisor document-read capability, customer relationship and current customer-advice Consent grant. Case submission includes explicit acceptance of the published manual-review notice, but does not issue or widen those independent grants. If permission or Consent is absent/revoked, document download and review decisions fail closed. Credentials and approved provider arrangements are not prerequisites to building or using this manual route. Any future regulated connector must retain its own approved route and purpose-specific authorization; this feature does not implement or authorize one.

Customer: open **Basic KYC** (also linked from My Profile), upload scanned PDF/PNG/JPEG evidence in Documents, set its category to Identity, then select up to three exact versions, their identity/address purposes and a named reviewer. At least one identity document is required. Read and accept the notice, add a note, review and confirm submission. Status and paged history remain visible. Withdraw a pending submission, or submit corrected evidence after changes are requested, a review is declined, withdrawn or completed.

Reviewer: open `/kyc/reviews` and sign in with the assigned user. The reviewer shell validates current assignment without requiring customer ownership or administrator status. The queue pages through pending assigned cases. Download the submitted evidence under current document permissions, record a reason and explicitly confirm manual acceptance, request changes or decline. Downloads use the submitted version and verified hash, never a later replacement. Final decisions preserve the original submission, notice, reviewer and evidence references.

Uncertain writes preserve the original operation for exact retry. Refresh obtains current state before another mutation. Stale versions and changed publication revisions conflict. A policy change requires pending customers to withdraw and resubmit against the new notice; it cannot silently change their acceptance.

Customer & Identity migration `0012_basic_kyc.sql` stores append-only case snapshots and actor-bound operation inputs. Bootstrap grants INSERT, not UPDATE/DELETE/TRUNCATE, on the event table. Apply migrations before physical startup. Original file bytes remain Evidence-owned; reference storage can be temporary. A historical manual acceptance does not guarantee continued byte availability or ongoing official identity verification.

Physical PostgreSQL migration/grant/restart and full authenticated Docker/Windows UAT remain unverified. Do not interpret component/owner tests or manual review acceptance as production or regulated KYC acceptance.
