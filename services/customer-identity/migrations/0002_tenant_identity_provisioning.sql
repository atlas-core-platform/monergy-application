-- AR-001 / AM-06: C&I-owned identity records and immutable provisioning receipts.
-- No password, invitation, authentication token, AM role or access membership.
CREATE TABLE customer_identity.provisioning_schema (
    singleton boolean PRIMARY KEY CHECK (singleton),
    version integer NOT NULL CHECK (version=1)
);
INSERT INTO customer_identity.provisioning_schema VALUES (true,1);
CREATE TABLE customer_identity.tenant_principals (
    actor_id text PRIMARY KEY CHECK (actor_id ~ '^ci-[0-9a-f]{32}$'),
    normalized_email text COLLATE "C" NOT NULL UNIQUE
        CHECK (length(normalized_email) BETWEEN 3 AND 320 AND normalized_email=lower(btrim(normalized_email))),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE customer_identity.provisioning_receipts (
    idempotency_key text PRIMARY KEY CHECK (idempotency_key ~ '^[0-9a-f]{64}$'),
    import_id text NOT NULL CHECK (length(import_id) BETWEEN 1 AND 80),
    row_number integer NOT NULL CHECK (row_number BETWEEN 1 AND 500),
    normalized_email text COLLATE "C" NOT NULL,
    actor_id text NOT NULL REFERENCES customer_identity.tenant_principals(actor_id),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(import_id,row_number)
);
