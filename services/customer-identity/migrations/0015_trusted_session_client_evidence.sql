-- MON-ID-006. Persist only bounded, browser-reported client classifications.
-- Raw user agents, IP addresses, location and device fingerprints are excluded.
ALTER TABLE customer_identity.trusted_sessions
    ADD COLUMN device_class text NOT NULL DEFAULT 'Unknown'
        CHECK (device_class IN ('Desktop','Tablet','Mobile','Unknown')),
    ADD COLUMN client_family text NOT NULL DEFAULT 'Unknown'
        CHECK (client_family IN ('Chrome','Edge','Firefox','Safari','Other','Unknown'));
