-- Apply with the Document Intelligence database owner, once in EACH tenant database.
-- Runtime role: CONNECT, USAGE on public; SELECT/INSERT/UPDATE on this table only.
CREATE TABLE document_classifications (
    tenant_id varchar(64) NOT NULL,
    classification_key char(64) COLLATE "C" NOT NULL CHECK (classification_key ~ '^[0-9a-f]{64}$'),
    record jsonb NOT NULL CHECK (jsonb_typeof(record) = 'object'),
    needs_review boolean NOT NULL,
    PRIMARY KEY (tenant_id, classification_key)
);
CREATE INDEX document_classifications_review ON document_classifications(tenant_id, classification_key) WHERE needs_review;
