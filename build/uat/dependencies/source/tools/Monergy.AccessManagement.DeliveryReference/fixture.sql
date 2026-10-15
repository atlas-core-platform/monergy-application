-- Disposable verification receiver. This is not an Audit or C&I migration.
CREATE SCHEMA delivery_reference;
CREATE TABLE delivery_reference.identity (
    singleton boolean PRIMARY KEY CHECK (singleton),
    kind text NOT NULL CHECK (kind='AM04_DELIVERY_FIXTURE')
);
INSERT INTO delivery_reference.identity VALUES (true,'AM04_DELIVERY_FIXTURE');
CREATE TABLE delivery_reference.inbox (
    destination text NOT NULL,
    tenant_id text NOT NULL,
    event_id uuid NOT NULL,
    event_hash text NOT NULL,
    receipt jsonb NOT NULL,
    PRIMARY KEY (destination,tenant_id,event_id)
);
CREATE TABLE delivery_reference.policy_versions (tenant_id text PRIMARY KEY, version bigint NOT NULL);
CREATE TABLE delivery_reference.subject_versions (
    tenant_id text NOT NULL,actor_id text NOT NULL,version bigint NOT NULL,PRIMARY KEY(tenant_id,actor_id)
);
CREATE TABLE delivery_reference.session_versions (
    tenant_id text NOT NULL,actor_id text NOT NULL,version bigint NOT NULL,PRIMARY KEY(tenant_id,actor_id)
);
CREATE TABLE delivery_reference.sessions (
    tenant_id text NOT NULL,authentication_context_id text NOT NULL,actor_id text NOT NULL,
    subject_version bigint NOT NULL,revoked boolean NOT NULL DEFAULT false,
    PRIMARY KEY (tenant_id,authentication_context_id)
);
CREATE INDEX reference_session_actor ON delivery_reference.sessions(tenant_id,actor_id);
CREATE TABLE delivery_reference.audit_observations (
    tenant_id text NOT NULL,event_id uuid NOT NULL,event_hash text NOT NULL,envelope jsonb NOT NULL,
    PRIMARY KEY (tenant_id,event_id)
);
