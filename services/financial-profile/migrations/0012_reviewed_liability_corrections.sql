-- Corrections preserve the creation receipt and every prior reviewed set of terms.
CREATE TABLE financial_profile.liability_record_revisions (
    tenant_id text NOT NULL,
    customer_id text NOT NULL,
    liability_record_id text NOT NULL,
    revision integer NOT NULL CHECK (revision > 1),
    operation_id text NOT NULL,
    actor_id text NOT NULL,
    fingerprint text NOT NULL,
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, customer_id, liability_record_id, revision),
    UNIQUE (tenant_id, customer_id, actor_id, operation_id),
    FOREIGN KEY (tenant_id, customer_id, liability_record_id)
        REFERENCES financial_profile.liability_records(tenant_id, customer_id, liability_record_id)
);
GRANT SELECT, INSERT ON financial_profile.liability_record_revisions TO monergy_financial_profile_runtime;
REVOKE UPDATE, DELETE ON financial_profile.liability_record_revisions FROM monergy_financial_profile_runtime;
