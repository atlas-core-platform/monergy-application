# Security Policy

Never commit credentials, tokens, private keys, provider secrets, connection
strings containing credentials, or protected customer/document payloads.

Local and CI configuration contains non-secret values and secret references
only. Human, build, deployment, migration, and runtime workload identities stay
distinct. Report a suspected secret or protected-data exposure through the
governed security process; do not include the value in an issue or log.

MWP-03-D01 configures local detection only. Selection and operation of the
Production-grade secret, SAST, composition, artifact, and vulnerability scanning
platforms remains pending approved engineering/platform decisions.

