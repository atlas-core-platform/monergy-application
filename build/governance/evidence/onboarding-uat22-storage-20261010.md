# Docker UAT #22: onboarding storage correction

Date: 2026-10-10 UTC. LOCAL/UAT candidate in application PR #43 only.

## Observed failure

[Docker UAT #22](https://github.com/atlas-core-platform/monergy-application/actions/runs/38058622256)
ran on `11cf03772640cd9561f59648fb4a626def3dcd5f`. Its redacted diagnostics show:

```text
Local UAT bootstrap: starting.
The local registry could not record the failure; provisioning remains incomplete.
Local UAT bootstrap failed at tenant-registry: UnauthorizedAccessException.
```

The container exited with **1**, unlike #21's unclassified exit 139. PostgreSQL
was healthy; bootstrap had advanced through cluster initialization into registry
creation. The reported PowerShell line is the wrapper propagating this failure.
Owner acceptance and browser acceptance did not execute.

The log also reports a missing GSSAPI library before reaching the registry
stage. That message did not prevent this database connection. No authentication
mode, PostgreSQL policy or runtime dependency is changed to suppress it.

## Correction and regression check

The previous image copied an empty source directory directly onto the target
mount path and relied on `COPY --chmod` to set that destination's mode. It did not
resolve #22. Docker's [directory-copy semantics](https://docs.docker.com/reference/dockerfile/#copy)
copy the source contents, including subdirectory metadata, rather than the source
directory itself.

The builder now prepares `monergy-onboarding` **inside** `/runtime-data`, sets its
intended mode to `1777`, and copies that parent tree into `/var/lib`. The directory
is therefore a copied child with its own metadata. No runtime root initializer,
permission relaxation on other paths, or volume reset is introduced.

A required image-build command runs as UID 1001 and exercises the real
`FileTenantRegistry`: create, atomic replacement, fresh-instance readback and
cleanup. It uses uniquely named probe files and never opens `registry.json` or
`setup.json`. The final image retains its existing default UID 1654; Compose
retains its existing configured non-root user and hardened runtime settings.

For onboarding profiles, bootstrap performs the same check against the mounted
volume before database provisioning. A failing check stops startup and records
the `onboarding-storage` stage. Directory mode is logged without credentials;
existing registry, setup and database data are preserved.

## Local evidence and acceptance boundary

The migrator published successfully with .NET SDK 10.0.401, locked restore and
analyzers enabled. C# whitespace and documentation formatting checks passed.
The published storage probe passed five local checks:

| Case                                             | Result                                                     |
| ------------------------------------------------ | ---------------------------------------------------------- |
| Writable directory: create, replace and readback | Exit 0                                                     |
| Repeat invocation and cleanup                    | Exit 0; no probe files remain                              |
| Directory without write permission               | Exit 1; `UnauthorizedAccessException`                      |
| Missing directory                                | Exit 1; `DirectoryNotFoundException`; no directory created |
| Production environment                           | Exit 1 before any probe operation                          |

Existing `registry.json` and `setup.json` sentinel files remained byte-for-byte
unchanged. The permission-negative check used mode `0555` in the local process;
the UID-1001 Docker build check has been added but cannot be executed locally
because this environment has no Docker engine.

[Continuous verification #145](https://github.com/atlas-core-platform/monergy-application/actions/runs/38058603729)
subsequently completed successfully on `11cf03772640cd9561f59648fb4a626def3dcd5f`.
That result covers the preceding candidate, not this new storage correction.

Fresh hosted Docker UAT and Continuous verification on the remediation commit
remain required. This record does not claim Docker acceptance, authorize a merge,
or authorize Production.
