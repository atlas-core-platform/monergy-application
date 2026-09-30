ALTER TABLE job_management.jobs
    ADD COLUMN retry_count integer NOT NULL DEFAULT 0 CHECK (retry_count >= 0),
    ADD COLUMN lease_owner text NULL,
    ADD COLUMN lease_token text NULL,
    ADD COLUMN lease_expires_at timestamptz NULL,
    ADD COLUMN reconciliation_required boolean NOT NULL DEFAULT false;

CREATE INDEX jobs_runnable_idx
    ON job_management.jobs (COALESCE(next_attempt_at, scheduled_at), job_id)
    WHERE reconciliation_required = false
      AND (state = 'Scheduled' OR (state = 'Failed' AND retryable = true));
