-- Retain existing Google grants; each provider remains explicitly bound.
ALTER TABLE consent.provider_grants DROP CONSTRAINT provider_grants_provider_check;
ALTER TABLE consent.provider_grants ADD CONSTRAINT provider_grants_provider_check CHECK(provider IN ('google-drive','onedrive'));
