# P2-35 — Customer-defined financial-goal Search

## Bounded journey

M2-WS04-E02-F03, M2-WS07-E01-F02 and MON-FIN-010 retain Customer-defined financial goals and governed financial Search. P2-35 adds each current goal to the authorized lexical Financial information projection. Financial Profile remains the authoritative owner; Search Retrieval stores no goal and rebuilds its derived projection inside the current authorized request.

Each result retains the canonical goal identifier and the immutable operation receipt for the exact current goal revision. Customer Web routes the result to the governed financial-goal surface and opens only the matching current goal.

## Safety and truth boundaries

- Search and financial-information authority are evaluated through the existing Product Search boundary. Financial Profile independently re-evaluates current Customer Self financial-read authority for both the bounded list and every exact revision read.
- Tenant, Customer and actor are resolved from the authenticated session. The browser cannot nominate another boundary, goal, revision or source receipt.
- Search confirms every listed goal against its exact current owner revision. A foreign Customer, changed revision, malformed owner record, duplicate identifier or stale list/read mismatch fails closed.
- More than 200 goals or an owner continuation page fails closed. Search never presents an incomplete goal corpus as complete.
- A Customer with goals but no source-fact Financial Profile can still search those goals; tax and document-derived facts are not invented.
- Results expose only the Customer-defined target, target date, reported lifecycle state and Customer-reported priority. They explicitly do not provide advice, a required contribution, savings allocation, eligibility or independently verified achievement.
- Search remains lexical, derived and rebuildable. No semantic/vector retrieval, persistence or migration is introduced.

## Customer experience

Customer Search includes goal names, target amount/currency/date, reported lifecycle state and Customer priority. Selecting a result opens the exact current goal details in Financial information. A removed or unavailable goal never substitutes another record and produces a safe unavailable message.

## Deferred scope

OD-08/C-10 required-contribution methodology, recommendations, automated prioritization, verified achievement, provider-confirmed contributions, FX, goal Reporting, semantic/vector Search, central Audit and physical/live acceptance remain open.

## Source validation

- 62/62 Search Retrieval checks, 50/50 focused Search/goal Customer Web checks and the 27/27 parent Financial information checks passed.
- Search Retrieval and UAT Gateway Release builds passed with zero warnings and zero errors.
- Customer Web TypeScript, affected ESLint, production build, bundle budget and lazy isolation passed.
- Physical PostgreSQL, installed-browser and retained LOCAL/live acceptance were not run.
