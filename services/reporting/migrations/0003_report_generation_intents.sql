CREATE TABLE reporting.report_generation_intents (
    operation_id text PRIMARY KEY,
    job_id text NOT NULL UNIQUE,
    report_id text NOT NULL UNIQUE,
    customer_id text NOT NULL,
    actor_id text NOT NULL,
    contract_version text NOT NULL,
    request_id text NOT NULL,
    correlation_id text NOT NULL,
    causation_id text,
    financial_review jsonb,
    fingerprint text NOT NULL CHECK (fingerprint ~ '^[0-9a-f]{64}$'),
    submitted_at timestamptz NOT NULL
);

CREATE INDEX ix_reporting_report_generation_customer
    ON reporting.report_generation_intents (customer_id, submitted_at DESC);

GRANT SELECT, INSERT ON reporting.report_generation_intents TO monergy_reporting_runtime;
