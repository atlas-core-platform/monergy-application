# GitHub Actions Bootstrap Pipeline

`gates.json` preserves all eleven accepted D05 gate categories. `pipeline.json`
orders them into the five D05 logical stages. GitHub Actions is the approved
hosted execution platform for this repository; the gate semantics remain
portable and vendor-neutral.

`.github/workflows/bootstrap.yml` executes the locally reproducible bootstrap
checks on pushes to `main`, pull requests, and explicit workflow dispatch. The
workflow uses read-only repository permissions and a commit-pinned checkout
action.

`IMPLEMENTED_EXECUTABLE` means D01 supplies a locally executable bootstrap check.
`CONFIGURED_REQUIRES_EXTERNAL_INFRASTRUCTURE` means the control is represented
but needs a trusted hosted capability. `DEFERRED_OPEN_DECISION` means stack or
packaging selection is required before a truthful executable implementation is
possible. D07 result states remain separate.
