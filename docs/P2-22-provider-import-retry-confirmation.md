# P2-22 Provider Import Retry Confirmation

## Bounded journey

A Customer importing a supported file from the configured Google Drive or OneDrive folder can safely recover when the browser does not receive a confirmed result.

- Customer Web retains the exact connection revision, provider file identifier and immutable provider file version.
- The failed view preserves the provider page and disables different imports until the exact pending import is confirmed or the Customer explicitly refreshes.
- **Retry exact import** resubmits only the retained identity. It cannot substitute another file, version, Customer, tenant or provider.
- The connector owner returns the existing document receipt when the exact import already completed. Concurrent exact retries produce one Evidence registration.
- Every attempt still re-evaluates the current authenticated owner, connection revision, provider configuration, authorization, Consent, folder membership and provider file version before an Evidence intake effect.

## Ownership and safety

Integration Gateway owns connector execution and the exact provider-version replay receipt. Evidence owns document registration and scanning. Customer & Identity, Access Management and Consent retain their existing authority boundaries.

No provider token, authorization code, credential, raw provider response or Consent detail is sent to Customer Web. A changed connection, owner, role, Consent, folder, configuration or provider file fails closed and requires a fresh review instead of replaying a stale request.

## Acceptance boundary

Source validation covers exact retry identity, duplicate-effect prevention, current-boundary rechecks and Google Drive/OneDrive Customer UX. Physical PostgreSQL, live provider OAuth, installed-browser and retained LOCAL acceptance remain separate gates.
