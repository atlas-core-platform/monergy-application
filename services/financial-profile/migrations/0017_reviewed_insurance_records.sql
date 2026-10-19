-- A source-linked, customer-reviewed record; amounts are not derived from arbitrary source figures.
CREATE TABLE financial_profile.insurance_records (
    tenant_id text NOT NULL,
    customer_id text NOT NULL,
    insurance_record_id text NOT NULL,
    operation_id text NOT NULL,
    actor_id text NOT NULL,
    fingerprint text NOT NULL,
    source_financial_fact_id text NOT NULL,
    source_revision integer NOT NULL,
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, customer_id, insurance_record_id),
    UNIQUE (tenant_id, customer_id, actor_id, operation_id),
    FOREIGN KEY (source_financial_fact_id, source_revision)
        REFERENCES financial_profile.fact_revisions(financial_fact_id, revision),
    CHECK (source_revision > 0)
);
CREATE INDEX ix_insurance_records_customer ON financial_profile.insurance_records (tenant_id, customer_id, recorded_at DESC, insurance_record_id);
GRANT SELECT, INSERT ON financial_profile.insurance_records TO monergy_financial_profile_runtime;
REVOKE UPDATE, DELETE ON financial_profile.insurance_records FROM monergy_financial_profile_runtime;
