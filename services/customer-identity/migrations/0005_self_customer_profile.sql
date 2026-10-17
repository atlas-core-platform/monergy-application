-- T1-44. Editable customer contact/profile version is separate from ownership
-- version. Changing a display name cannot transfer ownership or revoke Consent.
CREATE TABLE customer_identity.customer_profiles (
    customer_id text PRIMARY KEY REFERENCES customer_identity.customers(customer_id),
    display_name text NOT NULL CHECK (length(btrim(display_name)) BETWEEN 1 AND 120),
    mobile text CHECK (mobile ~ '^\+[1-9][0-9]{6,14}$'),
    version bigint NOT NULL CHECK (version>0),
    updated_at timestamptz NOT NULL
);
CREATE TABLE customer_identity.customer_profile_receipts (
    operation_id uuid PRIMARY KEY,
    customer_id text NOT NULL REFERENCES customer_identity.customers(customer_id),
    actor_id text NOT NULL REFERENCES customer_identity.tenant_principals(actor_id),
    fingerprint text NOT NULL CHECK (fingerprint ~ '^[0-9a-f]{64}$'),
    profile_version bigint NOT NULL CHECK (profile_version>0),
    recorded_at timestamptz NOT NULL
);
