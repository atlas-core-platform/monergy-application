CREATE TABLE am.customer_relationships (
    relationship_id text PRIMARY KEY,
    actor_id text NOT NULL REFERENCES am.access_subjects(actor_id),
    customer_id text NOT NULL CHECK (length(customer_id) BETWEEN 1 AND 128),
    assigned_by text NOT NULL,
    assigned_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(actor_id,customer_id)
);
ALTER TABLE am.tenant_identity ALTER COLUMN schema_version SET DEFAULT 5;
UPDATE am.tenant_identity SET schema_version=5 WHERE singleton;
