# Trusted Financial Summary, Drill-Down & Export — D08 candidate

The Reporting Service implements exactly three MWP-03-D08 Features at `SIMULATOR`
evidence level through reference adapters restricted to `LOCAL` and `CI_EPHEMERAL`.

It owns report identity, generation state, export metadata, and associations to
authoritative sources. Evidence, Financial Profile, Financial Rules, and Audit retain
their respective authority. The deterministic JSON export is a format-neutral reference
artifact, not a formal report pack, PDF, spreadsheet, or delivery-channel commitment.

Run locally with `Monergy__ReferenceAdapters=true`, `Monergy__ExecutionZone=LOCAL`, and
`dotnet run --project services/reporting/Monergy.Services.Reporting.csproj --urls http://127.0.0.1:5189`.
