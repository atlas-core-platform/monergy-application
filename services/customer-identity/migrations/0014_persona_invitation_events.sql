-- ID-02C-01: C&I-owned immutable invitation event ledger.
-- This does not create accounts, enroll roles, or issue authentication sessions.
CREATE TABLE customer_identity.persona_invitation_events (
    event_id uuid PRIMARY KEY,
    actor_id text NOT NULL REFERENCES customer_identity.tenant_principals(actor_id),
    invitation_hash text NOT NULL CHECK (invitation_hash ~ '^[0-9a-f]{64}$'),
    action text NOT NULL CHECK (action IN ('ISSUED','CONSUMED','CANCELLED','EXPIRED')),
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(invitation_hash,action)
);
CREATE INDEX persona_invitation_events_actor_time
    ON customer_identity.persona_invitation_events(actor_id,occurred_at DESC);
