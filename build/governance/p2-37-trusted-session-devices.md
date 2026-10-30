# P2-37 — trusted session device visibility

## Business journey

An authenticated Customer opens **Settings → Devices & sessions**, reviews the Customer's successful session history, identifies newly issued sessions by a bounded browser-reported device class and browser family, and ends another active session through the existing governed revocation flow.

Customer & Identity remains the owner of session issuance, history and revocation. The current tenant, actor and session are resolved from the authenticated session; the browser cannot post an actor or tenant to the self-session endpoints.

## Evidence boundary

Only these controlled values are retained with a trusted session:

- device class: `Desktop`, `Tablet`, `Mobile` or `Unknown`;
- client family: `Chrome`, `Edge`, `Firefox`, `Safari`, `Other` or `Unknown`.

The browser reduces its local user-agent string to those values before the request. Customer & Identity normalizes the two headers again before persistence. Raw user agents, IP addresses, location, arbitrary device names and device fingerprints are not retained. Existing sessions display **Device details were not recorded**.

The evidence is descriptive and never grants authority. Session validation continues to require the live tenant-bound session, current membership and exact subject version. Revocation remains actor-bound, idempotent and non-enumerating across tenants.

## Persistence and privilege

Migration `0015_trusted_session_client_evidence.sql` adds constrained, defaulted client-evidence columns to `customer_identity.trusted_sessions`. Runtime roles receive insert privilege for those columns but no update, delete or truncate privilege. Readiness fails closed when the required insert-only privilege contract is absent.

## Validation

- Customer & Identity, UAT Gateway and affected persistence-test Release builds.
- focused session, registration, email-access and persona-credential backend tests;
- focused Devices & sessions and authentication-entry React tests;
- Customer Web TypeScript, affected ESLint and production build/lazy-isolation checks;
- physical PostgreSQL regression for persistence and immutable runtime evidence.

Physical PostgreSQL, installed-browser and retained LOCAL UAT remain acceptance gates. Production identity and MFA remain separate from this bounded journey.
