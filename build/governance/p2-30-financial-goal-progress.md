# P2-30 — Customer-reported financial goal progress

## Bounded journey

M2-WS04-E02-F03 and MON-FIN-010 retain customer-managed financial goals and contribution/progress needs. P2-30 adds a read-only progress view for one current Customer goal. Financial Profile remains the owner of the saved goal and contribution reports. The browser supplies only the canonical goal identifier selected from the current authorized list.

The owner loads the current goal and the latest saved version of every contribution report for that goal. It excludes reports whose current state is `CUSTOMER_WITHDRAWN`, totals the remaining customer-reported amounts, calculates the amount remaining against the current target and returns the exact contribution identifiers, contribution revisions and goal revisions used. Progress uses two-decimal, away-from-zero arithmetic. A contribution report remains included after a same-currency goal correction because its exact historical goal version remains visible in the source list.

## Safety and truth boundaries

- Current Self read authority is required at Product Gateway ingress, at the Financial Profile owner and again before release.
- Tenant and Customer are resolved from the current authenticated session. The browser cannot post another tenant, Customer, contribution, amount, currency, percentage or achievement state.
- More than 200 contribution records fails closed instead of calculating an incomplete total.
- A current goal currency that differs from an active historical contribution currency fails closed. P2-30 performs no FX conversion.
- Unknown states, duplicate contribution identifiers, malformed owner data and inconsistent arithmetic fail closed at the owner or Product Gateway.
- The response says `CUSTOMER_REPORTED` and `NOT_DETERMINED`. It does not verify payment, bank transfer, savings allocation or achievement.
- No new persistence or migration is introduced. Progress is a current owner projection over governed goal and contribution history.

## Customer experience

Financial goals expose **View progress** beside the existing goal, contribution and history actions. The Midnight drawer shows target, reported contribution total, remaining amount, percentage, included/withdrawn counts, latest included date, current goal version and observation time. A changed currency, unavailable owner, removed authority or substituted response produces a safe unavailable state rather than partial progress.

## Deferred scope

Future required-contribution calculations, contribution schedules, allocation/reconciliation, provider-confirmed payments, FX, achievement lifecycle, planning recommendations, advice, goal prioritization, Search/Reports integration, central Audit and physical/live acceptance remain open. OD-08/C-10 methodology approval is still required before planning or recommendation behavior is introduced.

## Source validation

- 135/135 focused Financial Profile goal/contribution/progress tests passed.
- 95/95 focused Customer Web goal/progress/contribution tests passed.
- Financial Profile and UAT Gateway Release builds passed with zero warnings and zero errors.
- Customer Web TypeScript, full ESLint, production build, bundle budget and lazy isolation passed.
- Physical PostgreSQL, installed-browser and retained LOCAL/live acceptance were not run.
