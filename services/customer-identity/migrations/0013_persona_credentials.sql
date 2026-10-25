-- ID-01: email-verified credentials for existing tenant principals.
-- No membership/role/customer ownership created or modified.
ALTER TABLE customer_identity.registration_challenges
    DROP CONSTRAINT registration_challenges_purpose_check;
ALTER TABLE customer_identity.registration_challenges
    ADD CONSTRAINT registration_challenges_purpose_check
    CHECK (purpose IN ('registration','recovery','persona-activation','persona-recovery'));

CREATE TABLE customer_identity.persona_credentials (
    actor_id text PRIMARY KEY REFERENCES customer_identity.tenant_principals(actor_id),
    password_hash text NOT NULL CHECK(length(password_hash) BETWEEN 32 AND 1024),
    credential_version bigint NOT NULL DEFAULT 1 CHECK(credential_version > 0),
    failed_attempts integer NOT NULL DEFAULT 0 CHECK(failed_attempts BETWEEN 0 AND 5),
    locked_until timestamptz,
    activated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX persona_credentials_activated_idx
    ON customer_identity.persona_credentials(activated_at);
