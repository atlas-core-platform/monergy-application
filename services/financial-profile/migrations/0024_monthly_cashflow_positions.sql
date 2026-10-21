-- Immutable customer-reviewed inventories; monetary strings preserve exact cents on every wire.
CREATE TABLE financial_profile.monthly_cashflow_positions (
 tenant_id text NOT NULL,customer_id text NOT NULL,position_id text NOT NULL,
 actor_id text NOT NULL,operation_id text NOT NULL,fingerprint text NOT NULL,
 payload jsonb NOT NULL,recorded_at timestamptz NOT NULL,
 PRIMARY KEY(tenant_id,customer_id,position_id),UNIQUE(tenant_id,customer_id,actor_id,operation_id)
);
CREATE INDEX ix_monthly_cashflow_positions_customer ON financial_profile.monthly_cashflow_positions(tenant_id,customer_id,recorded_at DESC,position_id DESC);
GRANT SELECT,INSERT ON financial_profile.monthly_cashflow_positions TO monergy_financial_profile_runtime;
REVOKE UPDATE,DELETE ON financial_profile.monthly_cashflow_positions FROM monergy_financial_profile_runtime;
