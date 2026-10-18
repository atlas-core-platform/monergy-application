# Customer account agreements (T1-53)

Account agreements appears under **Your account** at `/settings/agreements` for
an authenticated customer. It records explicit acceptance of the exact published
terms, acknowledgment of the privacy notice and processing consent. This is a
bounded registration prerequisite for existing imported customers, not a public
registration/activation flow, new role/ownership grant or provider/advisor consent.
It does not gate or activate existing workspace access.

## Publish approved content locally

No legal text or acceptance is supplied by default. Until an operator publishes
approved documents for the customer's exact tenant, the page explains that the
documents are unavailable and exposes no acceptance action.

Provide `-AccountAgreementFile <absolute-json-path>` when preparing/starting a
named profile with `build/uat/Invoke-MonergyUat.ps1 -EnableOnboarding`. Use the same
file on subsequent preparation/start calls. The protected generated C&I
configuration receives `Monergy.AccountAgreements` from this file. Never commit
customer/private legal publication files as test fixtures.

The file has one root property, `Publications`, containing tenant publications.
Each publication has `TenantId`, `PublishedAt` and `Documents`:

- `TenantId`: exact existing tenant identity.
- `PublishedAt`: an explicit UTC timestamp in .NET round-trip format, for example
  `2026-10-18T10:00:00.0000000+00:00`. Future publications remain unavailable until
  this instant. An invalid publication fails C&I startup.
- `Documents`: exactly three records with distinct `Kind` values `TERMS`,
  `PRIVACY` and `CONSENT`. Each has `Version` (1–64 ASCII letters/digits/dot/dash/
  underscore), `Title` (1–120 characters), the approved exact plain-text `Body`
  (1–32,000 characters), and lowercase `Sha256` of that body's UTF-8 bytes.

Hashes cover the decoded string, including line breaks. Nothing trims, rewrites
or renders document text as HTML. The owner derives the bundle revision from
the exact tenant, publication timestamp and sorted versioned documents; changing
any covered content requires a fresh review. The client cannot supply legal text,
customer identity, actor, timestamp or permission grants to the mutation.

Both new tables are C&I-owned, tenant-database scoped and append-only to the
runtime role. An accepted publication snapshot and the three affirmative choices
are committed together with an operation receipt. Replaying that exact operation
returns its original receipt; an operation reused with a different binding or
revision conflicts. New operations against an already recorded current revision
also conflict and require readback. Older publication/acceptance rows are retained.
Customer ownership/profile versions and existing Consent grants are unchanged.

## Focused verification and remaining acceptance

Owner/session tests and real local HTTP boundary tests use explicit repository
doubles. UI tests exercise exact-body hashes, no default consent, three explicit
choices, receipt/readback validation and stale-response suppression. Synthetic
test documents are labeled test fixtures, never approved legal text.

The physical PostgreSQL transaction/replay/concurrency/runtime-grant checks,
PowerShell/Compose execution and browser-to-database acceptance require local UAT.
No successful publication, customer acceptance or legal approval is inferred from
unit tests. Public signup, account activation gating, basic KYC, full Settings and
withdrawal/retention policy workflows remain separate planned work.
