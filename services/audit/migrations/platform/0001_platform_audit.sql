CREATE SCHEMA IF NOT EXISTS audit_platform;
REVOKE ALL ON SCHEMA audit_platform FROM PUBLIC;
CREATE TABLE audit_platform.inbox (
 event_id uuid PRIMARY KEY, producer text NOT NULL CHECK(producer IN ('am-platform','ai-feedback')),
 payload_hash char(64) NOT NULL, received_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE audit_platform.evidence (
 event_id uuid PRIMARY KEY REFERENCES audit_platform.inbox(event_id),
 producer text NOT NULL CHECK(producer IN ('am-platform','ai-feedback')),
 realm text NOT NULL CHECK(realm='MONERGY_PLATFORM'),
 envelope jsonb NOT NULL, recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE audit_platform.receipts (
 event_id uuid PRIMARY KEY REFERENCES audit_platform.evidence(event_id), receipt_id uuid NOT NULL UNIQUE,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
REVOKE ALL ON ALL TABLES IN SCHEMA audit_platform FROM PUBLIC;
