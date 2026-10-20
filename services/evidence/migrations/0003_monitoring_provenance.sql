-- Preserve historical scan evidence; legacy rows are explicitly unrecorded.
ALTER TABLE evidence.document_versions ADD COLUMN tenant_id text NULL,
 ADD COLUMN malware_check jsonb NULL,
 ADD COLUMN intake_scan_verified boolean NOT NULL DEFAULT false;
CREATE INDEX ix_evidence_monitor_tenant ON evidence.document_versions(tenant_id,created_at DESC,document_version_id COLLATE "C" DESC);
