# P3-05 — Financial Professional workspace

## Accepted bounded journey

The Chartered Accountant / Financial Professional receives one explicit workspace capability:
`application-case.financial-review.certify`. The capability admits the persona to the workspace; it does not grant
unrestricted Financial Profile, Evidence, Reporting, tax, audit or customer access.

Application & Case owns the exact review assignment and its immutable activity. Consent remains a separate owner.
Before a review is listed or mutated, the service revalidates the authenticated session, current Financial Professional
capability, active customer, and current customer sharing for both `financial-profile.profile.read` and
`evidence.document.read`. Tenant, actor, customer, permission and assignment identifiers never come from mutation bodies.

## Source and signer integrity

- Every assignment contains canonical source references, owner labels and assigned/current source versions.
- A source cannot be marked reviewed after its current version diverges from the assigned version.
- Professional notes must cite one or more assigned source codes.
- Certification requires every assigned source to be reviewed at the unchanged version.
- The certification snapshot retains signer actor, time, policy, subject, customer and Consent versions plus every exact
  source owner/reference/version.
- Events, notes, certification-source snapshots and operation receipts are append-only. Browser/network retries are
  idempotent; stale review versions and operation reuse fail closed.

## Professional boundary

Monergy does not infer a professional certification from customer-entered data, OCR, a model response or a calculation.
The signer chooses one bounded outcome: `Certified with no exceptions`, `Certified with exceptions`, or
`Unable to certify`, and records the rationale. This journey does not claim statutory audit, tax-return preparation,
regulatory filing, provider validation or legal assurance.

## LOCAL/UAT

Actor `A600` represents the Financial Professional. The retained profile adopts Default Access Pack 2.9 and assigns the
new permission only to the `financial-professional` role. A reference review is explicitly assigned in Application & Case.
The customer must separately share both financial-information and document-read scopes with A600 before the assignment is
visible. Revocation or expiry immediately blocks listing and mutation.

The physical owner schema is version 4. Runtime privileges allow only the review-state columns required by the journey and
append-only inserts into notes, events, operation receipts and certification-source provenance. Assignment, customer and
source-version fields remain owner-only.

## Deferred

- provider or professional-body credential verification;
- statutory audit/tax forms, regulatory filing and electronic signature providers;
- automatic source ingestion from production Financial Profile/Evidence owners;
- multi-professional engagement allocation and reassignment UI;
- production notification and billing workflows.
