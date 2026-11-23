-- Event recovery does not create a topic or pretend that a resource-less release has a gap ID.
CREATE TABLE ai_feedback.audit_recovery_operations (
 operation_key char(64) PRIMARY KEY, request_hash char(64) NOT NULL, event_id uuid NOT NULL,
 recovery_event_id uuid NOT NULL, created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 expires_at timestamptz NOT NULL DEFAULT clock_timestamp()+interval '30 days'
);
REVOKE ALL ON ai_feedback.audit_recovery_operations FROM PUBLIC;
