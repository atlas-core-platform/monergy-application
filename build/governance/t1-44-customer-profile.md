# T1-44 — Customer-owned profile (unverified draft)

Status: DRAFT — SOURCE PREPARED, REQUIRED VERIFICATION BLOCKED BY ENVIRONMENT_OFFLINE. Do not merge or mark this unit complete until the focused checks and paired System Expert synchronization pass.

Base: Application main fdd31a8a9ee9707d4b920fdce8161fe29fb646c0 (merged T1-43 / PR #93). Expert main a0adef2e3a49015558f8f79bd43e9a55d6027c08 (PR #45) accurately represents that accepted source boundary, without claiming business acceptance.

## Prepared functional scope

The authenticated customer views and updates their own display name and an optional unverified international contact number. Registered email remains read-only, with verification derived from C&I email events. The server derives tenant, actor and exactly one active owned customer from the trusted session and C&I ownership records; the browser cannot choose a different customer, grant a role or claim KYC verification.

Profile version is separate from ownership version. Updating contact/name data must not transfer ownership or invalidate consent grants. C&I owns the additive customer_profiles table and append-only customer_profile_receipts. Optimistic version checks and operation fingerprints guard stale/conflicting/repeated mutations. Current membership and ownership are rechecked after asynchronous dependencies. The page suppresses stale responses after a session/customer change, checks owner receipt identity, reloads confirmed state, and refreshes the workspace customer label without hiding the page.

Prepared wiring includes /profile navigation and direct-route protection, the Gateway proxy, C&I endpoints and PostgreSQL repository, runtime grants and a hash-checked additive migration overlay. This is an intrinsic self-service identity operation, with no advisor/admin profile-edit API and no new business-role grant.

## Runtime interruption and evidence limits

The initial C&I service build and initial TypeScript check passed before all final draft edits. Those earlier results do not establish correctness of the complete draft. The local execution environment then returned: environment registry request failed (409 Conflict, environment_offline): Environment is not connected. The attempted SelfCustomerProfileTests.cs write failed, and its test run did not start. Initial changed-file lint returned a nonzero status; the log was inaccessible after disconnection, so lint must be read and corrected on resume. No final T1-44 test, build, lint, migration execution or Expert synchronization is claimed.

The available GitHub connector was used to preserve the draft source and wiring so it is reviewable and recoverable without depending on the disconnected scratch environment. No GitHub Actions were started and no draft changes are merged.

## Resume sequence

1. Check out the draft branch and compare/recover any retained local changes without discarding them. Use the remote draft as the durable recovery baseline.
2. Add focused owner/application and actual loopback HTTP tests for foreign tenant/session, missing/ambiguous ownership, malformed owner binding, ownership/membership changes around reads and saves, validation, stale versions and explicit post-commit uncertainty.
3. Verify the PostgreSQL adapter's operation replay, conflicting operation ID, optimistic update and rollback semantics against real owner persistence when a supported runtime is available. Verify that profile changes do not mutate customer ownership version.
4. Add UI checks for loading/saving, exact operation receipt and owner reload, conflict/uncertainty blocking a second save, stale response cancellation, no cross-customer data, read-only registered email, unverified mobile wording and updated workspace label. Include anonymous direct-route protection.
5. Run the focused native checks, TypeScript and changed-file lint; resolve findings. Confirm the migration SHA and scope lock, then build the customer UI, Gateway and Migrator as affected. Do not rerun unrelated complete pipelines.
6. Record exact candidate/source evidence and prepare the mandatory paired System Expert delta and source synchronization before marking the unit complete. User already authorized autonomous implementation/merge; no renewed permission is required.

MON-ID-005/012 advance only within this bounded profile scope. Public self-registration/new ownership, published terms/privacy acceptance, mobile OTP, password/PIN/MFA, KYC and account closure remain open; no legal text or provider approval has been invented.
