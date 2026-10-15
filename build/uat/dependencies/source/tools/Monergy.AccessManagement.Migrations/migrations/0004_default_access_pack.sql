ALTER TABLE am.tenant_identity
    ADD COLUMN default_access_pack_version text,
    ADD COLUMN default_access_pack_installed_at timestamptz,
    ADD CONSTRAINT ck_default_access_pack_pair
        CHECK ((default_access_pack_version IS NULL) = (default_access_pack_installed_at IS NULL));

ALTER TABLE am.tenant_identity ALTER COLUMN schema_version SET DEFAULT 4;
UPDATE am.tenant_identity SET schema_version=4 WHERE singleton;
