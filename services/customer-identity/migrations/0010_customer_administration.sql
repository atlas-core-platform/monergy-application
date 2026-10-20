-- Availability is separate from immutable verified ownership. Administrator
-- authority never grants ownership, a business role, Consent or financial data.
CREATE TABLE customer_identity.customer_availability (
 customer_id text PRIMARY KEY REFERENCES customer_identity.customers(customer_id),
 active boolean NOT NULL,
 version bigint NOT NULL CHECK(version>0 AND version<9007199254740000)
);
CREATE TABLE customer_identity.customer_status_changes (
 operation_id uuid PRIMARY KEY,
 customer_id text NOT NULL REFERENCES customer_identity.customers(customer_id),
 actor_id text NOT NULL REFERENCES customer_identity.tenant_principals(actor_id),
 customer_version bigint NOT NULL CHECK(customer_version>1),
 payload jsonb NOT NULL,
 recorded_at timestamptz NOT NULL,
 UNIQUE(customer_id,customer_version)
);
CREATE INDEX customer_status_changes_history ON customer_identity.customer_status_changes(customer_id,customer_version DESC);
CREATE VIEW customer_identity.current_customers AS
 SELECT c.customer_id,c.display_name,c.secondary_label,c.owner_actor_id,c.ownership_evidence,
 c.active AND coalesce(a.active,true) AS active,
 c.version+coalesce(a.version,0) AS version
 FROM customer_identity.customers c LEFT JOIN customer_identity.customer_availability a ON a.customer_id=c.customer_id;
REVOKE ALL ON customer_identity.customer_availability,customer_identity.customer_status_changes FROM PUBLIC;
