CREATE SCHEMA IF NOT EXISTS reporting AUTHORIZATION CURRENT_USER;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA reporting FROM PUBLIC;
GRANT USAGE ON SCHEMA reporting TO monergy_reporting_runtime;

CREATE TABLE reporting.reports (
    report_id text PRIMARY KEY,
    customer_id text NOT NULL,
    state text NOT NULL,
    generated_at timestamptz NOT NULL,
    items jsonb NOT NULL,
    source_financial_references jsonb NOT NULL,
    evidence_references jsonb NOT NULL,
    financial_provenance_references jsonb NOT NULL,
    calculation_lineage_references jsonb NOT NULL,
    ai_response_trace_reference text,
    audit_compatibility_reference_id text NOT NULL,
    export_file_name text NOT NULL,
    export_media_type text NOT NULL,
    export_content text NOT NULL,
    export_sha256 text NOT NULL CHECK (export_sha256 ~ '^[0-9A-F]{64}$')
);

CREATE INDEX ix_reporting_reports_customer ON reporting.reports (customer_id, report_id);

CREATE TABLE reporting.idempotency_operations (
    contract_name text NOT NULL,
    contract_version text NOT NULL,
    customer_id text NOT NULL,
    idempotency_key text NOT NULL,
    report_id text NOT NULL REFERENCES reporting.reports(report_id),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (contract_name, contract_version, customer_id, idempotency_key)
);

GRANT SELECT, INSERT ON reporting.reports TO monergy_reporting_runtime;
GRANT SELECT, INSERT ON reporting.idempotency_operations TO monergy_reporting_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA reporting
    GRANT SELECT, INSERT ON TABLES TO monergy_reporting_runtime;
