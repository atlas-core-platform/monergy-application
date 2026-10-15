CREATE SCHEMA am;

CREATE TABLE am.tenant_identity (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    tenant_id text NOT NULL UNIQUE CHECK (length(tenant_id) BETWEEN 1 AND 64),
    schema_version integer NOT NULL DEFAULT 1 CHECK (schema_version > 0),
    initial_admin_actor_id text,
    initialized_at timestamptz
);
CREATE TABLE am.policy_state (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    policy_version bigint NOT NULL DEFAULT 0 CHECK (policy_version >= 0)
);
INSERT INTO am.policy_state (singleton) VALUES (true);

CREATE TABLE am.permissions (
    permission_id text PRIMARY KEY,
    code text NOT NULL UNIQUE CHECK (code ~ '^[a-z][a-z0-9_-]{0,79}$'),
    label text NOT NULL CHECK (length(btrim(label)) BETWEEN 1 AND 120)
);
CREATE TABLE am.roles (
    role_id text PRIMARY KEY,
    code text NOT NULL UNIQUE CHECK (code ~ '^[a-z][a-z0-9_-]{0,79}$'),
    label text NOT NULL CHECK (length(btrim(label)) BETWEEN 1 AND 120)
);
CREATE TABLE am.permission_capabilities (
    permission_id text NOT NULL REFERENCES am.permissions ON DELETE CASCADE,
    capability_id text NOT NULL,
    scope text NOT NULL CHECK (scope IN ('Tenant', 'Resource')),
    PRIMARY KEY (permission_id, capability_id, scope)
);
CREATE TABLE am.role_permissions (
    role_id text NOT NULL REFERENCES am.roles ON DELETE CASCADE,
    permission_id text NOT NULL REFERENCES am.permissions ON DELETE RESTRICT,
    PRIMARY KEY (role_id, permission_id)
);
CREATE INDEX ix_role_permissions_permission ON am.role_permissions (permission_id);

CREATE TABLE am.access_subjects (
    actor_id text PRIMARY KEY CHECK (length(actor_id) BETWEEN 1 AND 128),
    normalized_email text NOT NULL UNIQUE
        CHECK (normalized_email = lower(normalized_email) AND length(normalized_email) BETWEEN 3 AND 320),
    status text NOT NULL CHECK (status IN ('PendingIdentity', 'Active', 'Disabled')),
    business_role_id text REFERENCES am.roles ON DELETE RESTRICT,
    administrative_authority text CHECK (administrative_authority = 'TenantAdmin'),
    subject_version bigint NOT NULL DEFAULT 1 CHECK (subject_version > 0)
);
CREATE INDEX ix_access_subjects_role ON am.access_subjects (business_role_id);
CREATE TABLE am.resource_grants (
    grant_id text PRIMARY KEY,
    actor_id text NOT NULL REFERENCES am.access_subjects ON DELETE CASCADE,
    capability_id text NOT NULL,
    resource_type text NOT NULL,
    resource_id text NOT NULL CHECK (length(resource_id) BETWEEN 1 AND 200),
    UNIQUE (actor_id, capability_id, resource_type, resource_id)
);
CREATE TABLE am.outbox (
    event_id uuid PRIMARY KEY,
    event_type text NOT NULL,
    tenant_id text NOT NULL,
    aggregate_id text NOT NULL,
    initiator_actor_id text NOT NULL,
    correlation_id text NOT NULL,
    policy_version bigint NOT NULL,
    subject_version bigint,
    payload jsonb NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX ix_outbox_occurred ON am.outbox (occurred_at, event_id);
