-- Minimized LOCAL product-ingress evidence. Existing evidence is never rewritten or given invented human provenance.
CREATE TABLE audit.product_activity (
 event_id text PRIMARY KEY,
 record jsonb NOT NULL,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX ix_product_activity_order ON audit.product_activity(recorded_at DESC,event_id COLLATE "C" DESC);
GRANT SELECT,INSERT ON audit.product_activity TO monergy_audit_runtime;
REVOKE UPDATE,DELETE,TRUNCATE ON audit.product_activity FROM monergy_audit_runtime;
