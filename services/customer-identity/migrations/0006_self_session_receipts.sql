-- T1-45. Intrinsic self-service session revocation; no bearer credential or
-- device/location inference. Session state and operation receipt commit together.
CREATE TABLE customer_identity.self_session_receipts (
    operation_id uuid PRIMARY KEY,
    actor_id text NOT NULL,
    session_reference text NOT NULL REFERENCES customer_identity.trusted_sessions(session_hash),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX trusted_sessions_self_history ON customer_identity.trusted_sessions
    (actor_id,established_at DESC,session_hash DESC);
