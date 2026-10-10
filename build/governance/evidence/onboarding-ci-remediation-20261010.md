# Onboarding candidate: CI failure remediation

Date: 2026-10-10 UTC. Scope: the LOCAL/UAT candidate in application PR #43.
This record does not accept the candidate, authorize a merge, or authorize Production.

## Hosted evidence reviewed

Application candidate: `89a3a8df1f4a1995512dbc5145a7f998a2fc8533`.

- [Continuous verification #144](https://github.com/atlas-core-platform/monergy-application/actions/runs/38047101537)
  failed at D01's root layout check. It expected the original six responsibility
  roots and rejected the CP-01 `platform/control-plane` addition. Restore,
  formatting, build, architecture checks, secret scan, impact policy, selected
  service tests and frontend component tests passed before this failure.
  Later verifiers and dependent jobs did not complete; their hosted acceptance
  cannot be inferred from those successful earlier steps.
- [Docker UAT #21](https://github.com/atlas-core-platform/monergy-application/actions/runs/38047148212)
  passed both Windows profile-permission checks and built the runtime image.
  PostgreSQL became healthy; the bootstrap container exited with code 139.
  The workflow did not collect container output before deleting the disposable
  profile. Owner and browser acceptance tests were skipped. The underlying
  startup failure is unresolved; the exit code alone is not a diagnosis.
- [Tenant Access owner integration #33](https://github.com/atlas-core-platform/monergy-application/actions/runs/38047132880)
  passed on the same application candidate.
- [Access Management verification #27](https://github.com/atlas-core-platform/monergy-access-management-service/actions/runs/38047073048)
  passed on `fb242bfccd161e49d63223b0d32c68fdf770a024`, the unchanged source archive
  used by this UAT candidate. Owner verification does not establish integrated
  application UAT acceptance.

## Bounded changes

- The root verifier now requires the seven current governed roots and permits
  only `control-plane` under `platform`. Negative cases still reject missing
  or unrelated roots and unrelated platform boundaries. The twelve business
  service boundaries are unchanged.
- Docker explicitly preserves the intended `1777` permissions for the isolated
  onboarding data directory when copying it into the final image. This supports
  the existing dynamic non-root UAT user. It is permission hardening, not a
  demonstrated explanation or repair of the exit-139 failure.
- Bootstrap progress and failure messages identify the controlled startup stage,
  exception type and optional PostgreSQL SQLSTATE. Exception messages and
  connection strings are not logged.
- On UAT failure, read-only container status and bounded logs are captured before
  cleanup. Profile credentials and transient session credentials are redacted.
  A credential-redaction self-test runs before startup. Only the redacted report
  is added to the acceptance artifact; the private profile is never uploaded.
- The immutable Access Management dependency manifest links the successful owner
  run and continues to record integrated UAT as pending.

## Local verification and limits

The updated root verifier passed 27/27 checks and 10/10 self-tests. The sixteen
direct D01-D16 verifier scripts passed with their existing forward-regression
arguments; D05 used a fresh 72/72 passing financial-rules test run and its TRX.
These direct script results do not include Docker, physical database jobs,
browser acceptance, D11 process/recovery companion scripts, or the complete
hosted toolchain wrapper.

The migrator published successfully using .NET SDK 10.0.401 and locked restore.
Launching that published output with a deliberately nonexistent profile returned
the expected exit 1 and the sanitized `profile-validation: FileNotFoundException`
message. This proves local host startup and the failure message, not startup in
the Docker image. C# whitespace, touched JSON/YAML/Markdown formatting,
PowerShell syntax, diagnostic redaction and diff whitespace checks passed.

Docker is unavailable in this execution environment. The required next evidence
is a **new dispatch** of Docker UAT and Continuous verification (`impacted`) on
`delivery/onboarding-integrated-uat` after the remediation commit is published.
Re-running #21 or #144 would retain the old candidate SHA. If UAT still fails,
inspect the `local-uat-acceptance` artifact's `uat-diagnostics.log` before proposing
the next correction. Merge and integrated acceptance remain pending.
