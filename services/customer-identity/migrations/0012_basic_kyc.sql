-- Append-only manual KYC review workflow. No government/provider verification assertion.
CREATE TABLE customer_identity.kyc_events (
 operation_id uuid PRIMARY KEY,
 customer_id text NOT NULL REFERENCES customer_identity.customers(customer_id),
 version bigint NOT NULL CHECK(version>0 AND version<9007199254740991),
 input jsonb NOT NULL,
 payload jsonb NOT NULL,
 UNIQUE(customer_id,version)
);
