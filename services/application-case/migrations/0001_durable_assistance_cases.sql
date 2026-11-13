CREATE SCHEMA application_case AUTHORIZATION CURRENT_USER;
REVOKE ALL ON SCHEMA application_case FROM PUBLIC;

CREATE TABLE application_case.tenant_identity (
    singleton boolean PRIMARY KEY CHECK (singleton),
    tenant_id text NOT NULL CHECK (tenant_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$'),
    schema_version integer NOT NULL CHECK (schema_version = 1),
    UNIQUE (tenant_id)
);

CREATE TABLE application_case.assistance_cases (
    case_id text PRIMARY KEY CHECK (case_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$'),
    tenant_id text NOT NULL REFERENCES application_case.tenant_identity(tenant_id),
    customer_id text NOT NULL CHECK (customer_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    customer_actor_id text NOT NULL CHECK (customer_actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    request_type text NOT NULL CHECK (request_type = 'Financial assistance'),
    state text NOT NULL CHECK (state IN ('Submitted','Accepted')),
    version integer NOT NULL CHECK (version IN (1,2)),
    created_at timestamptz NOT NULL,
    accepted_at timestamptz,
    accepted_by text CHECK (accepted_by IS NULL OR accepted_by ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    CHECK ((state='Submitted' AND version=1 AND accepted_at IS NULL AND accepted_by IS NULL)
        OR (state='Accepted' AND version=2 AND accepted_at IS NOT NULL AND accepted_by IS NOT NULL)),
    UNIQUE (case_id,tenant_id)
);
CREATE UNIQUE INDEX assistance_cases_one_active_customer
    ON application_case.assistance_cases(tenant_id,customer_id)
    WHERE state IN ('Submitted','Accepted');
CREATE INDEX assistance_cases_work_queue
    ON application_case.assistance_cases(tenant_id,state,created_at);

CREATE TABLE application_case.assistance_case_operations (
    tenant_id text NOT NULL,
    actor_id text NOT NULL CHECK (actor_id ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$'),
    operation_id text NOT NULL CHECK (operation_id ~ '^[0-9a-f]{32}$'),
    case_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('CREATE','ACCEPT')),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id,actor_id,operation_id),
    FOREIGN KEY (case_id,tenant_id)
        REFERENCES application_case.assistance_cases(case_id,tenant_id)
);

CREATE TABLE application_case.assistance_case_events (
    event_id text PRIMARY KEY CHECK (event_id ~ '^[0-9a-f]{32}$'),
    case_id text NOT NULL,
    tenant_id text NOT NULL,
    action text NOT NULL CHECK (action IN ('Submitted','Accepted')),
    version integer NOT NULL CHECK (version IN (1,2)),
    occurred_at timestamptz NOT NULL,
    UNIQUE (case_id,version),
    FOREIGN KEY (case_id,tenant_id)
        REFERENCES application_case.assistance_cases(case_id,tenant_id)
);

REVOKE ALL ON ALL TABLES IN SCHEMA application_case FROM PUBLIC;
