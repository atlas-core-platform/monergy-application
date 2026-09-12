# Vendor-Neutral CI Contract

`gates.json` preserves all eleven accepted D05 gate categories. `pipeline.json`
orders them into the five D05 logical stages without selecting a hosted runner.

`IMPLEMENTED_EXECUTABLE` means D01 supplies a locally executable bootstrap check.
`CONFIGURED_REQUIRES_EXTERNAL_INFRASTRUCTURE` means the control is represented
but needs a trusted hosted capability. `DEFERRED_OPEN_DECISION` means stack or
packaging selection is required before a truthful executable implementation is
possible. D07 result states remain separate.

