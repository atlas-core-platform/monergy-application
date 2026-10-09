# UI-04 — Focused access administration

Authorized by the platform owner on 2026-10-09. This redesign changes presentation and navigation within existing LOCAL/CI contracts. It does not change Production acceptance or any D01–D16/SG gate.

## Evaluation and navigation decision

The previous access page combined a promotional hero, operation cards, six tabs and a platform sidebar. Evidence, Search, Reports, System Expert and Engineering Foundation competed with administration tasks. Connected Services and Release Readiness exposed operator concerns in the tenant workspace. New user creation required preparing a CSV, and reviews appeared above the editing drawer as a second dialog.

The module now owns eight sidebar destinations. The shared shell accepts a module identity, home link and guide; its command palette uses only that module’s destinations. Platform destinations remain at `/`, and the independent System Expert retains its own menu. Internal navigation preserves the in-memory session; reload still requires authentication. Unknown access routes do not fall through to engineering content.

| Destination          | Route                   | Responsibility                                                |
| -------------------- | ----------------------- | ------------------------------------------------------------- |
| Overview             | `/access`               | Current access snapshot and task guidance                     |
| Users                | `/access/users`         | Create users; change role, status and administrator authority |
| Onboarding history   | `/access/users/history` | Secondary Users journey: inspect and recover requests         |
| Roles                | `/access/roles`         | Define roles and select permissions                           |
| Permissions          | `/access/permissions`   | Configure capability and scope grants                         |
| Groups               | `/access/groups`        | Organize users without granting access                        |
| Resource Access      | `/access/resources`     | Assign and remove individual resource grants                  |
| Access Activity      | `/access/activity`      | Review canonical access audit delivery                        |
| Security & Sessions  | `/access/sessions`      | Inspect session records; revoke all sessions for a user       |
| Local UAT operations | `/operations`           | Separate platform operator diagnostics and release gates      |

## Interaction and visual model

Compact page headers replace repeated heroes and tabs. Each administration list has one primary creation action; Users keeps imports and history secondary. Navy navigation, light work surfaces, self-hosted Inter, restrained teal accents and consistent spacing use the shared foundation. Keyboard navigation, narrow-screen drawers and reduced-motion behavior remain supported.

Editors show a review step within the same drawer, preserving the draft when returning. Existing records display before/after values. Member changes explicitly warn about session invalidation, administrator authority, disablement and removal of individual resource grants when the business role changes. Roles/users expose configured permission summaries; these are not authoritative effective-access decisions.

## Contract and security boundary

Create User serializes one CSV row behind the form and uses the existing AM-06 preview/commit/process contract. The form accepts email, one optional existing role and optional organizational groups. It does not fabricate an identity, send invitations, configure credentials or grant TenantAdmin. Server validation, content hash, stable operation ID and policy revision remain authoritative. An uncertain commit locks replacement content and exposes status recovery. Pending/failed responses never imply successful activation. Retry uses the current policy revision and retains the operation ID. Bulk onboarding retains the same behavior.

All administration is server-authorized. API paths, persistence, audit, tenant/session headers, last-administrator protection and production security gates are unchanged. Authorization failures clear the current workspace. Selection pagination rejects mixed policy revisions; editor selections also match the revision of the edited record. Role configuration and resource assignment summaries do not replace service-side authorization evaluation.

Overview counts cover up to 100 records per category with an explicit + for additional records. The unassigned-role figure is explicitly limited to loaded users. List filters only search loaded records. No aggregate-count or server-search API is invented.

## Scope deliberately unchanged

One business role per user; groups do not inherit access. User name/email editing, multiple roles, invitation/password/SSO flows, other-user effective-access evaluation, individual-session revocation and a resource picker require separate backend decisions and approval.

## Validation

Component tests cover denied authority, stale edits, expiry cleanup, revision-safe pagination, invalid bulk and single-user input, reviewed payloads, interrupted commit recovery without duplicate staging, failed processing and eventual activation. Browser tests cover module-only navigation/guide/search/home, direct routes and history, in-memory session continuity, unknown paths, responsive navigation, keyboard/focus, drag/drop and review draft preservation. Existing reference journeys remain regression tested.

Local visual inspection and axe checks cover the rendered workspace; automated scans are not a universal accessibility certification. Hosted Docker UAT validates real single-user and bulk onboarding, session records, canonical audit and separately routed operator tools against the actual containers. Actions remain main-push/manual only.
