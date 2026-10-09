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
