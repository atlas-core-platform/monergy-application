# Security Policy

Never commit credentials, tokens, private keys, provider secrets, connection
strings containing credentials, or protected customer/document payloads.

Local and CI configuration contains non-secret values and secret references
only. Human, build, deployment, migration, and runtime workload identities stay
distinct. Report a suspected secret or protected-data exposure through the
governed security process; do not include the value in an issue or log.

MWP-03-D02 pins Gitleaks for history/candidate secret scans, Syft for CycloneDX
SBOMs, Grype for vulnerability scanning, language-native advisory scans, .NET
analyzers and ESLint security rules. Scanner error or unavailability is never
converted to `PASS`. Production security-platform selection remains open.
