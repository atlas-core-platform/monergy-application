-- Additive platform-only recovery. No reviewer grant, tenant migration or Default Access Pack change.
ALTER TABLE am_platform.audit_delivery ADD COLUMN recovery_count integer NOT NULL DEFAULT 0
    CHECK (recovery_count BETWEEN 0 AND 3);
ALTER TABLE am_platform.policy DROP CONSTRAINT policy_schema_version_check;
UPDATE am_platform.policy SET schema_version=2 WHERE singleton;
ALTER TABLE am_platform.policy ADD CONSTRAINT policy_schema_version_check CHECK (schema_version=2);
