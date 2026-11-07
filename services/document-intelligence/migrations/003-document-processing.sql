-- Apply as each tenant's Document Intelligence owner.
-- Runtime may mutate only current processing state and append immutable terminal events.
CREATE TABLE document_processing (
    tenant_id varchar(64) NOT NULL,
    processing_id varchar(160) COLLATE "C" NOT NULL,
    contract_name varchar(160) NOT NULL,
    contract_version varchar(32) NOT NULL,
    customer_id varchar(160) NOT NULL,
    idempotency_key varchar(160) NOT NULL,
    state varchar(32) NOT NULL CHECK (state IN ('Accepted','Running','Completed','Failed')),
    updated_at timestamptz NOT NULL,
    record jsonb NOT NULL CHECK (jsonb_typeof(record) = 'object'),
    PRIMARY KEY (tenant_id, processing_id),
    UNIQUE (tenant_id, contract_name, contract_version, customer_id, idempotency_key)
);
CREATE INDEX document_processing_monitor
    ON document_processing(tenant_id, customer_id, processing_id DESC);

CREATE TABLE document_processing_events (
    tenant_id varchar(64) NOT NULL,
    event_id varchar(200) COLLATE "C" NOT NULL,
    processing_id varchar(160) COLLATE "C" NOT NULL,
    created_at timestamptz NOT NULL,
    event jsonb NOT NULL CHECK (jsonb_typeof(event) = 'object'),
    PRIMARY KEY (tenant_id, event_id),
    FOREIGN KEY (tenant_id, processing_id)
        REFERENCES document_processing(tenant_id, processing_id)
        ON DELETE RESTRICT
);
