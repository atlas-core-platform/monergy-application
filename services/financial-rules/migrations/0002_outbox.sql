CREATE TABLE financial_rules.outbox (
    event_id text PRIMARY KEY,
    contract_id text NOT NULL,
    event_name text NOT NULL,
    event_version text NOT NULL,
    occurred_at timestamptz NOT NULL,
    correlation_id text NOT NULL,
    causation_id text,
    producer text NOT NULL,
    subject_type text NOT NULL,
    subject_id text NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    dispatched_at timestamptz
);

CREATE INDEX ix_financial_rules_outbox_pending
    ON financial_rules.outbox (created_at) WHERE dispatched_at IS NULL;
GRANT SELECT, INSERT, UPDATE (dispatched_at) ON financial_rules.outbox TO monergy_financial_rules_runtime;
