ALTER TABLE application_case.tenant_identity
    DROP CONSTRAINT tenant_identity_schema_version_check;
UPDATE application_case.tenant_identity SET schema_version = 3 WHERE singleton;
ALTER TABLE application_case.tenant_identity
    ADD CONSTRAINT tenant_identity_schema_version_check CHECK (schema_version = 3);

CREATE TABLE application_case.lender_applications (
    tenant_id text NOT NULL REFERENCES application_case.tenant_identity(tenant_id),
    application_id text NOT NULL CHECK (application_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    application_reference text NOT NULL CHECK (length(application_reference) BETWEEN 1 AND 120 AND application_reference !~ '[[:cntrl:]]'),
    assigned_lender_actor_id text NOT NULL CHECK (assigned_lender_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    assignment_source text NOT NULL CHECK (length(assignment_source) BETWEEN 1 AND 160 AND assignment_source !~ '[[:cntrl:]]'),
    state text NOT NULL CHECK (state IN ('Assigned','Under review','Decided')),
    version integer NOT NULL CHECK (version BETWEEN 1 AND 200),
    assigned_at timestamptz NOT NULL,
    decision text CHECK (decision IS NULL OR decision IN ('Approved','Declined','More information required')),
    decision_reason text CHECK (decision_reason IS NULL OR (length(decision_reason) BETWEEN 3 AND 600 AND decision_reason !~ '[[:cntrl:]]')),
    decided_at timestamptz,
    decided_by text,
    PRIMARY KEY (tenant_id,application_id),
    CHECK ((state='Decided' AND decision IS NOT NULL AND decision_reason IS NOT NULL AND decided_at IS NOT NULL AND decided_by=assigned_lender_actor_id)
        OR (state<>'Decided' AND decision IS NULL AND decision_reason IS NULL AND decided_at IS NULL AND decided_by IS NULL))
);

CREATE TABLE application_case.lender_application_documents (
    tenant_id text NOT NULL,
    application_id text NOT NULL,
    document_code text NOT NULL CHECK (document_code ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$'),
    label text NOT NULL CHECK (length(label) BETWEEN 1 AND 120 AND label !~ '[[:cntrl:]]'),
    sequence smallint NOT NULL CHECK (sequence BETWEEN 1 AND 50),
    present boolean NOT NULL,
    reviewed_at timestamptz,
    reviewed_by text,
    PRIMARY KEY (tenant_id,application_id,document_code),
    UNIQUE (tenant_id,application_id,sequence),
    FOREIGN KEY (tenant_id,application_id)
        REFERENCES application_case.lender_applications(tenant_id,application_id),
    CHECK ((reviewed_at IS NULL AND reviewed_by IS NULL) OR (present AND reviewed_at IS NOT NULL AND reviewed_by IS NOT NULL))
);

CREATE TABLE application_case.lender_application_events (
    event_id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    application_id text NOT NULL,
    lender_actor_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('DOCUMENT_REVIEW','DECISION')),
    document_code text,
    decision text,
    reason text,
    version integer NOT NULL CHECK (version BETWEEN 2 AND 200),
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id,application_id,version),
    FOREIGN KEY (tenant_id,application_id)
        REFERENCES application_case.lender_applications(tenant_id,application_id),
    CHECK ((action='DOCUMENT_REVIEW' AND document_code IS NOT NULL AND decision IS NULL AND reason IS NULL)
        OR (action='DECISION' AND document_code IS NULL AND decision IN ('Approved','Declined','More information required')
            AND length(reason) BETWEEN 3 AND 600))
);

CREATE TABLE application_case.lender_application_operations (
    tenant_id text NOT NULL,
    lender_actor_id text NOT NULL CHECK (lender_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    operation_id text NOT NULL CHECK (operation_id ~ '^[0-9a-f]{32}$'),
    application_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('DOCUMENT_REVIEW','DECISION')),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id,lender_actor_id,operation_id),
    FOREIGN KEY (tenant_id,application_id)
        REFERENCES application_case.lender_applications(tenant_id,application_id)
);

REVOKE ALL ON application_case.lender_applications,
    application_case.lender_application_documents,
    application_case.lender_application_events,
    application_case.lender_application_operations FROM PUBLIC;
