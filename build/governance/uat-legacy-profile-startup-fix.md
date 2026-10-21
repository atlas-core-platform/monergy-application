# UAT legacy profile startup repair

Fixes the reported jobAuditEvent and onboarding StrictMode failures when retained pre-onboarding profiles start. Compose uses the normalized legacy mode. All profiles receive missing audit delivery tokens on Prepare/Start; retained tokens and database credentials are preserved. Missing foundational credentials fail with recovery guidance instead of being regenerated.

Also replaces five .NET-only hexadecimal conversions for Windows PowerShell 5.1 compatibility, accepts the pinned Access Pack 2.5, and uses the retained profile port for provider callback validation.

Validation: Test-LegacyProfiles.ps1 runs real PowerShell configuration generation for fresh, legacy default and onboarding profiles, missing-token migration, repeat-run stability, and existing credential preservation. Start/Status/Verify/Stop use mocked Docker and readiness responses. Pinned Access Management sources are independently checked using restore-source.mjs. Windows ACLs and live Docker startup are not verified in this Linux environment. No production acceptance or tracker completion counts advance.
