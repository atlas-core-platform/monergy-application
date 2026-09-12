# Monergy Application Repository Structure — D02 Candidate

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

`.github/` realizes hosted repository governance and technology-specific CI; it
does not add a seventh application responsibility. Root policy/toolchain files do
not add application responsibilities. `.artifacts/` and `.toolcache/` are ignored
local evidence/tool homes.

D02 adds:

- `.dockerignore` for the controlled OCI build context;
- exact .NET/Node/pnpm/package pins and dependency locks at the root;
- twelve independent .NET service/worker projects and OCI definitions;
- `shared/platform/Monergy.Platform/` for vendor-neutral technical bootstrap;
- `shared/platform/frontend-ui/` for shared semantic tokens and Ant/Tailwind
  integration;
- a minimal `apps/customer-web/` toolchain-verification shell;
- architecture, component/accessibility and browser smoke tests;
- build, supply-chain, release-manifest and deterministic verification scripts.
  The hosted OCI script emits twelve archive/digest/SBOM/scan rows without
  publishing images.

No product Feature, persistence schema, migration, provider integration,
credential, environment deployment or artifact publication is introduced.

See the catalogs and README files in each responsibility area for the maintained
boundary inventory.

`build/governance/github-free-governance-exception.md` records the accepted D01
compensating controls and expiry conditions for the private GitHub Free
repository.
