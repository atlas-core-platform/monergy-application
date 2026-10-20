-- Append-only operational receipts stay with the authoritative job and retain
-- no customer authentication/workload credentials or document payloads.
CREATE TABLE job_management.administrator_operations (
 operation_id uuid PRIMARY KEY,
 job_id text NOT NULL REFERENCES job_management.jobs(job_id),
 recorded_at timestamptz NOT NULL,
 payload jsonb NOT NULL
);
CREATE INDEX administrator_operations_history ON job_management.administrator_operations(job_id,recorded_at DESC,operation_id DESC);
CREATE INDEX job_administrator_tenant_queue ON job_management.jobs((security_context->'workload'->>'workloadIdentityId'),requested_at DESC,job_id COLLATE "C" DESC);
GRANT SELECT,INSERT ON job_management.administrator_operations TO monergy_job_management_runtime;
REVOKE UPDATE,DELETE,TRUNCATE ON job_management.administrator_operations FROM monergy_job_management_runtime;
