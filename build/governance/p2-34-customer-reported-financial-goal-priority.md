# P2-34 — Customer-reported financial-goal priority

## Ownership and authority

Financial Profile owns the goal record and its immutable revision history. The Customer may report priority only through the current authenticated Self context with both `financial-profile.profile.read` and `financial-profile.profile.update`. Tenant, customer and actor are resolved from the current session; the request cannot supply them.

## Governed meaning

The bounded values are `Not set`, `Low`, `Medium` and `High`. Every value has the `CUSTOMER_REPORTED` basis and, once explicitly changed, an effective date. Priority is an organizing label reported by the Customer. It is not financial advice, a suitability or risk score, a savings allocation, a required-contribution calculation, an eligibility decision, or proof that a goal was achieved.

## Integrity

- A priority change appends a new goal revision; earlier priority values remain available in goal history.
- Exact operation retries return the original saved revision. A changed payload using the same operation fails closed.
- The request must name the expected current revision. Concurrent or stale changes fail with a revision conflict.
- An effective date cannot precede the previously recorded priority date.
- Priority can change only while the goal is `IN_PROGRESS` or `PAUSED`. A closed or customer-reported achieved goal retains its last priority until it is reopened through the separate lifecycle journey.
- Goal corrections and lifecycle reports preserve the current priority fields. Priority reports preserve the goal terms and lifecycle fields.
- Product Gateway validates the owner receipt and an exact revision readback before confirming success, then re-evaluates the current session authority.

## Deferred

Automated prioritization, recommendations, ranking across customers, contribution planning, advisor-authored priority, notifications, verified achievement and portfolio allocation are not part of this journey.
