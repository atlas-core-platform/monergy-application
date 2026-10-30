# P2-36 — Customer-defined financial-goal Reporting

## Bounded journey

M2-WS04-E02-F03, M2-WS07-E01-F03 and MON-FIN-010 retain Customer-defined financial goals and governed basic Reporting. P2-36 adds each current goal to the authorized basic financial summary. Financial Profile remains the authoritative owner; Reporting stores the exact source snapshot in the generated report and never becomes the goal system of record.

Each report item retains the canonical goal identifier, exact current owner revision and immutable operation receipt. Customer Web links the item back to the governed financial-goal history surface and exports only the Reporting-owned payload.

## Safety and truth boundaries

- Reporting and financial-information authority are evaluated through the existing Product Reporting boundary. Financial Profile independently re-evaluates current Customer Self financial-read authority for both the bounded list and every exact revision read.
- Tenant, Customer and actor are resolved from the authenticated session. The browser cannot nominate another boundary, goal, revision or source receipt.
- Reporting confirms every listed goal against its exact current owner revision. A foreign Customer, changed revision, malformed owner record, duplicate identifier or stale list/read mismatch fails closed.
- More than 200 goals, an owner continuation page or more than 100 combined report items fails closed. Reporting never represents an incomplete corpus as complete.
- A Customer with goals but no document-derived Financial Fact profile can still generate a goal-only basic summary. Financial facts, evidence and calculation receipts are not invented.
- Goal items expose only the Customer-defined target amount, currency and date, reported lifecycle state and Customer-reported priority. They explicitly do not provide advice, a required contribution, savings allocation, eligibility or independently verified achievement.
- The report source type is `FinancialGoal`; Financial Fact provenance, evidence references and calculation receipts remain not applicable rather than being fabricated.

## Customer experience

The basic financial summary labels each item as a Customer-defined goal, displays its exact owner revision and operation receipt, and links directly to the goal and its history. Exported content carries the same governed fields and safety boundary as the on-screen summary.

## Deferred scope

OD-08/C-10 required-contribution methodology, recommendations, automated prioritization, verified achievement, provider-confirmed contributions, FX, semantic/vector Search, formal report packs, central Audit and physical/live acceptance remain open.

## Source validation

- Reporting owner, Gateway, Customer Web and System Expert source gates are recorded in the paired P2-36 pull requests.
- Physical PostgreSQL, installed-browser and retained LOCAL/live acceptance are not claimed by this source implementation journey.
