# Guided existing-customer setup (T1-54)

The LOCAL product workspace can require an existing owned customer to verify the
registered email, save/review the profile and explicitly accept the current
published account agreements before opening financial features.

Add `-RequireCustomerSetup` to the named profile preparation/start invocation of
`build/uat/Invoke-MonergyUat.ps1`, together with `-EnableOnboarding` and
`-EnableEmailAccess`. Supply the existing email-delivery configuration and
`-AccountAgreementFile <absolute-json-path>` with approved tenant-specific legal
content as described in Customer-Account-Agreements.md. Use the same switches on
subsequent preparation/start calls. Missing publication keeps setup incomplete.
No legal text, verification or agreement acceptance is fabricated.

The switch defaults off. When on, a customer landing on a financial URL sees
setup first. Profile saving and agreement receipt/readback refresh current
readiness. Email verification uses sign-out then the registered-email code flow.
Sharing permissions, session management and sign-out remain accessible. The
server denies financial product/raw-contract proxy requests until ready; hiding
the UI is not the enforcement mechanism. Replacing an agreement publication
requires fresh acceptance of that revision.

Readiness is derived from existing owner records, not a new activation state.
This controls the LOCAL product ingress, not every private service port. It adds
no roles, customer ownership, schema or automatic migration. It does not deliver
public customer registration, customer creation, KYC or a complete activation
policy. Default-off mode preserves normal existing customer access.

Native verification uses explicit dependency doubles and loopback HTTP. Actual
SMTP delivery, physical PostgreSQL and full PowerShell/Compose/browser UAT must
be proven separately; none is claimed by this receipt.
