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

-- Historical request-to-permission evidence is independent from the removable effective grant.
-- A permission shared by several targets retains one immutable snapshot per fulfilled request.
ALTER TABLE am.access_requests
    ADD CONSTRAINT ux_access_requests_requester_snapshot UNIQUE (request_id, requester_actor_id);

CREATE TABLE am.access_request_permission_grants (
    request_id text NOT NULL,
    actor_id text NOT NULL CHECK (actor_id <> ''),
    permission_id text NOT NULL,
    permission_code text NOT NULL CHECK (permission_code <> ''),
    capability_id text NOT NULL CHECK (capability_id <> ''),
    scope text NOT NULL CHECK (scope IN ('Tenant','Resource')),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (request_id, permission_id, capability_id, scope),
    FOREIGN KEY (request_id, actor_id)
        REFERENCES am.access_requests(request_id, requester_actor_id) ON DELETE RESTRICT
);
CREATE INDEX ix_access_request_permission_grants_actor
    ON am.access_request_permission_grants(actor_id, permission_id);

INSERT INTO am.access_request_permission_grants (
    request_id,actor_id,permission_id,permission_code,capability_id,scope,recorded_at)
SELECT sp.source_request_id,sp.actor_id,sp.permission_id,p.code,pc.capability_id,pc.scope,sp.granted_at
FROM am.subject_permissions sp
JOIN am.permissions p ON p.permission_id=sp.permission_id
JOIN am.permission_capabilities pc ON pc.permission_id=sp.permission_id;

CREATE FUNCTION am.reject_access_request_permission_grant_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'access request permission history is append-only';
END;
$$;

CREATE TRIGGER access_request_permission_grants_append_only
BEFORE UPDATE OR DELETE ON am.access_request_permission_grants
FOR EACH ROW EXECUTE FUNCTION am.reject_access_request_permission_grant_mutation();

ALTER TABLE am.tenant_identity ALTER COLUMN schema_version SET DEFAULT 8;
UPDATE am.tenant_identity SET schema_version = 8 WHERE singleton;
