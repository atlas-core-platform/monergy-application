-- Platform-only owner schema. No synthetic tenant, Default Access Pack or initial reviewer grant.
CREATE SCHEMA IF NOT EXISTS am_platform;
CREATE TABLE am_platform.policy (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    schema_version integer NOT NULL CHECK (schema_version = 1),
    revision bigint NOT NULL CHECK (revision >= 0)
);
INSERT INTO am_platform.policy (singleton, schema_version, revision) VALUES (true, 1, 0);

CREATE TABLE am_platform.principal_references (
    principal_id varchar(128) PRIMARY KEY,
    realm text NOT NULL CHECK (realm = 'MONERGY_PLATFORM'),
    credential_version bigint NOT NULL CHECK (credential_version > 0),
    verified_at timestamptz NOT NULL
);
CREATE TABLE am_platform.reviewer_grants (
    entitlement_id uuid PRIMARY KEY,
    principal_id varchar(128) NOT NULL UNIQUE REFERENCES am_platform.principal_references(principal_id),
    capability text NOT NULL CHECK (capability = 'platform.assistant.feedback.review'),
    granted boolean NOT NULL,
    revision bigint NOT NULL CHECK (revision > 0),
    changed_at timestamptz NOT NULL
);
CREATE TABLE am_platform.command_results (
    operation_id uuid PRIMARY KEY,
    request_hash char(64) NOT NULL,
    result_json jsonb NOT NULL,
    recorded_at timestamptz NOT NULL
);
CREATE TABLE am_platform.audit_outbox (
    event_id uuid PRIMARY KEY,
    operation_id uuid NOT NULL,
    payload_json jsonb NOT NULL,
    payload_hash char(64) NOT NULL,
    occurred_at timestamptz NOT NULL
);
CREATE INDEX audit_outbox_operation ON am_platform.audit_outbox(operation_id);
CREATE TABLE am_platform.audit_delivery (
    event_id uuid PRIMARY KEY REFERENCES am_platform.audit_outbox(event_id),
    state text NOT NULL CHECK (state IN ('Pending', 'Delivering', 'Retry', 'Accepted', 'Paused')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 8),
    next_attempt_at timestamptz NOT NULL,
    lease_until timestamptz,
    lease_id uuid,
    receipt_id varchar(128),
    recorded_at timestamptz,
    CHECK ((state = 'Accepted') = (receipt_id IS NOT NULL AND recorded_at IS NOT NULL))
);
CREATE INDEX audit_delivery_pending ON am_platform.audit_delivery(state, next_attempt_at);

-- Runtime grants are deliberately applied by the LOCAL owner bootstrap to its separate runtime role.
-- No role, credential, entitlement or tenant policy is created by this migration.
