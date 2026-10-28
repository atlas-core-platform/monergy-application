CREATE TABLE am.access_requests (
    request_id text PRIMARY KEY CHECK (request_id ~ '^[0-9a-f]{32}$'),
    tenant_id text NOT NULL REFERENCES am.tenant_identity(tenant_id) ON DELETE RESTRICT,
    requester_actor_id text NOT NULL REFERENCES am.access_subjects(actor_id) ON DELETE RESTRICT,
    target_code text NOT NULL CHECK (target_code = 'financial-advisor.assigned-customer-financial-review'),
    requester_role_id text NOT NULL,
    requester_subject_version bigint NOT NULL CHECK (requester_subject_version > 0),
    submit_operation_id text NOT NULL UNIQUE CHECK (submit_operation_id ~ '^[0-9a-f]{32}$'),
    state text NOT NULL CHECK (state IN ('Submitted', 'Rejected', 'Fulfilled')),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at timestamptz NOT NULL,
    decision_at timestamptz,
    deciding_actor_id text REFERENCES am.access_subjects(actor_id) ON DELETE RESTRICT,
    decision_action text CHECK (decision_action IN ('Approve', 'Reject')),
    decision_operation_id text UNIQUE CHECK (
        decision_operation_id IS NULL OR decision_operation_id ~ '^[0-9a-f]{32}$'),
    decision_reason text CHECK (
        decision_reason IS NULL OR decision_reason IN ('AdministratorRejected', 'RequesterNoLongerEligible',
            'RequestExpired', 'CapabilityUnavailable')),
    fulfilled_at timestamptz,
    CHECK (expires_at > created_at),
    CHECK (
        (state = 'Submitted' AND decision_at IS NULL AND deciding_actor_id IS NULL AND
            decision_action IS NULL AND decision_operation_id IS NULL AND decision_reason IS NULL AND fulfilled_at IS NULL) OR
        (state = 'Rejected' AND decision_at IS NOT NULL AND deciding_actor_id IS NOT NULL AND
            decision_action IS NOT NULL AND decision_operation_id IS NOT NULL AND decision_reason IS NOT NULL AND fulfilled_at IS NULL) OR
        (state = 'Fulfilled' AND decision_at IS NOT NULL AND deciding_actor_id IS NOT NULL AND
            decision_action = 'Approve' AND decision_operation_id IS NOT NULL AND decision_reason IS NULL AND fulfilled_at IS NOT NULL)
    )
);

CREATE UNIQUE INDEX ux_access_requests_open_target
    ON am.access_requests (requester_actor_id, target_code)
    WHERE state = 'Submitted';
CREATE INDEX ix_access_requests_review
    ON am.access_requests (request_id) INCLUDE (state, created_at);
CREATE INDEX ix_access_requests_requester
    ON am.access_requests (requester_actor_id, created_at DESC, request_id DESC);

CREATE TABLE am.subject_permissions (
    actor_id text NOT NULL REFERENCES am.access_subjects(actor_id) ON DELETE CASCADE,
    permission_id text NOT NULL REFERENCES am.permissions(permission_id) ON DELETE RESTRICT,
    granted_at timestamptz NOT NULL,
    granted_by text NOT NULL REFERENCES am.access_subjects(actor_id) ON DELETE RESTRICT,
    source_request_id text NOT NULL REFERENCES am.access_requests(request_id) ON DELETE RESTRICT,
    PRIMARY KEY (actor_id, permission_id)
);
CREATE INDEX ix_subject_permissions_permission ON am.subject_permissions(permission_id);
CREATE INDEX ix_subject_permissions_request ON am.subject_permissions(source_request_id);

CREATE TABLE am.access_request_events (
    event_id uuid PRIMARY KEY,
    request_id text NOT NULL REFERENCES am.access_requests(request_id) ON DELETE RESTRICT,
    event_type text NOT NULL CHECK (event_type IN ('Submitted', 'Approved', 'Rejected', 'Fulfilled')),
    actor_id text NOT NULL REFERENCES am.access_subjects(actor_id) ON DELETE RESTRICT,
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    policy_version bigint NOT NULL CHECK (policy_version > 0),
    reason text CHECK (
        reason IS NULL OR reason IN ('AdministratorRejected', 'RequesterNoLongerEligible',
            'RequestExpired', 'CapabilityUnavailable'))
);
CREATE INDEX ix_access_request_events_request ON am.access_request_events(request_id, occurred_at, event_id);

CREATE FUNCTION am.reject_access_request_event_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'access request history is append-only';
END;
$$;

CREATE TRIGGER access_request_events_append_only
BEFORE UPDATE OR DELETE ON am.access_request_events
FOR EACH ROW EXECUTE FUNCTION am.reject_access_request_event_mutation();

ALTER TABLE am.tenant_identity ALTER COLUMN schema_version SET DEFAULT 7;
UPDATE am.tenant_identity SET schema_version = 7 WHERE singleton;
