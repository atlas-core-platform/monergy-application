-- T1-53. Immutable legal publication snapshots and explicit customer acceptance.
-- This does not activate an account or grant access to another actor/provider.
CREATE TABLE customer_identity.account_agreement_publications (
    revision text PRIMARY KEY CHECK (revision ~ '^[0-9a-f]{64}$'),
    content_json text NOT NULL,
    published_at timestamptz NOT NULL
);
CREATE TABLE customer_identity.account_agreement_acceptances (
    operation_id uuid PRIMARY KEY,
    customer_id text NOT NULL REFERENCES customer_identity.customers(customer_id),
    actor_id text NOT NULL REFERENCES customer_identity.tenant_principals(actor_id),
    revision text NOT NULL REFERENCES customer_identity.account_agreement_publications(revision),
    terms_accepted boolean NOT NULL CHECK (terms_accepted),
    privacy_acknowledged boolean NOT NULL CHECK (privacy_acknowledged),
    processing_consented boolean NOT NULL CHECK (processing_consented),
    accepted_at timestamptz NOT NULL,
    UNIQUE (customer_id, actor_id, revision)
);
