CREATE SCHEMA IF NOT EXISTS job_management AUTHORIZATION monergy_job_management_owner;
GRANT USAGE ON SCHEMA job_management TO monergy_job_management_runtime;

CREATE TABLE job_management.jobs (
    job_id text PRIMARY KEY,
    job_name text NOT NULL,
    job_version text NOT NULL,
    owner_service text NOT NULL,
    payload_reference text NOT NULL,
    customer_id text NOT NULL,
    request_id text NOT NULL,
    correlation_id text NOT NULL,
    causation_id text NULL,
    security_context jsonb NOT NULL,
    idempotency_key text NOT NULL,
    payload_fingerprint text NOT NULL,
    state text NOT NULL,
    attempt integer NOT NULL DEFAULT 0 CHECK (attempt >= 0),
    replay_count integer NOT NULL DEFAULT 0 CHECK (replay_count >= 0),
    requested_at timestamptz NOT NULL,
    scheduled_at timestamptz NOT NULL,
    next_attempt_at timestamptz NULL,
    cancellation_requested_at timestamptz NULL,
    terminal_at timestamptz NULL,
    failure_code text NULL,
    retryable boolean NOT NULL DEFAULT false,
    outcome_reference text NULL,
    updated_at timestamptz NOT NULL
);

CREATE TABLE job_management.idempotency_operations (
    contract_name text NOT NULL,
    contract_version text NOT NULL,
    customer_id text NOT NULL,
    idempotency_key text NOT NULL,
    payload_fingerprint text NOT NULL,
    job_id text NOT NULL REFERENCES job_management.jobs(job_id),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (contract_name, contract_version, customer_id, idempotency_key)
);

CREATE TABLE job_management.execution_attempts (
    job_id text NOT NULL REFERENCES job_management.jobs(job_id),
    attempt integer NOT NULL,
    started_at timestamptz NOT NULL,
    ended_at timestamptz NULL,
    outcome text NULL,
    failure_code text NULL,
    retryable boolean NOT NULL DEFAULT false,
    PRIMARY KEY (job_id, attempt)
);

CREATE TABLE job_management.outbox (
    event_id text PRIMARY KEY,
    contract_id text NOT NULL,
    event_name text NOT NULL,
    event_version text NOT NULL,
    occurred_at timestamptz NOT NULL,
    correlation_id text NOT NULL,
    causation_id text NULL,
    producer text NOT NULL,
    subject_type text NOT NULL,
    subject_id text NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    dispatched_at timestamptz NULL
);

GRANT SELECT, INSERT, UPDATE ON job_management.jobs TO monergy_job_management_runtime;
GRANT SELECT, INSERT ON job_management.idempotency_operations TO monergy_job_management_runtime;
GRANT SELECT, INSERT, UPDATE ON job_management.execution_attempts TO monergy_job_management_runtime;
GRANT SELECT, INSERT, UPDATE (dispatched_at) ON job_management.outbox TO monergy_job_management_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA job_management
    GRANT SELECT, INSERT ON TABLES TO monergy_job_management_runtime;
