# Contributing

Use short-lived change branches and submit changes to protected `main` through
review and applicable automated gates. Environment branches named `dev`, `qa`,
`uat`, or `production` are prohibited.

Changes must preserve service ownership, D03 contracts, D04 persistence and
migration boundaries, and D08 implementation-start guardrails. Shared platform
code may contain technical capabilities only; it must not become shared domain
authority. Do not add secrets, Production data, or cross-service persistence
access.

Product code must not be added until its runtime, language, dependency manager,
and verification toolchain have an approved engineering decision.

