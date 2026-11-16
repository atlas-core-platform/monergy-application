ALTER TABLE application_case.tenant_identity
    DROP CONSTRAINT tenant_identity_schema_version_check;
UPDATE application_case.tenant_identity SET schema_version = 5 WHERE singleton;
ALTER TABLE application_case.tenant_identity
    ADD CONSTRAINT tenant_identity_schema_version_check CHECK (schema_version = 5);

CREATE TABLE application_case.property_valuation_assignments (
    tenant_id text NOT NULL REFERENCES application_case.tenant_identity(tenant_id),
    valuation_id text NOT NULL CHECK (valuation_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    property_reference text NOT NULL CHECK (property_reference ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$'),
    property_label text NOT NULL CHECK (length(property_label) BETWEEN 1 AND 120 AND property_label !~ '[[:cntrl:]]'),
    customer_id text NOT NULL CHECK (customer_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    assigned_valuer_actor_id text NOT NULL CHECK (assigned_valuer_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    assignment_source text NOT NULL CHECK (length(assignment_source) BETWEEN 1 AND 160 AND assignment_source !~ '[[:cntrl:]]'),
    state text NOT NULL CHECK (state IN ('Assigned','Inspection in progress','Valued')),
    version integer NOT NULL CHECK (version BETWEEN 1 AND 200),
    assigned_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,valuation_id)
);

CREATE TABLE application_case.property_valuation_evidence (
    tenant_id text NOT NULL,
    valuation_id text NOT NULL,
    evidence_code text NOT NULL CHECK (evidence_code ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$'),
    label text NOT NULL CHECK (length(label) BETWEEN 1 AND 120 AND label !~ '[[:cntrl:]]'),
    owner_service text NOT NULL CHECK (owner_service IN ('Evidence','Financial Profile')),
    resource_reference text NOT NULL CHECK (resource_reference ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$'),
    sequence smallint NOT NULL CHECK (sequence BETWEEN 1 AND 50),
    assigned_version bigint NOT NULL CHECK (assigned_version > 0),
    current_version bigint NOT NULL CHECK (current_version > 0),
    inspected_at timestamptz,
    inspected_by text,
    PRIMARY KEY (tenant_id,valuation_id,evidence_code),
    UNIQUE (tenant_id,valuation_id,sequence),
    FOREIGN KEY (tenant_id,valuation_id)
        REFERENCES application_case.property_valuation_assignments(tenant_id,valuation_id),
    CHECK ((inspected_at IS NULL AND inspected_by IS NULL) OR
        (inspected_at IS NOT NULL AND inspected_by IS NOT NULL))
);

CREATE TABLE application_case.property_valuation_revisions (
    tenant_id text NOT NULL,
    valuation_id text NOT NULL,
    revision integer NOT NULL CHECK (revision BETWEEN 1 AND 100),
    market_value numeric(20,2) NOT NULL CHECK (market_value > 0 AND market_value <= 1000000000000000),
    currency text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    effective_date date NOT NULL CHECK (effective_date >= DATE '1900-01-01'),
    basis text NOT NULL CHECK (length(basis) BETWEEN 3 AND 500 AND basis !~ '[[:cntrl:]]'),
    findings text NOT NULL CHECK (length(findings) BETWEEN 3 AND 2000 AND findings !~ '[[:cntrl:]]'),
    recorded_at timestamptz NOT NULL,
    recorded_by text NOT NULL,
    policy_version bigint NOT NULL CHECK (policy_version > 0),
    subject_version bigint NOT NULL CHECK (subject_version > 0),
    customer_version bigint NOT NULL CHECK (customer_version > 0),
    consent_version bigint NOT NULL CHECK (consent_version > 0),
    PRIMARY KEY (tenant_id,valuation_id,revision),
    FOREIGN KEY (tenant_id,valuation_id)
        REFERENCES application_case.property_valuation_assignments(tenant_id,valuation_id)
);

CREATE TABLE application_case.property_valuation_revision_evidence (
    tenant_id text NOT NULL,
    valuation_id text NOT NULL,
    revision integer NOT NULL,
    evidence_code text NOT NULL,
    owner_service text NOT NULL,
    resource_reference text NOT NULL,
    source_version bigint NOT NULL CHECK (source_version > 0),
    recorded_by text NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,valuation_id,revision,evidence_code),
    FOREIGN KEY (tenant_id,valuation_id,revision)
        REFERENCES application_case.property_valuation_revisions(tenant_id,valuation_id,revision)
);

CREATE TABLE application_case.property_valuation_events (
    event_id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    valuation_id text NOT NULL,
    valuer_actor_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('EVIDENCE_INSPECTION','VALUATION')),
    evidence_code text,
    valuation_revision integer,
    version integer NOT NULL CHECK (version BETWEEN 2 AND 200),
    policy_version bigint NOT NULL CHECK (policy_version > 0),
    subject_version bigint NOT NULL CHECK (subject_version > 0),
    customer_version bigint NOT NULL CHECK (customer_version > 0),
    consent_version bigint NOT NULL CHECK (consent_version > 0),
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id,valuation_id,version),
    FOREIGN KEY (tenant_id,valuation_id)
        REFERENCES application_case.property_valuation_assignments(tenant_id,valuation_id),
    CHECK ((action='EVIDENCE_INSPECTION' AND evidence_code IS NOT NULL AND valuation_revision IS NULL)
        OR (action='VALUATION' AND evidence_code IS NULL AND valuation_revision > 0))
);

CREATE TABLE application_case.property_valuation_operations (
    tenant_id text NOT NULL,
    valuer_actor_id text NOT NULL CHECK (valuer_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    operation_id text NOT NULL CHECK (operation_id ~ '^[0-9a-f]{32}$'),
    valuation_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('EVIDENCE_INSPECTION','VALUATION')),
    request_fingerprint text NOT NULL CHECK (request_fingerprint ~ '^[0-9a-f]{64}$'),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id,valuer_actor_id,operation_id),
    FOREIGN KEY (tenant_id,valuation_id)
        REFERENCES application_case.property_valuation_assignments(tenant_id,valuation_id)
);

REVOKE ALL ON application_case.property_valuation_assignments,
    application_case.property_valuation_evidence,
    application_case.property_valuation_revisions,
    application_case.property_valuation_revision_evidence,
    application_case.property_valuation_events,
    application_case.property_valuation_operations FROM PUBLIC;
