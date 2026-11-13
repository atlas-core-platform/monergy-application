# P3-03 — Financial Advisor operations workspace

## Implemented journey

An authenticated Financial Advisor can open a currently authorized assigned customer from the
existing work queue, review source-backed financial facts, record an advisory preparation note
against selected canonical fact identifiers, record a customer communication, and advance a
bounded application-preparation stage through:

`Not started → Preparing → Documents requested → Ready for submission`

The journey deliberately stops before submission to a lender. It does not perform underwriting,
pricing, approval, rejection, disbursement, email, SMS or notification delivery.

## Authority boundary

Access Management Default Access Pack 2.7 adds only
`application-case.advisor-engagement.manage` to the Financial Advisor role. The capability is a
Customer resource mutation but is granted tenant-wide only as an operation eligibility flag.
Every customer read or mutation also requires both previously approved read capabilities,
the active advisor role, a current customer assignment, exact resource grants for the two read
capabilities, and live Customer Consent. The Application & Case owner compares policy, subject,
customer and Consent versions before committing a mutation.

Request Access remains the separate bounded mechanism for the two read permissions. It never
creates an assignment, resource grant or Consent. Customer Identity owns customer status and
labels; Consent owns sharing; Financial Profile and Evidence remain authoritative for the facts
and documents; Application & Case persists only canonical fact references and engagement data.

## Integrity and persistence

- Tenant, actor and customer authorization context are derived from the current session and
  owner decisions, never accepted in a mutation body.
- Mutations require an actor-bound idempotency key and expected engagement version.
- An authority recheck occurs inside the mutation boundary.
- Activities and operation receipts are append-only. Only the engagement version, current
  preparation stage and updated timestamp are mutable.
- Tenant database binding and schema version 2 are checked exactly at runtime.
- Runtime roles can append activity/provenance but cannot rewrite or delete history, change the
  advisor/customer binding, create schema objects or truncate tables.
- LOCAL reference data reports its temporary/durable test posture and makes no Production claim.

## Deferred

Outbound communication delivery, customer replies, arbitrary workflow configuration, lender
submission/decisions, automated advice and production deployment acceptance are not part of
P3-03. P3-04 owns the separate Lender Professional workspace.
