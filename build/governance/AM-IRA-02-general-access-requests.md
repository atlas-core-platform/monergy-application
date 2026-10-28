# AM-IRA-02 application synchronization

The customer application consumes Access Management's generic request-target API. Restricted product routes remain visible but do not mount protected business components before their minimum current capability is present. A requester can submit only the canonical target in the route plus a generated operation ID; no actor, tenant, role, permission or grant payload is sent.

Customer overview lists missing applicable targets, including capabilities that are not standalone routes. The Financial Advisor workspace uses the generic API and does not call the protected customer-list endpoint until its access target is fulfilled. The Organization Administrator review surface renders any safe canonical target returned by Access Management and explains that Consent, assignments and administrator authority remain separate.

After fulfillment, a status refresh re-reads the governed workspace context before the feature is mounted. The legacy Advisor endpoint remains a server compatibility wrapper only; the browser no longer depends on it.

The LOCAL UAT dependency source under `build/uat/dependencies/source` must be byte-synchronized with the paired Access Management candidate before exact-head validation.
