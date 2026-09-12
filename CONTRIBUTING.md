# Contributing

Use short-lived change branches and submit changes to protected `main` through
review and applicable automated gates. Environment branches named `dev`, `qa`,
`uat`, or `production` are prohibited.

Under the MWP-03-D01 GitHub Free exception, `main` cannot be technically
protected while this organization repository remains private. Pull requests are
still mandatory by policy. CODEOWNERS is advisory, CI runs for every pull request
and push to `main`, and CI flags direct pushes after they occur. Force pushes and
deletion of `main` are prohibited. The exception and its immediate expiry
conditions are defined in
`build/governance/github-free-governance-exception.md`.

Changes must preserve service ownership, D03 contracts, D04 persistence and
migration boundaries, and D08 implementation-start guardrails. Shared platform
code may contain technical capabilities only; it must not become shared domain
authority. Do not add secrets, Production data, or cross-service persistence
access.

Product code must not be added until its runtime, language, dependency manager,
and verification toolchain have an approved engineering decision.
