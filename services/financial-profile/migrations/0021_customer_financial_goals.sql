-- Customer-defined objectives remain separate from source-derived financial facts.
CREATE TABLE financial_profile.financial_goals (
    tenant_id text NOT NULL,
    customer_id text NOT NULL,
    financial_goal_id text NOT NULL,
    operation_id text NOT NULL,
    actor_id text NOT NULL,
    fingerprint text NOT NULL,
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, customer_id, financial_goal_id),
    UNIQUE (tenant_id, customer_id, actor_id, operation_id)
);
CREATE INDEX ix_financial_goals_customer ON financial_profile.financial_goals (tenant_id, customer_id, recorded_at DESC, financial_goal_id);
GRANT SELECT, INSERT ON financial_profile.financial_goals TO monergy_financial_profile_runtime;
REVOKE UPDATE, DELETE ON financial_profile.financial_goals FROM monergy_financial_profile_runtime;

-- Corrections preserve the creation receipt and every prior reviewed set of terms.
CREATE TABLE financial_profile.financial_goal_revisions (
    tenant_id text NOT NULL,
    customer_id text NOT NULL,
    financial_goal_id text NOT NULL,
    revision integer NOT NULL CHECK (revision > 1),
    operation_id text NOT NULL,
    actor_id text NOT NULL,
    fingerprint text NOT NULL,
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, customer_id, financial_goal_id, revision),
    UNIQUE (tenant_id, customer_id, actor_id, operation_id),
    FOREIGN KEY (tenant_id, customer_id, financial_goal_id)
        REFERENCES financial_profile.financial_goals(tenant_id, customer_id, financial_goal_id)
);
GRANT SELECT, INSERT ON financial_profile.financial_goal_revisions TO monergy_financial_profile_runtime;
REVOKE UPDATE, DELETE ON financial_profile.financial_goal_revisions FROM monergy_financial_profile_runtime;
