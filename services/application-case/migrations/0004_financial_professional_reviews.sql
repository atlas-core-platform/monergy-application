ALTER TABLE application_case.tenant_identity
    DROP CONSTRAINT tenant_identity_schema_version_check;
UPDATE application_case.tenant_identity SET schema_version = 4 WHERE singleton;
ALTER TABLE application_case.tenant_identity
    ADD CONSTRAINT tenant_identity_schema_version_check CHECK (schema_version = 4);

CREATE TABLE application_case.financial_professional_reviews (
    tenant_id text NOT NULL REFERENCES application_case.tenant_identity(tenant_id),
    review_id text NOT NULL CHECK (review_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    review_reference text NOT NULL CHECK (length(review_reference) BETWEEN 1 AND 120 AND review_reference !~ '[[:cntrl:]]'),
    customer_id text NOT NULL CHECK (customer_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    assigned_professional_actor_id text NOT NULL CHECK (assigned_professional_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    assignment_source text NOT NULL CHECK (length(assignment_source) BETWEEN 1 AND 160 AND assignment_source !~ '[[:cntrl:]]'),
    state text NOT NULL CHECK (state IN ('Assigned','Under review','Certified')),
    version integer NOT NULL CHECK (version BETWEEN 1 AND 200),
    assigned_at timestamptz NOT NULL,
    certification_outcome text CHECK (certification_outcome IS NULL OR certification_outcome IN
        ('Certified with no exceptions','Certified with exceptions','Unable to certify')),
    certification_rationale text CHECK (certification_rationale IS NULL OR
        (length(certification_rationale) BETWEEN 3 AND 2000 AND certification_rationale !~ '[[:cntrl:]]')),
    certified_at timestamptz,
    certified_by text,
    certified_policy_version bigint,
    certified_subject_version bigint,
    certified_customer_version bigint,
    certified_consent_version bigint,
    PRIMARY KEY (tenant_id,review_id),
    CHECK ((state='Certified' AND certification_outcome IS NOT NULL AND certification_rationale IS NOT NULL
        AND certified_at IS NOT NULL AND certified_by=assigned_professional_actor_id
        AND certified_policy_version > 0 AND certified_subject_version > 0
        AND certified_customer_version > 0 AND certified_consent_version > 0)
      OR (state<>'Certified' AND certification_outcome IS NULL AND certification_rationale IS NULL
        AND certified_at IS NULL AND certified_by IS NULL AND certified_policy_version IS NULL
        AND certified_subject_version IS NULL AND certified_customer_version IS NULL
        AND certified_consent_version IS NULL))
);

CREATE TABLE application_case.financial_review_sources (
    tenant_id text NOT NULL,
    review_id text NOT NULL,
    source_code text NOT NULL CHECK (source_code ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$'),
    label text NOT NULL CHECK (length(label) BETWEEN 1 AND 120 AND label !~ '[[:cntrl:]]'),
    owner_service text NOT NULL CHECK (owner_service IN ('Financial Profile','Evidence','Reporting')),
    resource_reference text NOT NULL CHECK (resource_reference ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$'),
    sequence smallint NOT NULL CHECK (sequence BETWEEN 1 AND 50),
    assigned_version bigint NOT NULL CHECK (assigned_version > 0),
    current_version bigint NOT NULL CHECK (current_version > 0),
    reviewed_at timestamptz,
    reviewed_by text,
    PRIMARY KEY (tenant_id,review_id,source_code),
    UNIQUE (tenant_id,review_id,sequence),
    FOREIGN KEY (tenant_id,review_id)
        REFERENCES application_case.financial_professional_reviews(tenant_id,review_id),
    CHECK ((reviewed_at IS NULL AND reviewed_by IS NULL) OR
        (reviewed_at IS NOT NULL AND reviewed_by IS NOT NULL))
);

CREATE TABLE application_case.financial_review_notes (
    note_id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    review_id text NOT NULL,
    professional_actor_id text NOT NULL,
    note text NOT NULL CHECK (length(note) BETWEEN 3 AND 2000 AND note !~ '[[:cntrl:]]'),
    source_codes text[] NOT NULL CHECK (cardinality(source_codes) BETWEEN 1 AND 20),
    recorded_at timestamptz NOT NULL,
    FOREIGN KEY (tenant_id,review_id)
        REFERENCES application_case.financial_professional_reviews(tenant_id,review_id)
);

CREATE TABLE application_case.financial_review_certification_sources (
    tenant_id text NOT NULL,
    review_id text NOT NULL,
    source_code text NOT NULL,
    owner_service text NOT NULL,
    resource_reference text NOT NULL,
    source_version bigint NOT NULL CHECK (source_version > 0),
    certified_by text NOT NULL,
    certified_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,review_id,source_code),
    FOREIGN KEY (tenant_id,review_id)
        REFERENCES application_case.financial_professional_reviews(tenant_id,review_id)
);

CREATE TABLE application_case.financial_review_events (
    event_id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    review_id text NOT NULL,
    professional_actor_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('SOURCE_REVIEW','NOTE','CERTIFICATION')),
    source_code text,
    note_source_codes text[],
    outcome text,
    version integer NOT NULL CHECK (version BETWEEN 2 AND 200),
    policy_version bigint NOT NULL CHECK (policy_version > 0),
    subject_version bigint NOT NULL CHECK (subject_version > 0),
    customer_version bigint NOT NULL CHECK (customer_version > 0),
    consent_version bigint NOT NULL CHECK (consent_version > 0),
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id,review_id,version),
    FOREIGN KEY (tenant_id,review_id)
        REFERENCES application_case.financial_professional_reviews(tenant_id,review_id),
    CHECK ((action='SOURCE_REVIEW' AND source_code IS NOT NULL AND note_source_codes IS NULL AND outcome IS NULL)
        OR (action='NOTE' AND source_code IS NULL AND cardinality(note_source_codes) BETWEEN 1 AND 20 AND outcome IS NULL)
        OR (action='CERTIFICATION' AND source_code IS NULL AND note_source_codes IS NULL
            AND outcome IN ('Certified with no exceptions','Certified with exceptions','Unable to certify')))
);

CREATE TABLE application_case.financial_review_operations (
    tenant_id text NOT NULL,
    professional_actor_id text NOT NULL CHECK (professional_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    operation_id text NOT NULL CHECK (operation_id ~ '^[0-9a-f]{32}$'),
    review_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('SOURCE_REVIEW','NOTE','CERTIFICATION')),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id,professional_actor_id,operation_id),
    FOREIGN KEY (tenant_id,review_id)
        REFERENCES application_case.financial_professional_reviews(tenant_id,review_id)
);

REVOKE ALL ON application_case.financial_professional_reviews,
    application_case.financial_review_sources,
    application_case.financial_review_notes,
    application_case.financial_review_certification_sources,
    application_case.financial_review_events,
    application_case.financial_review_operations FROM PUBLIC;
