CREATE SCHEMA consent;
CREATE TABLE consent.tenant_identity (
    singleton boolean PRIMARY KEY CHECK(singleton), tenant_id text NOT NULL,
    schema_version integer NOT NULL CHECK(schema_version=1)
);
CREATE TABLE consent.state (singleton boolean PRIMARY KEY CHECK(singleton), version bigint NOT NULL CHECK(version>0));
INSERT INTO consent.state VALUES(true,1);
CREATE TABLE consent.grants (
    grant_id text PRIMARY KEY,
    customer_id text NOT NULL,
    actor_id text NOT NULL,
    purpose text NOT NULL CHECK(purpose='customer-advice'),
    capability_ids text[] NOT NULL CHECK(cardinality(capability_ids) BETWEEN 1 AND 32),
    owner_actor_id text NOT NULL,
    owner_version bigint NOT NULL CHECK(owner_version>0),
    granted_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    CHECK(expires_at>granted_at)
);
CREATE UNIQUE INDEX one_current_consent ON consent.grants(customer_id,actor_id,purpose) WHERE revoked_at IS NULL;
CREATE TABLE consent.receipts (
    request_id text PRIMARY KEY,
    content_hash text NOT NULL,
    receipt jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE consent.audit (
    evidence_id text PRIMARY KEY,
    actor_id text NOT NULL,
    customer_id text NOT NULL,
    grant_id text NOT NULL,
    operation text NOT NULL CHECK(operation IN ('Granted','Revoked')),
    version bigint NOT NULL UNIQUE,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
-- Durable owner evidence for later governed delivery. This candidate does not
-- claim an external Audit consumer or delivery acknowledgement for this stream.
CREATE TABLE consent.outbox (
    event_id text PRIMARY KEY REFERENCES consent.audit(evidence_id),
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
