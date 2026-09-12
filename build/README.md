# Build and Delivery Bootstrap

The D01 bootstrap uses repository-local PowerShell validation because that is
already the architecture repository's deterministic verification convention. It
does not select the future product implementation language, framework, package
manager, CI service, artifact registry, deployment platform, or scanner vendor.

`Invoke-Bootstrap.ps1` exposes restore, format, lint, static analysis, test, CI
configuration, secret scan, dependency scan, and build-smoke tasks. Generated
evidence is local and ignored. It cannot be promoted or deployed.

