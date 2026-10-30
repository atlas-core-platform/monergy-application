# P2-33 — Customer-reported financial-goal lifecycle

## Bounded journey

M2-WS04-E02-F03 and MON-FIN-010 retain a broader financial-goal journey. P2-33 adds one bounded Customer capability: the Customer can report that an owned goal is **In progress**, **Paused**, **Reported achieved**, or **Stopped**, with an effective date. Financial Profile remains the owner. The authenticated session supplies tenant, actor and Customer; the browser supplies only the selected canonical goal, one allowed state, effective date, expected revision and retry operation.

Every state is explicitly `CUSTOMER_REPORTED`. `REPORTED_ACHIEVED` is not a verified achievement decision. The P2-30 progress projection continues to return `achievementStatus=NOT_DETERMINED`; the lifecycle report does not inspect payments, allocate savings, calculate required contributions, apply FX, recommend a plan or provide advice.

## Integrity and history

Lifecycle changes use the existing append-only financial-goal revision owner. Each accepted transition retains exact goal terms, declaration status, actor, operation, timestamp, effective date and lifecycle basis. Exact retries return the original receipt. Changed operation reuse, stale revisions, duplicate same-state transitions, backwards effective dates, unknown states and cross-goal reuse fail closed. Ordinary goal corrections preserve the current lifecycle fields instead of restoring the creation state. Historical payloads written before P2-33 restore as customer-reported In progress with no invented effective date.

Current Self `financial-profile.profile.read` and `financial-profile.profile.update` authority is required at Gateway ingress, at the owner and again before release. Read-only Customers keep list, detail, progress, contribution and history access without lifecycle mutation. Tenant/Customer binding and current actor, subject and Customer versions remain mandatory. No migration, role, grant, provider, package, service or GitHub Action is introduced.

## Customer experience

Financial goals display the current reported status. **Update status** opens a Midnight drawer with the current state, an allowed replacement state and an effective date. Pending requests disable duplicate edits. Uncertain results preserve the exact operation and payload for confirmation retry; confirmed invalid input permits correction, while a conflict requires refreshed review. Goal details and immutable history distinguish Customer-reported status from independent achievement.

## Deferred scope

Required-contribution schedules and methodology remain blocked by OD-08/C-10. Allocation/reconciliation, provider-confirmed payments, FX, verified achievement, automated lifecycle changes, prioritization, planning/advice, tax calculation/filing, semantic/vector Search, central Audit and physical/live acceptance remain open.

## Source validation

Focused Financial Profile goal/contribution/progress owner and Gateway tests, affected Customer Web tests, Release builds, TypeScript, ESLint, production build, bundle budget and lazy isolation are required before merge. Physical PostgreSQL, installed-browser, retained LOCAL and live provider acceptance are not claimed by this source increment.
