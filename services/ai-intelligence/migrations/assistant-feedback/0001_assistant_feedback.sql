CREATE SCHEMA IF NOT EXISTS ai_feedback;
REVOKE ALL ON SCHEMA ai_feedback FROM PUBLIC;
CREATE TABLE ai_feedback.topics (
 gap_id uuid PRIMARY KEY, source_audience text NOT NULL CHECK(source_audience IN ('product','platform')),
 status text NOT NULL, revision bigint NOT NULL CHECK(revision>0), created_at timestamptz NOT NULL,
 expires_at timestamptz NOT NULL, payload jsonb NOT NULL, knowledge_revision char(64) NOT NULL,
 retrieval_revision char(64) NOT NULL, deleted_at timestamptz
);
CREATE INDEX topics_page ON ai_feedback.topics(created_at,gap_id) WHERE deleted_at IS NULL;
CREATE TABLE ai_feedback.operations (
 operation_key text PRIMARY KEY, payload_hash char(64) NOT NULL, gap_id uuid NOT NULL,
 result jsonb NOT NULL, audit_event_id uuid, created_at timestamptz NOT NULL DEFAULT clock_timestamp(), expires_at timestamptz NOT NULL
);
CREATE TABLE ai_feedback.counters (
 day date NOT NULL, source_audience text NOT NULL CHECK(source_audience IN ('product','platform')),
 category text NOT NULL, intent text NOT NULL, knowledge_revision char(64) NOT NULL, retrieval_revision char(64) NOT NULL,
 count bigint NOT NULL CHECK(count>0), PRIMARY KEY(day,source_audience,category,intent,knowledge_revision,retrieval_revision)
);
CREATE TABLE ai_feedback.audit_outbox (
 event_id uuid PRIMARY KEY, payload jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 attempt_count integer NOT NULL DEFAULT 0, recovery_count integer NOT NULL DEFAULT 0,
 next_attempt_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 receipt jsonb, delivered_at timestamptz
);
CREATE INDEX pending_audit ON ai_feedback.audit_outbox(next_attempt_at) WHERE delivered_at IS NULL;
REVOKE ALL ON ALL TABLES IN SCHEMA ai_feedback FROM PUBLIC;
CREATE FUNCTION ai_feedback.purge_delivered_audit() RETURNS integer LANGUAGE plpgsql SECURITY DEFINER
SET search_path=pg_catalog,ai_feedback AS $$
DECLARE removed integer;
BEGIN
 DELETE FROM ai_feedback.audit_outbox WHERE delivered_at IS NOT NULL AND delivered_at<clock_timestamp()-interval '90 days';
 GET DIAGNOSTICS removed=ROW_COUNT;
 RETURN removed;
END;
$$;
REVOKE ALL ON FUNCTION ai_feedback.purge_delivered_audit() FROM PUBLIC;
