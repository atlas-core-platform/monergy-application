-- Separate provider permission from delegated customer-advice access.
CREATE TABLE consent.provider_grants (
    connection_id text PRIMARY KEY,
    customer_id text NOT NULL,
    owner_actor_id text NOT NULL,
    owner_version bigint NOT NULL CHECK(owner_version>0),
    provider text NOT NULL CHECK(provider='google-drive'),
    purpose text NOT NULL CHECK(purpose='document-import'),
    folder_id text,
    version bigint NOT NULL CHECK(version>0),
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    granted_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE consent.provider_evidence (
    evidence_id text PRIMARY KEY,
    connection_id text NOT NULL REFERENCES consent.provider_grants(connection_id),
    customer_id text NOT NULL,
    actor_id text NOT NULL,
    operation text NOT NULL CHECK(operation IN ('Granted','FolderBound','Revoked')),
    version bigint NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(connection_id,version)
);
