-- P2-01: customer-reviewed bank-account metadata bound to one exact canonical fact revision.
CREATE TABLE financial_profile.bank_account_records (
    tenant_id text NOT NULL,
    customer_id text NOT NULL,
    bank_account_record_id text NOT NULL,
    operation_id text NOT NULL,
    actor_id text NOT NULL,
    fingerprint text NOT NULL,
    source_financial_fact_id text NOT NULL,
    source_revision integer NOT NULL CHECK (source_revision > 0),
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, customer_id, bank_account_record_id),
    UNIQUE (tenant_id, customer_id, actor_id, operation_id),
    FOREIGN KEY (source_financial_fact_id, source_revision)
        REFERENCES financial_profile.fact_revisions(financial_fact_id, revision)
);
CREATE INDEX ix_bank_account_records_customer
    ON financial_profile.bank_account_records
    (tenant_id, customer_id, recorded_at DESC, bank_account_record_id);

CREATE TABLE financial_profile.bank_account_record_revisions (
    tenant_id text NOT NULL,
    customer_id text NOT NULL,
    bank_account_record_id text NOT NULL,
    revision integer NOT NULL CHECK (revision > 1),
    operation_id text NOT NULL,
    actor_id text NOT NULL,
    fingerprint text NOT NULL,
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, customer_id, bank_account_record_id, revision),
    UNIQUE (tenant_id, customer_id, actor_id, operation_id),
    FOREIGN KEY (tenant_id, customer_id, bank_account_record_id)
        REFERENCES financial_profile.bank_account_records(tenant_id, customer_id, bank_account_record_id)
);

GRANT SELECT, INSERT ON financial_profile.bank_account_records,
    financial_profile.bank_account_record_revisions TO monergy_financial_profile_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON financial_profile.bank_account_records,
    financial_profile.bank_account_record_revisions FROM monergy_financial_profile_runtime;
