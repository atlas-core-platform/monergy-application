-- LOCAL/UAT owner model. Only owner bootstrap may establish verified ownership;
-- display-directory configuration and administrator access cannot create it.
ALTER TABLE customer_identity.tenant_principals DROP CONSTRAINT tenant_principals_actor_id_check;
ALTER TABLE customer_identity.tenant_principals ADD CONSTRAINT tenant_principals_actor_id_check
    CHECK (actor_id ~ '^ci-[0-9a-f]{32}$' OR actor_id IN ('A100','A200','A300','A900','A901'));
CREATE TABLE customer_identity.customers (
    customer_id text PRIMARY KEY CHECK (length(customer_id) BETWEEN 1 AND 128),
    display_name text NOT NULL CHECK (length(btrim(display_name)) BETWEEN 1 AND 120),
    secondary_label text CHECK (length(secondary_label) BETWEEN 1 AND 120),
    owner_actor_id text REFERENCES customer_identity.tenant_principals(actor_id),
    ownership_evidence text,
    active boolean NOT NULL DEFAULT true,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    CHECK ((owner_actor_id IS NULL AND ownership_evidence IS NULL) OR
        (owner_actor_id IS NOT NULL AND length(ownership_evidence) BETWEEN 1 AND 300))
);
