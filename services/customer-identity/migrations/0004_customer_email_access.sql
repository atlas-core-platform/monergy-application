-- T1-43. C&I-owned email challenge/outbox; tenant isolation is the database binding.
-- No password/PIN, AM membership, role, customer or ownership is created here.
CREATE TABLE customer_identity.email_access_challenges (
    normalized_email text COLLATE "C" PRIMARY KEY REFERENCES customer_identity.tenant_principals(normalized_email),
    challenge_hash text UNIQUE NOT NULL CHECK (challenge_hash ~ '^[0-9a-f]{64}$'),
    code_hash text NOT NULL CHECK (code_hash ~ '^[0-9a-f]{64}$'),
    actor_id text NOT NULL REFERENCES customer_identity.tenant_principals(actor_id),
    customer_id text NOT NULL REFERENCES customer_identity.customers(customer_id),
    customer_version bigint NOT NULL CHECK (customer_version>0),
    protected_code bytea CHECK (octet_length(protected_code)=34),
    requested_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    request_window timestamptz NOT NULL,
    requests integer NOT NULL CHECK (requests BETWEEN 1 AND 5),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 5),
    consumed boolean NOT NULL DEFAULT false,
    delivered boolean NOT NULL DEFAULT false,
    delivery_attempt integer NOT NULL DEFAULT 0 CHECK (delivery_attempt BETWEEN 0 AND 3),
    lease_until timestamptz,
    CHECK (expires_at=requested_at+interval '5 minutes')
);
CREATE INDEX email_access_pending ON customer_identity.email_access_challenges(requested_at)
    WHERE protected_code IS NOT NULL AND NOT consumed;
CREATE TABLE customer_identity.email_access_events (
    event_id uuid PRIMARY KEY,
    challenge_hash text NOT NULL CHECK (challenge_hash ~ '^[0-9a-f]{64}$'),
    actor_id text NOT NULL,
    customer_id text NOT NULL,
    outcome text NOT NULL CHECK (outcome IN ('REQUESTED','DELIVERED','DELIVERY_FAILED','CODE_REJECTED','CODE_VERIFIED','SESSION_ISSUED','SESSION_DENIED')),
    recorded_at timestamptz NOT NULL
);
