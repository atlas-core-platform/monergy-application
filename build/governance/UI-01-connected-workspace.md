# UI-01 — Connected Monergy workspace

Status: implemented candidate for LOCAL / CI verification, authorized by the platform owner on 2026-10-07. UI-01 adds an experience layer to AR-001 and AM-06. Historical D01–D16 acceptance, SG-01 READY, SG-02 CONDITIONALLY_READY and SG-03/04 BLOCKED are unchanged. This is not Production deployment or Production identity-provider integration.

## One workspace standard

The application owns the shared React/Ant Design foundation under `shared/platform/frontend-ui`. Access Management administration lives at `/access` inside this application; the service remains the policy authority, without an independent competing frontend. System Expert adopts the same foundation and visual language but stays an independently runnable knowledge application, outside the transaction path.

| Element              | Standard                                                                                                                    |
| -------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| Navigation           | Navy workspace sidebar, active location, local context, collapsible desktop navigation and mobile drawer                    |
| Surfaces             | Light canvas, restrained borders, blue actions, readable cards and contextual details                                       |
| Typography and color | Existing semantic tokens and Ant Design theme; workspace CSS defines shared shell geometry                                  |
| Interaction          | Ctrl/Command K page switcher, Escape to close, visible keyboard focus, focus return, contextual drawers                     |
| Motion               | Short entrance/hover/drawer transitions; OS reduced-motion preference disables nonessential movement                        |
| Data                 | Real server results, explicit loading/empty/error states, visible tenant, no invented activity or health statistics         |
| Forms                | Review before access mutation; service authorization and expected policy revision on every write                            |
| Import               | Drag-and-drop plus keyboard file chooser and paste; whole-file validation, review, stable operation ID, retry/status/cancel |
| Responsive behavior  | Navigation and panels adapt at narrow widths; data tables scroll within their container                                     |

System Expert vendors a reviewed, exact snapshot of the foundation with source commit and SHA-256 manifest. Its verification rejects changed vendored bytes or diverging bundled/static assets. This avoids runtime or deployment coupling between repositories. Future foundation changes must update the pin, manifest and both explorer variants together. A private package release can replace vendoring once a governed package registry exists.

## Routes and retained functionality

- `/`: connected workspace overview.
- `/access`: people, roles, permissions, organizational groups, resource grants and import history.
- `/vs02`, `/search`, `/reports`: retained reference experiences, now framed by the workspace shell.
- `/foundation`: retained D02 toolchain evidence and keyboard-operable dialog.
- System Expert: `http://127.0.0.1:4310/`, with a return link to the application preview on port 4173.

Navigation does not transfer sessions between origins. System Expert cannot administer access. Its accepted knowledge snapshot is distinct from the current implementation repositories.

## Local operation

Use the repository-pinned Node and pnpm toolchain. From the application root:

```sh
pnpm install --frozen-lockfile
pnpm --filter @monergy/customer-web build
pnpm --filter @monergy/customer-web preview --host 127.0.0.1 --port 4173
```

Open `http://127.0.0.1:4173/`. For editing, use `pnpm --filter @monergy/customer-web dev --host 127.0.0.1 --port 4173`.

The access screens require the existing owner composition and provisioned tenant databases: Access Management at `127.0.0.1:5088`, Customer & Identity at `127.0.0.1:5101`, with the AM-06 provisioning credential and owner routes configured. Configure the reference administrator key on the server, then enter it in the local connection form. No demo login, baked-in key or fixture membership is created by the frontend. Without the services, the connection fails visibly and safely. The owner integration workflow remains the executable, disposable PostgreSQL composition and verification reference; do not run its reset scripts against retained data.

Vite proxies only `/access-api/v1` and `/identity-api/local/v1/tenant-sessions` to fixed loopback owners, alongside existing reference routes. Internal workload provisioning/validation routes are not forwarded by these UI prefixes. Bind the UI and owner services to loopback. This developer preview is not a Production gateway.

## Access and recovery boundaries

Credentials are cleared after use. The resulting session stays in component memory, never local/session storage or a URL. Reloading the page requires reconnection. C&I authenticates, AM verifies current TenantAdmin authority, and all mutations recheck the current server policy revision. Sign-out durably revokes the exact tenant-scoped session, is idempotent and does not require AM to be available.

A TenantAdmin has no implied business permissions. A person has at most one business role. Groups organize people without granting access. Role changes remove individual resource grants according to the existing service contract. Stale edits remain visible after a conflict but cannot overwrite a newer policy without refresh/review.

CSV imports have the exact `email,role_code,group_codes` header, up to 500 rows and 240 KB. A blank role grants no business access; group codes are pipe-separated. The browser asks the service to validate the entire file. Initial processing uses the staging receipt's revision. Explicit retry rechecks current policy; interrupted staging keeps the same ID, and status lookup can recover the receipt. Only a confirmed missing/rejected stage unlocks replacement content. The C&I owner may retain already-created identities after cancellation; this is not activated AM access. Completed batches are never silently reprocessed by a restored staging receipt.

## Verification boundary

Component tests exercise administrator rejection, clearing secrets, stale-edit retention, session expiry, whole-file rejection, staged revision binding and mixed-revision pagination. Browser tests retain the four historical reference flows and add keyboard navigation/focus, reduced motion, mobile overflow, CSV drag-and-drop and sign-out. UI tests use clearly synthetic API fixtures; actual PostgreSQL/HTTP owner tests separately verify the server contract and tenant-isolated durable revocation.

Automated accessibility scans and desktop/mobile visual review supplement keyboard tests. They are not a claim of universal WCAG certification. Existing bundle measurement records all generated chunks; the entry chunk alone is not the total initial network payload. Shared vendor chunk size remains visible in build output.
