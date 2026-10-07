# Impact-Aware GitHub Actions Pipeline

MWP-03-D15 replaces deliverable-number-driven cumulative verification with a
deterministic changed-file and architecture-impact classification. The
repository-owned policy is `../governance/ci-impact-map.json`; the executable
classifier is `Get-CiImpact.ps1`. Its JSON result names affected services,
focused test projects, historical semantic verifiers, OCI images and
conditional heavy gates. Any unmapped non-documentation path escalates to full
regression rather than silently selecting no verification.

`gates.json` preserves all eleven accepted D05 gate categories. `pipeline.json`
orders them into the five D05 logical stages. GitHub Actions is the approved
hosted execution platform for this repository; the gate semantics remain
portable and vendor-neutral.

Automatic Actions run only on pushes to `main` after merge; opening or updating
a pull request does not start verification. This policy applies to both
`bootstrap.yml` and `access-owner-integration.yml`. Manual dispatch remains
available for targeted verification. Passing post-merge runs provide the hosted
verification evidence for the merged revision.

`.github/workflows/bootstrap.yml` keeps a universal clean baseline: locked
restore, format, lint/source security, build,
architecture boundaries, candidate secret scanning, impact validation and the
D15 verifier. Service, contract, browser, physical persistence, D11 runtime,
dependency-security and OCI work is then selected from the classifier output.
Only rebuilt images receive image SBOM and vulnerability evidence.

Manual workflow dispatch exposes `impacted` and `full_regression` modes. Full
regression selects D01 through D16, all focused test projects, browser and
physical integration lanes, all twelve images, and repository/image security
analysis. D15 defines no recurring schedule.

D16 release-path changes select D01, D02, D15 and D16 without enabling browser,
physical persistence, D11 runtime or complete OCI lanes. The older D05
acceptance-manifest paths remain separately classified to D05 so their historical
semantics cannot be changed without rerunning that verifier.

On each push to `main`, the workflow checks whether the commit is associated
with a merged pull request. A direct push is surfaced as a GitHub Actions warning
and job-summary finding. Under the accepted GitHub Free exception this is a
detection control, not prevention; pull requests, no force-push, and no branch
deletion remain mandatory policy.

Historical D13 and D14 verifiers inspect their accepted Feature semantics and
governed boundaries only. Current workflow topology is verified by D15, so
future job names and layouts do not falsify historical acceptance evidence.

`IMPLEMENTED_EXECUTABLE` means D01 supplies a locally executable bootstrap check.
`CONFIGURED_REQUIRES_EXTERNAL_INFRASTRUCTURE` means the control is represented
but needs a trusted hosted capability. `DEFERRED_OPEN_DECISION` means stack or
packaging selection is required before a truthful executable implementation is
possible. D07 result states remain separate.
