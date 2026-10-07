-- AR-001 / AM-05. Tenant bindings are installed only by owner provisioning.
-- Existing D09/D10 Audit composition remains unchanged without a binding.
CREATE TABLE audit.access_tenant_identity (
    singleton boolean PRIMARY KEY CHECK (singleton),
    tenant_id text NOT NULL UNIQUE,
    schema_version integer NOT NULL CHECK (schema_version=1)
);
REVOKE ALL ON audit.access_tenant_identity FROM monergy_audit_runtime;
CREATE TABLE audit.access_event_inbox (
    event_id uuid PRIMARY KEY,
    event_hash text NOT NULL CHECK (length(event_hash)=64),
    envelope jsonb NOT NULL,
    receipt jsonb NOT NULL,
    audit_evidence_id text NOT NULL UNIQUE REFERENCES audit.evidence,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
REVOKE UPDATE,DELETE,TRUNCATE ON audit.access_event_inbox FROM monergy_audit_runtime;
