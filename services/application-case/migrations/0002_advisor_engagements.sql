ALTER TABLE application_case.tenant_identity
    DROP CONSTRAINT tenant_identity_schema_version_check;
UPDATE application_case.tenant_identity SET schema_version = 2 WHERE singleton;
ALTER TABLE application_case.tenant_identity
    ADD CONSTRAINT tenant_identity_schema_version_check CHECK (schema_version = 2);

CREATE TABLE application_case.advisor_engagements (
    tenant_id text NOT NULL REFERENCES application_case.tenant_identity(tenant_id),
    advisor_actor_id text NOT NULL CHECK (advisor_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    customer_id text NOT NULL CHECK (customer_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    version integer NOT NULL CHECK (version BETWEEN 1 AND 200),
    application_stage text NOT NULL CHECK (application_stage IN
        ('Not started','Preparing','Documents requested','Ready for submission')),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,advisor_actor_id,customer_id),
    CHECK (updated_at >= created_at)
);

CREATE TABLE application_case.advisor_engagement_activities (
    activity_id text PRIMARY KEY CHECK (activity_id ~ '^[0-9a-f]{32}$'),
    tenant_id text NOT NULL,
    advisor_actor_id text NOT NULL,
    customer_id text NOT NULL,
    kind text NOT NULL CHECK (kind IN ('NOTE','COMMUNICATION','APPLICATION_STAGE')),
    body text CHECK (body IS NULL OR (length(body) BETWEEN 1 AND 600 AND body !~ '[[:cntrl:]]')),
    source_fact_ids text[] NOT NULL DEFAULT '{}',
    application_stage text CHECK (application_stage IS NULL OR application_stage IN
        ('Preparing','Documents requested','Ready for submission')),
    version integer NOT NULL CHECK (version BETWEEN 1 AND 200),
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id,advisor_actor_id,customer_id,version),
    FOREIGN KEY (tenant_id,advisor_actor_id,customer_id)
        REFERENCES application_case.advisor_engagements(tenant_id,advisor_actor_id,customer_id),
    CHECK ((kind='NOTE' AND body IS NOT NULL AND cardinality(source_fact_ids) BETWEEN 1 AND 20 AND application_stage IS NULL)
        OR (kind='COMMUNICATION' AND body IS NOT NULL AND cardinality(source_fact_ids)=0 AND application_stage IS NULL)
        OR (kind='APPLICATION_STAGE' AND body IS NULL AND cardinality(source_fact_ids)=0 AND application_stage IS NOT NULL))
);

CREATE TABLE application_case.advisor_engagement_operations (
    tenant_id text NOT NULL,
    advisor_actor_id text NOT NULL CHECK (advisor_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    operation_id text NOT NULL CHECK (operation_id ~ '^[0-9a-f]{32}$'),
    customer_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('NOTE','COMMUNICATION','APPLICATION_STAGE')),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id,advisor_actor_id,operation_id),
    FOREIGN KEY (tenant_id,advisor_actor_id,customer_id)
        REFERENCES application_case.advisor_engagements(tenant_id,advisor_actor_id,customer_id)
);

REVOKE ALL ON application_case.advisor_engagements,
    application_case.advisor_engagement_activities,
    application_case.advisor_engagement_operations FROM PUBLIC;
