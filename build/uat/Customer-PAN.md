# Customer PAN (T1-56)

My Profile → Manage PAN supports an optional self-declared PAN for an existing
owned customer. The saved record is displayed as a last-four mask. Recording or
replacement requires explicit confirmation and acceptance of the current published
account agreements. Removal remains available when a current publication is absent
or unaccepted. A saved PAN is not a verified identity or KYC result.

The feature defaults off. Add `-EnableCustomerPan` to named-profile Prepare/Start
calls with onboarding enabled. For a new profile, also pass `-EnableOnboarding`.
Supply approved legal publication through the existing `-AccountAgreementFile`
configuration and explicitly review/accept it through Account agreements. No legal
text, PAN or acceptance is seeded. Use the switch on subsequent Prepare/Start calls
to keep the feature available.

The launcher retains a separate random PAN key in the existing protected profile
and writes its base64 value under C&I `Monergy.CustomerPan.Keys.local-v1`, with
`CurrentKeyId=local-v1` and `Enabled=true`. Preserve this key with the protected
profile and database backup. Replacing/loss of the key cannot decrypt prior data.
The owner keyring accepts up to four explicitly configured key IDs and fails closed
when a referenced historical key is missing; existing receipts also need their old
key. Complete rotation/retention/KMS and production deployment are separate work.
The launcher manages its one retained LOCAL key and never prints it.

Customers can replace a record by entering the new PAN and confirming the action.
Removal is a separate explicit confirmation. The full input clears when a save
starts or the panel closes. Success follows a receipt and fresh masked/null owner
readback; any uncertainty requires Reload PAN. Removing clears the active encrypted
value; it does not promise physical erasure from MVCC/WAL/backups or account closure.

C&I migration0009 and additive verifier overlay create encrypted state, append-only
receipts and read/write/replay metadata. No plaintext PAN enters those tables or
shared directories/Search/Reports. Native tests use synthetic identifiers, real
cryptography and explicit persistence doubles. Physical PostgreSQL, bootstrap/runtime
grants, PowerShell/Compose and integrated browser UAT remain unexecuted.
