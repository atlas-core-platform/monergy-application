CREATE TABLE audit.inbox (
    source_event_id text PRIMARY KEY,
    received_at timestamptz NOT NULL
);

GRANT SELECT, INSERT ON audit.inbox TO monergy_audit_runtime;
