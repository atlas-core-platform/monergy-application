# P3-08 — Operations Support workspace

## Accepted source boundary

Operations Support is a bounded Job Management recovery journey. Access Management Default Access Pack 2.12 grants only `job-management.job.recover` to the `operations-support` role. It does not grant customer-financial browsing, Evidence document access, Consent authority, access administration, professional decision authority or Platform Administrator authority.

Job Management owns the tenant-bound processing queue, safe job metadata, retry/cancellation eligibility, optimistic revision checks, idempotent operation receipts and immutable recovery history. Existing owner authorization and Consent are re-evaluated before retry. Customer & Identity owns the authenticated session. Access Management owns the current capability decision. Audit records the governed Gateway recovery boundary without retaining reasons, payloads, credentials or document content.

## Workflow and records

- Operations Support sees safe task, state, attempt, timing, failure-code and customer-reference metadata for the authenticated tenant only.
- Customer names, financial facts, documents, Consent records, credentials and arbitrary job payloads are not available through the workspace.
- Eligible retry and cancellation use the current Job Management state and exact reviewed revision. Stale or uncertain work fails closed.
- Browser/network retries reuse the same operation identifier and return the immutable owner receipt; changed intent is rejected.
- Retry rechecks the original job's current owner authorization and Consent before replay is scheduled.
- Every read and change rechecks `job-management.job.recover`; disablement, role change, grant removal, tenant mismatch or subject-version drift ends usable authority.
- Organization Administrator processing controls remain a separate route and authority. Operations Support cannot grant access or impersonate an administrator, customer, professional or Platform Administrator.

System Expert may explain these ownership and recovery boundaries and guide navigation. It cannot approve recovery, create authorization, change Consent or infer a professional decision.

## Validation boundary

Release builds, focused Access Management/Job Management/Gateway/Customer Identity tests, React regressions, TypeScript, ESLint, production bundle and exact embedded Access Management source verification are required before merge. Disposable PostgreSQL recovery/privilege checks, retained LOCAL onboarding, installed-browser persona journeys, readiness and authenticated topology remain explicit acceptance gates on a capable host.
