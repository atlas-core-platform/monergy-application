CREATE TABLE am.groups (
    group_id text PRIMARY KEY,
    code text NOT NULL UNIQUE CHECK (code ~ '^[a-z][a-z0-9_-]{0,79}$'),
    label text NOT NULL CHECK (length(label) BETWEEN 1 AND 120)
);
CREATE TABLE am.group_members (
    group_id text NOT NULL REFERENCES am.groups ON DELETE CASCADE,
    actor_id text NOT NULL REFERENCES am.access_subjects ON DELETE CASCADE,
    PRIMARY KEY (group_id, actor_id)
);
CREATE INDEX group_members_actor ON am.group_members (actor_id);

CREATE TABLE am.import_batches (
    import_id text PRIMARY KEY,
    content_hash text NOT NULL CHECK (content_hash ~ '^[a-f0-9]{64}$'),
    status text NOT NULL CHECK (status IN ('PendingIdentity','Failed','Activated','Cancelled')),
    row_count integer NOT NULL CHECK (row_count BETWEEN 1 AND 500),
    created_by text NOT NULL,
    activated_policy_version bigint,
    error_code text,
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((status = 'Activated') = (activated_policy_version IS NOT NULL))
);
-- Pending access intents, never queried by the authorization evaluator. Resolved
-- role/group IDs are immutable snapshots, revalidated before atomic activation.
CREATE TABLE am.import_rows (
    import_id text NOT NULL REFERENCES am.import_batches,
    row_number integer NOT NULL CHECK (row_number BETWEEN 1 AND 500),
    normalized_email text NOT NULL CHECK (normalized_email = lower(normalized_email)),
    business_role_id text,
    group_ids text[] NOT NULL,
    identity_key text NOT NULL UNIQUE,
    actor_id text,
    PRIMARY KEY (import_id, row_number),
    UNIQUE (import_id, normalized_email),
    UNIQUE (import_id, actor_id)
);
CREATE TABLE am.import_email_reservations (
    normalized_email text PRIMARY KEY,
    import_id text NOT NULL REFERENCES am.import_batches
);
CREATE INDEX import_email_reservations_batch ON am.import_email_reservations (import_id);
ALTER TABLE am.tenant_identity ALTER COLUMN schema_version SET DEFAULT 2;
UPDATE am.tenant_identity SET schema_version=2 WHERE singleton;
