# MWP-03 — Monergy Midnight implementation candidate

Design approved by the product owner on 2026-10-09 after review of the MWP-03
specification and representative authentication, Users and review screens.
This record covers the bounded implementation candidate. Acceptance, merge and
release remain separate CTO/product-owner decisions. No Production or D01–D16/SG
acceptance is upgraded.

Application base: `11d3a938f4d7553de649e76428edfa3276f2980d`.
Access Management owner base: `4cd825c52b4db4f131360c5b0b619d6c97c5e921`.
System Expert base: `d480b7896fbf1a4a6fb4fef4ef76c4b986d31dc2` (unchanged).

## Ownership and existing guardrails

| Responsibility                                   | Existing owner / source                                                                      | Candidate treatment                                                                                 |
| ------------------------------------------------ | -------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| Local credential validation                      | Customer & Identity, `Application/TenantSessionAuthority.cs`, `ReferenceTenantAuthenticator` | Preserve key-based local reference authentication; no new IdP, credential or token format           |
| Session establishment, revocation and validation | Customer & Identity, `TenantSessionEndpoints.cs` and tenant-session authority                | Use the existing session endpoint; keep tokens only in volatile memory                              |
| Current administrator authority                  | Access Management, `/v1/administration/context`, `AdministrationContext.cs`                  | Verify matching tenant, actor, current session and TenantAdmin before publishing a frontend session |
| Protected shell lifecycle                        | `App.tsx`, `WorkspaceSessionProvider.tsx`, `WorkspaceEntry.tsx`                              | Gate the complete shell and portals, not just navigation visibility                                 |
| Shared components and tokens                     | `shared/platform/frontend-ui` and existing Ant Design                                        | Add opt-in Midnight appearance; retain default light appearance for other modules                   |
| Administration workflows                         | `src/access` and existing Access/C&I/Audit API owners                                        | Restyle and clarify existing workflows; preserve contracts, mutations and owner boundaries          |

The local key is an authentication credential bound to the configured tenant and
actor, not merely a workspace selector. The backend still validates membership,
subject version, current session and live administrator authority. The frontend
gate is a lifecycle and privacy boundary; it is not an authorization control.

## Verified-entry lifecycle

Public entry → identity verification → owner administrator verification → protected
workspace. A session is published only after both checks succeed. Invalid,
expired, malformed, mismatched or denied responses never mount the protected
shell. All local-UAT routes are gated; the separately built reference simulator
retains its existing public reference pages outside Access/Operations.

Expiry, sign-out, 401 and 403 remove the shell, records, drawers and volatile
session. Offline events and service failures remove protected content and show
reconnection. Tab resume and browser-cache restoration revalidate with the owner
before remounting. Reload requires a new connection. Generations and abort signals
prevent cancelled or superseded requests from repopulating a later session.
Requests retain a 20-second timeout. Known issued sessions from cancelled/denied
connections are revoked on a best-effort basis. Sign-out clears the browser first;
a failed server revocation is explicitly reported.

An interrupted onboarding commit retains only tenant, actor and original operation
ID in memory. After owner revalidation, the administrator can read the existing
request status. CSV contents, keys, drafts and records are not retained across
disconnection; no mutation is automatically replayed. Signing out clears the
recovery reference. Onboarding history remains the durable recovery journey.

## Visual and interaction specification

| Token or component                                | Candidate value / behavior                                                                    |
| ------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| Canvas / sidebar                                  | `#0B111C` / `#101B2B`                                                                         |
| Surface / elevated                                | `#172338` / `#203149`                                                                         |
| Accent / primary text / secondary text            | `#57C8BE` / `#F0F5FC` / `#91A5BF`                                                             |
| Decorative border / essential control border      | `#2B3C54` / `#657D9C`                                                                         |
| Semantic success / warning / danger / information | `#78D7AF` / `#F0C36C` / `#FF9B9B` / `#91BAFF`                                                 |
| Typography                                        | Existing bundled Inter; 13px body, 12px metadata, 18px section, 24px page title               |
| Density                                           | 36px desktop controls, 32px row actions, 44px minimum data rows; larger narrow/coarse targets |
| Sidebar                                           | 208px expanded, 64px collapsed; focused access destinations; keyboard and hover tooltips      |
| Drawers                                           | 480px single-user/editor, 650px bulk; responsive width, review in the same drawer             |
| Motion                                            | 120ms hover, 160ms transitions; reduced-motion override; no decorative animation              |

Eight Access destinations remain unchanged. Users has separate business-role and
administrator columns, loaded-record search/status filtering and explicit
pagination scope. Create user is primary; bulk import and onboarding history are
secondary. Roles, permissions, groups, resources, activity and sessions share
compact headers, panels, controls, notices and review styling. Drawers retain
before/after review and security-sensitive confirmations.

No multi-role membership, group-derived permissions, profile/email editing,
invitation/SSO/password flows, new resource picker, authoritative effective-access
API or per-session revocation is added. No backend, database, API contract,
tenant-isolation, audit or authorization policy changes are included. System
Expert's shared-foundation pin remains unchanged pending a separately accepted
adoption. Actions remain main-push/manual only.

## Validation and limits

Local verification on the candidate includes frontend build, type checking,
linting, generated-token consistency, 26 component tests, 28 browser lifecycle and
workflow tests, and 43/43 D02 governance checks. The browser suite covers direct
routes, reload, invalid credentials, denied authority, malformed/expired sessions,
tenant/actor mismatch, pending verification cancellation, expiry with an open
drawer, sign-out before revocation completion, 401/403/503, offline/resume, and
uncertain onboarding recovery without duplicate commit. Existing simulator,
search, report and Access workflow tests are retained.

`apps/customer-web/e2e/midnight.spec.ts` checks actual rendered entry, Users,
collapsed keyboard tooltips, status filtering, mobile navigation and create-user
portals with axe and screenshots. The local visual review also covered all eight
destinations, role review, 390px mobile, short desktop and a 720px reflow viewport
equivalent to the CSS layout width at 200% zoom on a 1440px display. It is not a
native-browser zoom or screen-reader certification. All 18 inspected states had
no page overflow, page errors or axe WCAG A/AA violations. Tables retain an
intentional horizontal scrolling region on narrow screens.

Browser fixtures are synthetic `example.test` records and simulated API responses;
they do not prove real server authorization. The local runtime has Node 24.19.0
rather than the pinned 24.21.0; repository pins are unchanged. The .NET identity
test attempt could not complete because the local package cache lacks pinned
Dapper/Npgsql assemblies. Docker is unavailable in this execution environment.
These are pending hosted checks, not passing evidence.

Before acceptance, manually run the existing **Impact-aware continuous
verification** (impacted mode) and **Local Docker UAT acceptance** workflows on
the candidate branch. The latter exercises real owner authorization, tenant
isolation, revocation, persistence and browser administration in isolated Docker
containers. Its browser assertions now include public-shell isolation and reload.
No workflow triggers are changed to obtain candidate runs.

Reproduce frontend checks with the pinned toolchain: `pnpm install --frozen-lockfile`,
`pnpm --filter @monergy/ui-foundation build`, `pnpm --filter @monergy/customer-web build`,
`pnpm --filter @monergy/customer-web test`,
`pnpm --filter @monergy/customer-web --filter @monergy/ui-foundation lint`, and
`pnpm exec playwright test`. Complete Docker UAT on the candidate before requesting
acceptance; retain failures and evidence with the candidate PR.
