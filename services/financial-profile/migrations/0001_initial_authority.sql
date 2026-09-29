CREATE SCHEMA IF NOT EXISTS financial_profile AUTHORIZATION CURRENT_USER;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA financial_profile FROM PUBLIC;
GRANT USAGE ON SCHEMA financial_profile TO monergy_financial_profile_runtime;

CREATE TABLE financial_profile.profiles (
    financial_profile_id text PRIMARY KEY,
    customer_id text NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    updated_at timestamptz NOT NULL
);

CREATE TABLE financial_profile.fact_revisions (
    financial_fact_id text NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    financial_profile_id text NOT NULL REFERENCES financial_profile.profiles(financial_profile_id),
    customer_id text NOT NULL,
    fact_type text NOT NULL,
    label text NOT NULL,
    value numeric NOT NULL,
    currency text NOT NULL CHECK (char_length(currency) = 3),
    effective_date date NOT NULL,
    financial_provenance_id text NOT NULL,
    source_fact_id text NOT NULL,
    PRIMARY KEY (financial_fact_id, revision),
    UNIQUE (financial_profile_id, fact_type, label, revision)
);

CREATE INDEX ix_financial_profile_fact_current
    ON financial_profile.fact_revisions (financial_profile_id, fact_type, label, revision DESC);

CREATE TABLE financial_profile.provenance (
    financial_provenance_id text PRIMARY KEY,
    financial_fact_id text NOT NULL,
    customer_id text NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE TABLE financial_profile.idempotency_operations (
    contract_name text NOT NULL,
    contract_version text NOT NULL,
    customer_id text NOT NULL,
    idempotency_key text NOT NULL,
    payload_fingerprint text NOT NULL,
    result_payload jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (contract_name, contract_version, customer_id, idempotency_key)
);

GRANT SELECT, INSERT, UPDATE ON financial_profile.profiles TO monergy_financial_profile_runtime;
GRANT SELECT, INSERT ON financial_profile.fact_revisions TO monergy_financial_profile_runtime;
GRANT SELECT, INSERT ON financial_profile.provenance TO monergy_financial_profile_runtime;
GRANT SELECT, INSERT ON financial_profile.idempotency_operations TO monergy_financial_profile_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA financial_profile
    GRANT SELECT, INSERT ON TABLES TO monergy_financial_profile_runtime;
