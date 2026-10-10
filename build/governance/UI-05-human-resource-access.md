# UI-05 — Human-readable resource access

Status: Candidate
Authority: AR-002
Source application main: 75d98f7d7e78efcbb4cd6c1544667b23206e0ddc

## Scope

- Remove manual raw resource-ID entry from Access Management administration.
- Resolve selectable resources through an owner-backed directory.
- Display business labels while submitting/storing immutable resource IDs.
- Keep Access Management free of duplicated customer/business labels.
- Fail closed when no owner-backed directory exists for a resource type.
- Provide a bounded LOCAL/UAT C&I reference directory for the customer resource type.
- Preserve all existing Access Management authorization semantics and service-side enforcement.

## Non-goals

- No Production customer-directory implementation.
- No automatic customer-self resource assignment yet.
- No advisor/client relationship domain workflow yet.
- No document/report resource directory yet.
- No change to Access Management database storage.

## Acceptance signals

1. Resource creation UI contains no free-text raw-ID field.
2. Customer resources are shown by human-readable label in LOCAL/UAT.
3. Selected labels resolve to immutable IDs before the existing AM grant API is called.
4. Unsupported resource types do not fall back to manual identifier entry.
5. Existing resource lists do not expose raw IDs as their primary label.

## Continuation integration correction

The customer directory is registered with C&I's tenant-session composition, which Docker UAT actually runs. LOCAL/CI customer display fixtures must match the operator-owned customer/tenant bindings used by the owner boundary. Unknown tenants return no resources; invalid/mismatched configuration fails closed. Directory labels neither prove self-ownership nor grant access. Production and reference-adapter composition are rejected by this bounded adapter.

Disposable Docker acceptance now checks unauthenticated/non-admin rejection, tenant isolation, named directory entries with matching IDs, and unsupported resource types. This adds checks; it is not a claim that Docker acceptance has run on this candidate.

Local continuation evidence: 26 owner-boundary unit tests (including 8 directory cases), 30 frontend component tests, and 22 targeted browser regressions pass. Frontend typecheck/lint/build and gateway build pass. Unsupported directory types return 422 without discarding an otherwise valid session; actual owner failures still fail closed. Raw output: [UI-05 continuation evidence](evidence/ui05-continuation-20261010.txt). The local Node runtime is 24.19.0; the repository's 24.21.0 pin is unchanged. Hosted and Docker checks remain pending.
