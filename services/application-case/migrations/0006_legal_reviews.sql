ALTER TABLE application_case.tenant_identity
    DROP CONSTRAINT tenant_identity_schema_version_check;
UPDATE application_case.tenant_identity SET schema_version = 6 WHERE singleton;
ALTER TABLE application_case.tenant_identity
    ADD CONSTRAINT tenant_identity_schema_version_check CHECK (schema_version = 6);

CREATE TABLE application_case.legal_review_assignments (
    tenant_id text NOT NULL REFERENCES application_case.tenant_identity(tenant_id),
    review_id text NOT NULL CHECK (review_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    matter_reference text NOT NULL CHECK (matter_reference ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$'),
    matter_label text NOT NULL CHECK (length(matter_label) BETWEEN 1 AND 120 AND matter_label !~ '[[:cntrl:]]'),
    customer_id text NOT NULL CHECK (customer_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    assigned_legal_advisor_actor_id text NOT NULL CHECK (assigned_legal_advisor_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    assignment_source text NOT NULL CHECK (length(assignment_source) BETWEEN 1 AND 160 AND assignment_source !~ '[[:cntrl:]]'),
    state text NOT NULL CHECK (state IN ('Assigned','Inspection in progress','Reviewed')),
    version integer NOT NULL CHECK (version BETWEEN 1 AND 200),
    assigned_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,review_id)
);

CREATE TABLE application_case.legal_review_evidence (
    tenant_id text NOT NULL,
    review_id text NOT NULL,
    evidence_code text NOT NULL CHECK (evidence_code ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$'),
    label text NOT NULL CHECK (length(label) BETWEEN 1 AND 120 AND label !~ '[[:cntrl:]]'),
    owner_service text NOT NULL CHECK (owner_service = 'Evidence'),
    resource_reference text NOT NULL CHECK (resource_reference ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$'),
    sequence smallint NOT NULL CHECK (sequence BETWEEN 1 AND 50),
    assigned_version bigint NOT NULL CHECK (assigned_version > 0),
    current_version bigint NOT NULL CHECK (current_version > 0),
    inspected_at timestamptz,
    inspected_by text,
    PRIMARY KEY (tenant_id,review_id,evidence_code),
    UNIQUE (tenant_id,review_id,sequence),
    FOREIGN KEY (tenant_id,review_id)
        REFERENCES application_case.legal_review_assignments(tenant_id,review_id),
    CHECK ((inspected_at IS NULL AND inspected_by IS NULL) OR
        (inspected_at IS NOT NULL AND inspected_by IS NOT NULL))
);

CREATE TABLE application_case.legal_review_revisions (
    tenant_id text NOT NULL,
    review_id text NOT NULL,
    revision integer NOT NULL CHECK (revision BETWEEN 1 AND 100),
    outcome text NOT NULL CHECK (outcome IN ('NO_ISSUE_IDENTIFIED','ACTION_REQUIRED','UNABLE_TO_CONCLUDE')),
    findings text NOT NULL CHECK (length(findings) BETWEEN 3 AND 2000 AND findings !~ '[[:cntrl:]]'),
    recorded_at timestamptz NOT NULL,
    recorded_by text NOT NULL,
    policy_version bigint NOT NULL CHECK (policy_version > 0),
    subject_version bigint NOT NULL CHECK (subject_version > 0),
    customer_version bigint NOT NULL CHECK (customer_version > 0),
    consent_version bigint NOT NULL CHECK (consent_version > 0),
    PRIMARY KEY (tenant_id,review_id,revision),
    FOREIGN KEY (tenant_id,review_id)
        REFERENCES application_case.legal_review_assignments(tenant_id,review_id)
);

CREATE TABLE application_case.legal_review_revision_evidence (
    tenant_id text NOT NULL,
    review_id text NOT NULL,
    revision integer NOT NULL,
    evidence_code text NOT NULL,
    owner_service text NOT NULL,
    resource_reference text NOT NULL,
    source_version bigint NOT NULL CHECK (source_version > 0),
    recorded_by text NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,review_id,revision,evidence_code),
    FOREIGN KEY (tenant_id,review_id,revision)
        REFERENCES application_case.legal_review_revisions(tenant_id,review_id,revision)
);

CREATE TABLE application_case.legal_review_events (
    event_id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    review_id text NOT NULL,
    legal_advisor_actor_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('EVIDENCE_INSPECTION','OUTCOME')),
    evidence_code text,
    review_revision integer,
    version integer NOT NULL CHECK (version BETWEEN 2 AND 200),
    policy_version bigint NOT NULL CHECK (policy_version > 0),
    subject_version bigint NOT NULL CHECK (subject_version > 0),
    customer_version bigint NOT NULL CHECK (customer_version > 0),
    consent_version bigint NOT NULL CHECK (consent_version > 0),
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id,review_id,version),
    FOREIGN KEY (tenant_id,review_id)
        REFERENCES application_case.legal_review_assignments(tenant_id,review_id),
    CHECK ((action='EVIDENCE_INSPECTION' AND evidence_code IS NOT NULL AND review_revision IS NULL)
        OR (action='OUTCOME' AND evidence_code IS NULL AND review_revision > 0))
);

CREATE TABLE application_case.legal_review_operations (
    tenant_id text NOT NULL,
    legal_advisor_actor_id text NOT NULL CHECK (legal_advisor_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    operation_id text NOT NULL CHECK (operation_id ~ '^[0-9a-f]{32}$'),
    review_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('EVIDENCE_INSPECTION','OUTCOME')),
    request_fingerprint text NOT NULL CHECK (request_fingerprint ~ '^[0-9a-f]{64}$'),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id,legal_advisor_actor_id,operation_id),
    FOREIGN KEY (tenant_id,review_id)
        REFERENCES application_case.legal_review_assignments(tenant_id,review_id)
);

REVOKE ALL ON application_case.legal_review_assignments,
    application_case.legal_review_evidence,
    application_case.legal_review_revisions,
    application_case.legal_review_revision_evidence,
    application_case.legal_review_events,
    application_case.legal_review_operations FROM PUBLIC;
