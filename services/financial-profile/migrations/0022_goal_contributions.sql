-- Customer-reported contributions preserve exact target versions without executing payments.
CREATE TABLE financial_profile.goal_contributions (
    tenant_id text NOT NULL,
    customer_id text NOT NULL,
    goal_contribution_id text NOT NULL,
    financial_goal_id text NOT NULL,
    goal_revision integer NOT NULL CHECK(goal_revision>0),
    operation_id text NOT NULL,
    actor_id text NOT NULL,
    fingerprint text NOT NULL,
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, customer_id, goal_contribution_id),
    UNIQUE (tenant_id, customer_id, actor_id, operation_id),
    FOREIGN KEY(tenant_id,customer_id,financial_goal_id) REFERENCES financial_profile.financial_goals(tenant_id,customer_id,financial_goal_id)
);
CREATE INDEX ix_goal_contributions_customer ON financial_profile.goal_contributions (tenant_id, customer_id,financial_goal_id, recorded_at DESC, goal_contribution_id);
GRANT SELECT, INSERT ON financial_profile.goal_contributions TO monergy_financial_profile_runtime;
REVOKE UPDATE, DELETE ON financial_profile.goal_contributions FROM monergy_financial_profile_runtime;

-- Corrections preserve the creation receipt and every prior reviewed set of terms.
CREATE TABLE financial_profile.goal_contribution_revisions (
    tenant_id text NOT NULL,
    customer_id text NOT NULL,
    goal_contribution_id text NOT NULL,
    revision integer NOT NULL CHECK (revision > 1),
    operation_id text NOT NULL,
    actor_id text NOT NULL,
    fingerprint text NOT NULL,
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, customer_id, goal_contribution_id, revision),
    UNIQUE (tenant_id, customer_id, actor_id, operation_id),
    FOREIGN KEY (tenant_id, customer_id, goal_contribution_id)
        REFERENCES financial_profile.goal_contributions(tenant_id, customer_id, goal_contribution_id)
);
GRANT SELECT, INSERT ON financial_profile.goal_contribution_revisions TO monergy_financial_profile_runtime;
REVOKE UPDATE, DELETE ON financial_profile.goal_contribution_revisions FROM monergy_financial_profile_runtime;
