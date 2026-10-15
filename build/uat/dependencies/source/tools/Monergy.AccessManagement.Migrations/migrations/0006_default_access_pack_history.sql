CREATE TABLE am.default_access_pack_history (
    pack_version text PRIMARY KEY CHECK (length(pack_version) BETWEEN 1 AND 32),
    pack_identity text NOT NULL UNIQUE CHECK (length(pack_identity) BETWEEN 1 AND 160),
    installed_at timestamptz NOT NULL,
    operation_id text NOT NULL UNIQUE CHECK (length(operation_id) BETWEEN 1 AND 128),
    plan_hash text CHECK (
        plan_hash IS NULL OR
        (length(plan_hash) = 64 AND plan_hash !~ '[^0-9a-f]')
    ),
    plan_document text CHECK (
        plan_document IS NULL OR
        jsonb_typeof(plan_document::jsonb) = 'object'
    ),
    from_policy_version bigint NOT NULL CHECK (from_policy_version >= 0),
    to_policy_version bigint NOT NULL CHECK (to_policy_version >= from_policy_version),
    installed_by text NOT NULL CHECK (length(installed_by) BETWEEN 1 AND 128)
);

INSERT INTO am.default_access_pack_history (
    pack_version,
    pack_identity,
    installed_at,
    operation_id,
    plan_hash,
    plan_document,
    from_policy_version,
    to_policy_version,
    installed_by)
SELECT
    default_access_pack_version,
    'monergy-default-access-pack/' || default_access_pack_version,
    default_access_pack_installed_at,
    'historical-bootstrap-' || replace(default_access_pack_version, '.', '-'),
    NULL,
    NULL,
    0,
    (SELECT policy_version FROM am.policy_state WHERE singleton),
    'reference-control-plane'
FROM am.tenant_identity
WHERE singleton
  AND default_access_pack_version IS NOT NULL
ON CONFLICT (pack_version) DO NOTHING;

ALTER TABLE am.tenant_identity
    ALTER COLUMN schema_version SET DEFAULT 6;

UPDATE am.tenant_identity
SET schema_version = 6
WHERE singleton;
