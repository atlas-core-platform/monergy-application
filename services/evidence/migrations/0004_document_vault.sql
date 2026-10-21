-- Customer organization metadata is separate from immutable original evidence and machine classification.
ALTER TABLE evidence.document_versions ADD COLUMN source_location jsonb;
CREATE TABLE evidence.vault_metadata (
    document_version_id text PRIMARY KEY REFERENCES evidence.document_versions(document_version_id),
    category text NOT NULL CHECK (category IN ('Identity','Income Tax','Banking','Credit','Loans','Insurance','Investments','Property','Vehicles','Other')),
    revision integer NOT NULL CHECK (revision > 0),
    metadata jsonb NOT NULL
);
CREATE TABLE evidence.vault_operations (
    tenant_id text NOT NULL, customer_id text NOT NULL, operation_id text NOT NULL,
    document_version_id text NOT NULL REFERENCES evidence.document_versions(document_version_id),
    actor_id text NOT NULL, change jsonb NOT NULL, receipt jsonb NOT NULL,
    PRIMARY KEY (tenant_id, customer_id, operation_id)
);
GRANT SELECT, INSERT, UPDATE ON evidence.vault_metadata TO monergy_evidence_runtime;
GRANT SELECT, INSERT ON evidence.vault_operations TO monergy_evidence_runtime;
