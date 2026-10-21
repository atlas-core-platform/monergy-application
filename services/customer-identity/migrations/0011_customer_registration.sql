-- T1-95. Approved email/OTP/password registration. C&I-owned tenant database.
CREATE TABLE customer_identity.registration_challenges (
 purpose text NOT NULL CHECK(purpose IN ('registration','recovery')),
 email text COLLATE "C" NOT NULL,
 challenge_hash text NOT NULL UNIQUE CHECK(length(challenge_hash)=64),
 code_hash text NOT NULL CHECK(length(code_hash)=64),
 protected_code bytea,
 requested_at timestamptz NOT NULL,
 expires_at timestamptz NOT NULL,
 window_at timestamptz NOT NULL,
 requests integer NOT NULL,
 attempts integer NOT NULL DEFAULT 0,
 consumed boolean NOT NULL DEFAULT false,
 delivered boolean NOT NULL DEFAULT false,
 delivery_attempt integer NOT NULL DEFAULT 0,
 lease_until timestamptz,
 PRIMARY KEY(purpose,email),
 CHECK(attempts BETWEEN 0 AND 5),
 CHECK(requests BETWEEN 1 AND 5),
 CHECK(delivery_attempt BETWEEN 0 AND 3),
 CHECK(protected_code IS NULL OR octet_length(protected_code)=34)
);
CREATE TABLE customer_identity.registered_credentials (
 actor_id text PRIMARY KEY REFERENCES customer_identity.tenant_principals,
 customer_id text NOT NULL UNIQUE REFERENCES customer_identity.customers,
 password_hash text NOT NULL,
 credential_version bigint NOT NULL DEFAULT 1 CHECK(credential_version>0),
 enrolled boolean NOT NULL DEFAULT false,
 failed_attempts integer NOT NULL DEFAULT 0,
 locked_until timestamptz
);
CREATE TABLE customer_identity.registration_events (
 event_id uuid PRIMARY KEY,
 action text NOT NULL,
 actor_id text,
 customer_id text,
 occurred_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
