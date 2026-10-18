-- T1-56. C&I-only encrypted self-declared PAN; no KYC/ownership assertion.
CREATE TABLE customer_identity.customer_pan (
    customer_id text PRIMARY KEY REFERENCES customer_identity.customers(customer_id),
    actor_id text NOT NULL REFERENCES customer_identity.tenant_principals(actor_id),
    key_id text,
    protected_pan bytea,
    version bigint NOT NULL CHECK (version>0),
    verification_reference text NOT NULL CHECK (verification_reference ~ '^[0-9a-f]{64}$'),
    updated_at timestamptz NOT NULL,
    CHECK ((key_id IS NULL AND protected_pan IS NULL) OR
        (key_id IS NOT NULL AND key_id ~ '^[A-Za-z0-9_-]{1,32}$' AND protected_pan IS NOT NULL AND octet_length(protected_pan)=38))
);
CREATE TABLE customer_identity.customer_pan_receipts (
    operation_id uuid PRIMARY KEY,
    customer_id text NOT NULL REFERENCES customer_identity.customers(customer_id),
    actor_id text NOT NULL REFERENCES customer_identity.tenant_principals(actor_id),
    fingerprint_key_id text NOT NULL CHECK (fingerprint_key_id ~ '^[A-Za-z0-9_-]{1,32}$'),
    fingerprint text NOT NULL CHECK (fingerprint ~ '^[0-9a-f]{64}$'),
    pan_version bigint NOT NULL CHECK (pan_version>0),
    verification_reference text NOT NULL CHECK (verification_reference ~ '^[0-9a-f]{64}$'),
    recorded_at timestamptz NOT NULL
);
CREATE TABLE customer_identity.customer_pan_audit (
    event_id uuid PRIMARY KEY,
    customer_id text NOT NULL REFERENCES customer_identity.customers(customer_id),
    actor_id text NOT NULL REFERENCES customer_identity.tenant_principals(actor_id),
    pan_version bigint NOT NULL CHECK (pan_version>=0),
    outcome text NOT NULL CHECK (outcome IN ('READ','RECORDED','REMOVED','REPLAY')),
    recorded_at timestamptz NOT NULL
);
