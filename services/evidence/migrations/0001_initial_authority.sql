CREATE SCHEMA IF NOT EXISTS evidence AUTHORIZATION CURRENT_USER;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA evidence FROM PUBLIC;
GRANT USAGE ON SCHEMA evidence TO monergy_evidence_runtime;

CREATE TABLE evidence.document_versions (
    document_version_id text PRIMARY KEY,
    document_id text NOT NULL,
    evidence_id text NOT NULL,
    customer_id text NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    source_name text NOT NULL,
    original_file_name text NOT NULL,
    content_type text NOT NULL,
    content_reference text NOT NULL,
    content_storage_key text NOT NULL,
    content_storage_version_id text NOT NULL,
    content_length bigint NOT NULL CHECK (content_length >= 0),
    content_sha256 text NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    received_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (document_id, version),
    UNIQUE (evidence_id, document_version_id)
);

CREATE INDEX ix_evidence_document_versions_document
    ON evidence.document_versions (document_id, version);
CREATE INDEX ix_evidence_document_versions_customer
    ON evidence.document_versions (customer_id, document_id);

CREATE TABLE evidence.idempotency_operations (
    contract_name text NOT NULL,
    contract_version text NOT NULL,
    customer_id text NOT NULL,
    idempotency_key text NOT NULL,
    payload_fingerprint text NOT NULL,
    document_version_id text NOT NULL REFERENCES evidence.document_versions(document_version_id),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (contract_name, contract_version, customer_id, idempotency_key)
);

GRANT SELECT, INSERT ON evidence.document_versions TO monergy_evidence_runtime;
GRANT SELECT, INSERT ON evidence.idempotency_operations TO monergy_evidence_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA evidence
    GRANT SELECT, INSERT ON TABLES TO monergy_evidence_runtime;
