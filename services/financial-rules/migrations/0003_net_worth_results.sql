-- Immutable Financial Rules calculation results; monetary strings preserve exact cents on every wire.
CREATE TABLE financial_rules.net_worth_results (
 tenant_id text NOT NULL,customer_id text NOT NULL,calculation_id text NOT NULL,
 actor_id text NOT NULL,operation_id text NOT NULL,fingerprint text NOT NULL,
 payload jsonb NOT NULL,recorded_at timestamptz NOT NULL,
 PRIMARY KEY(tenant_id,customer_id,calculation_id),UNIQUE(tenant_id,customer_id,actor_id,operation_id)
);
CREATE INDEX ix_net_worth_results_customer ON financial_rules.net_worth_results(tenant_id,customer_id,recorded_at DESC,calculation_id DESC);
GRANT SELECT,INSERT ON financial_rules.net_worth_results TO monergy_financial_rules_runtime;
REVOKE UPDATE,DELETE ON financial_rules.net_worth_results FROM monergy_financial_rules_runtime;
