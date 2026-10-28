ALTER TABLE am.access_requests
    ADD COLUMN target_definition_hash text;

-- Rows created by the bounded AM-IRA-01 target retain the exact definition they represented.
UPDATE am.access_requests
SET target_definition_hash = 'd50763216406ac40d3f43a6cc92b44190964dcdfcd7d5ffcb6a24871c06594a3'
WHERE target_code = 'financial-advisor.assigned-customer-financial-review';

ALTER TABLE am.access_requests
    ALTER COLUMN target_definition_hash SET NOT NULL,
    ADD CONSTRAINT access_requests_target_definition_hash_check
        CHECK (target_definition_hash ~ '^[0-9a-f]{64}$');

ALTER TABLE am.access_requests DROP CONSTRAINT access_requests_target_code_check;
ALTER TABLE am.access_requests
    ADD CONSTRAINT access_requests_target_code_check
        CHECK (target_code ~ '^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+$');

-- An effective subject permission has one materialized row but may be justified by more than one
-- fulfilled request. This mapping preserves every provenance without overwriting the first source.
CREATE TABLE am.access_request_permission_grants (
    request_id text NOT NULL REFERENCES am.access_requests(request_id) ON DELETE RESTRICT,
    actor_id text NOT NULL,
    permission_id text NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (request_id, permission_id),
    FOREIGN KEY (actor_id, permission_id)
        REFERENCES am.subject_permissions(actor_id, permission_id) ON DELETE CASCADE
);
CREATE INDEX ix_access_request_permission_grants_actor
    ON am.access_request_permission_grants(actor_id, permission_id);

INSERT INTO am.access_request_permission_grants (request_id,actor_id,permission_id,recorded_at)
SELECT source_request_id,actor_id,permission_id,granted_at
FROM am.subject_permissions;

ALTER TABLE am.tenant_identity ALTER COLUMN schema_version SET DEFAULT 8;
UPDATE am.tenant_identity SET schema_version = 8 WHERE singleton;
