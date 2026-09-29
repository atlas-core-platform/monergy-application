CREATE SCHEMA IF NOT EXISTS audit AUTHORIZATION CURRENT_USER;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA audit FROM PUBLIC;
GRANT USAGE ON SCHEMA audit TO monergy_audit_runtime;

CREATE TABLE audit.evidence (
    audit_evidence_id text PRIMARY KEY,
    source_contract_id text NOT NULL,
    source_event_id text NOT NULL UNIQUE,
    event_name text NOT NULL,
    producer text NOT NULL,
    subject_type text NOT NULL,
    subject_id text NOT NULL,
    occurred_at timestamptz NOT NULL,
    correlation_id text NOT NULL,
    causation_id text,
    recorded_at timestamptz NOT NULL
);

CREATE INDEX ix_audit_evidence_recorded ON audit.evidence (recorded_at, audit_evidence_id);
GRANT SELECT, INSERT ON audit.evidence TO monergy_audit_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON audit.evidence FROM monergy_audit_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT, INSERT ON TABLES TO monergy_audit_runtime;
