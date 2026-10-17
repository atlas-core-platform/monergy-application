# Reporting Service

The Reporting Service owns report identity, generation state, export metadata, and associations to authoritative sources. Evidence, Financial Profile, Financial Rules, and Audit retain their respective authority.

## Governed execution profiles

The historical MWP-03-D08 reference adapter remains available only at its recorded `SIMULATOR` evidence level in `LOCAL` and `CI_EPHEMERAL`. It produces the bounded deterministic reference summary used by D08 tests and must not be presented as a customer's live financial report.

T1-10 adds a separate LOCAL/UAT product adapter. When `Monergy:ProductReporting:Enabled=true`, Reporting reads the server-authorized customer's current Financial Profile through CID-030 and CID-033 and includes only currently supported `INCOME`, `EXPENSE`, `LOAN`, `ASSET`, `INVESTMENT`, `INSURANCE`, and `TAX` source facts. Loan source figures are not verified outstanding balances; Asset source figures are not certified valuations or net worth. Investment source figures are not live holdings, NAV, current market prices, or performance. It does not aggregate periods, run Financial Rules, create a Financial Health score, invoke AI, or claim a formal report pack. Insurance figures are not verified policy benefits, claims, premiums or cash values. Tax source figures are not assessed tax owed, accepted filings, refunds or tax advice. The JSON export is the exact governed content of that generated basic summary.

The T1-10 product adapter depends on the tenant/customer authorization boundary and is not Production acceptance. Report storage under the reference-adapter UAT profile remains volatile.

The D11 persisted-reporting profile remains a distinct engineering profile with physical report persistence and service-contract source reading; its evidence and claims are unchanged.
