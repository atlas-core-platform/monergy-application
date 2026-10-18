# Consent Service

The Development LOCAL/CI customer-relationship profile owns customer-advice sharing records. Current C&I session/ownership and independent AM access remain mandatory.

T1-46 adds owner-bound GET `/local/v1/consents/customers/{customerId}/sharing-options` and POST `.../sharing`. The browser supplies exact recipient email, selected supported capabilities, expiry and revision/request identity. C&I resolves the recipient using the dedicated owner credential; no public directory, new identity, role or assignment is created. Existing GrantAsync owns optimistic concurrency, replay, immutable owner/version binding and atomic grant/revision/receipt/audit/outbox writes. Existing list and revoke remain available.

The outbox is durable owner evidence; external Audit delivery acknowledgement is not asserted. T1-47 adds exact-grant sharing options and POST replacement for changing scope/expiry or renewing an expired unrevoked grant. Recipient authority stays server-derived and fixed. The existing state lock serializes an atomic old revocation/new grant, two audit/outbox records and a composite replay receipt, advancing revision by two. No schema/runtime grant changes. See [T1-47 behavior and verification](../../build/governance/t1-47-sharing-lifecycle.md).

Provider connection/reconnect, provider consent, recipient workspaces and production policy/acceptance remain open. See [T1-46 scope and native evidence](../../build/governance/t1-46-customer-sharing.md).
