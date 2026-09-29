CREATE SCHEMA IF NOT EXISTS financial_rules AUTHORIZATION CURRENT_USER;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA financial_rules FROM PUBLIC;
GRANT USAGE ON SCHEMA financial_rules TO monergy_financial_rules_runtime;

CREATE TABLE financial_rules.calculations (
    calculation_id text PRIMARY KEY,
    customer_id text NOT NULL,
    rule_id text NOT NULL,
    rule_version text NOT NULL,
    request_fingerprint text NOT NULL,
    inputs jsonb NOT NULL,
    result jsonb,
    error jsonb,
    outcome_event jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    CHECK ((result IS NULL) <> (error IS NULL))
);

CREATE INDEX ix_financial_rules_calculations_customer
    ON financial_rules.calculations (customer_id, calculation_id);

CREATE TABLE financial_rules.idempotency_operations (
    contract_name text NOT NULL,
    contract_version text NOT NULL,
    customer_id text NOT NULL,
    idempotency_key text NOT NULL,
    request_fingerprint text NOT NULL,
    calculation_id text NOT NULL REFERENCES financial_rules.calculations(calculation_id),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (contract_name, contract_version, customer_id, idempotency_key)
);

GRANT SELECT, INSERT ON financial_rules.calculations TO monergy_financial_rules_runtime;
GRANT SELECT, INSERT ON financial_rules.idempotency_operations TO monergy_financial_rules_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA financial_rules
    GRANT SELECT, INSERT ON TABLES TO monergy_financial_rules_runtime;
