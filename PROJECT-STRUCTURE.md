# Monergy Application Repository Structure — D03 Candidate

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

D02 provides the accepted toolchain foundation. D03 adds:

- a transport-neutral `Monergy.Contracts` assembly and closed VS-02 JSON schema;
- executable application/domain ports in Evidence, Document Intelligence,
  Financial Profile, Job Management, and Audit;
- LOCAL/CI-only in-memory, fixture and append-only reference adapters;
- `build/governance/vs02-scope-lock.json` with exact 8-Feature, 14-contract,
  and 5-service scope;
- `apps/customer-web/src/vs02/` as a separately loaded, clearly labeled
  reference experience;
- `tests/vs02/` and frontend component/accessibility/Playwright evidence;
- `build/verify-vs02.ps1` with deterministic checks and negative self-tests.

D02 assets retained include:

- `.dockerignore` for the controlled OCI build context;
- exact .NET/Node/pnpm/package pins and dependency locks at the root;
- twelve independent .NET service/worker projects and OCI definitions;
- `shared/platform/Monergy.Platform/` for vendor-neutral technical bootstrap;
- `shared/platform/frontend-ui/` for shared semantic tokens and Ant/Tailwind
  integration;
- the `apps/customer-web/` toolchain-verification shell;
- architecture, component/accessibility and browser smoke tests;
- build, supply-chain, release-manifest and deterministic verification scripts.
  The hosted OCI script emits twelve archive/digest/SBOM/scan rows without
  publishing images.

No physical persistence schema, migration, provider SDK/integration,
credential, persistent-environment deployment or artifact publication is
introduced. In-memory/fixture behavior is reference evidence, never a
Production adapter.

See the catalogs and README files in each responsibility area for the maintained
boundary inventory.

`build/governance/github-free-governance-exception.md` records the accepted D01
compensating controls and expiry conditions for the private GitHub Free
repository.
