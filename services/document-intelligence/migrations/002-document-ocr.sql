-- Apply as each tenant's Document Intelligence owner; runtime SELECT/INSERT/UPDATE only.
CREATE TABLE document_ocr (
    tenant_id varchar(64) NOT NULL,
    document_key char(64) COLLATE "C" NOT NULL CHECK (document_key ~ '^[0-9a-f]{64}$'),
    source_hash char(64) NOT NULL CHECK (source_hash ~ '^[0-9a-f]{64}$'),
    result jsonb NOT NULL CHECK (jsonb_typeof(result) = 'object'),
    PRIMARY KEY(tenant_id, document_key)
);
