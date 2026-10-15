-- Publication state is separate from immutable source facts. The delivery role
-- may update this table but cannot update/delete the outbox or access policy.
CREATE TABLE am.outbox_delivery (
    event_id uuid NOT NULL REFERENCES am.outbox,
    destination text NOT NULL CHECK (destination IN ('authorization','sessions','audit','unroutable')),
    status text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','InFlight','Delivered','DeadLetter')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    cycle_attempts integer NOT NULL DEFAULT 0 CHECK (cycle_attempts >= 0),
    redrives integer NOT NULL DEFAULT 0 CHECK (redrives >= 0),
    next_attempt_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    lease_token uuid,
    lease_until timestamptz,
    last_error_code text,
    delivered_at timestamptz,
    receipt jsonb,
    PRIMARY KEY (event_id,destination),
    CHECK (cycle_attempts <= attempts),
    CHECK ((lease_token IS NULL) = (lease_until IS NULL)),
    CHECK ((delivered_at IS NULL) = (receipt IS NULL)),
    CHECK ((status='InFlight') = (lease_token IS NOT NULL AND lease_until IS NOT NULL)),
    CHECK ((status='Delivered') = (delivered_at IS NOT NULL AND receipt IS NOT NULL))
);
CREATE INDEX outbox_delivery_due ON am.outbox_delivery (next_attempt_at,event_id,destination)
    WHERE status IN ('Pending','InFlight');
CREATE TABLE am.outbox_redrives (
    operation_id uuid PRIMARY KEY,
    event_id uuid NOT NULL,
    destination text NOT NULL,
    expected_attempts integer NOT NULL,
    operator_reference text NOT NULL CHECK (length(operator_reference) BETWEEN 1 AND 128),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (event_id,destination) REFERENCES am.outbox_delivery
);
ALTER TABLE am.tenant_identity ALTER COLUMN schema_version SET DEFAULT 3;
UPDATE am.tenant_identity SET schema_version=3 WHERE singleton;
