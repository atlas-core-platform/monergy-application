-- AR-001 / AM-05. One Customer & Identity database per tenant; no AM tables.
CREATE SCHEMA customer_identity AUTHORIZATION CURRENT_USER;
REVOKE ALL ON SCHEMA customer_identity FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
CREATE TABLE customer_identity.tenant_identity (
    singleton boolean PRIMARY KEY CHECK (singleton),
    tenant_id text NOT NULL UNIQUE,
    schema_version integer NOT NULL CHECK (schema_version=1)
);
CREATE TABLE customer_identity.trusted_sessions (
    session_hash text PRIMARY KEY CHECK (length(session_hash)=64),
    actor_id text NOT NULL,
    subject_version bigint NOT NULL CHECK (subject_version>0),
    established_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at timestamptz NOT NULL DEFAULT (clock_timestamp()+interval '30 minutes'),
    revoked boolean NOT NULL DEFAULT false,
    CHECK (expires_at>established_at)
);
CREATE INDEX trusted_sessions_actor ON customer_identity.trusted_sessions(actor_id,subject_version);
CREATE TABLE customer_identity.access_subject_versions (
    actor_id text PRIMARY KEY,
    version bigint NOT NULL CHECK (version>0)
);
CREATE TABLE customer_identity.access_policy_version (
    singleton boolean PRIMARY KEY CHECK (singleton),
    version bigint NOT NULL CHECK (version>=0)
);
INSERT INTO customer_identity.access_policy_version VALUES (true,0);
CREATE TABLE customer_identity.access_event_inbox (
    destination text NOT NULL CHECK (destination IN ('authorization','sessions')),
    event_id uuid NOT NULL,
    event_hash text NOT NULL CHECK (length(event_hash)=64),
    receipt jsonb NOT NULL,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY(destination,event_id)
);
CREATE FUNCTION customer_identity.prevent_session_resurrection() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF OLD.revoked AND NOT NEW.revoked THEN
        RAISE EXCEPTION 'Session revocation is irreversible' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trusted_session_revocation_monotonic BEFORE UPDATE OF revoked
ON customer_identity.trusted_sessions FOR EACH ROW EXECUTE FUNCTION customer_identity.prevent_session_resurrection();
