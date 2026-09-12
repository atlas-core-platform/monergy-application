# Monergy Application Repository Structure

The D05 application root has exactly six governed responsibility areas:

```text
monergy-application/
├── .github/              GitHub Actions and CODEOWNERS governance
├── apps/                 Reserved customer and administration web applications
├── services/             Twelve independently owned R3 service boundaries
├── contracts/            Transport-neutral D03 contract implementation boundary
├── shared/platform/      Reusable technical platform concerns only
├── tests/                Contract, integration, end-to-end, and bootstrap evidence
└── build/                Local/CI bootstrap, policy, release, and supply-chain logic
```

`.github/` realizes hosted repository governance; it does not add a seventh
application responsibility. Root policy files do not add application
responsibilities. `.artifacts/` is
ignored generated evidence. No product source files, persistence schemas,
migrations, provider integrations, credentials, or environment deployments are
introduced by D01.

See the catalogs and README files in each responsibility area for the maintained
boundary inventory.

`build/governance/github-free-governance-exception.md` records the accepted D01
compensating controls and expiry conditions for the private GitHub Free
repository.
