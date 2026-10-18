-- T1-55. Self-declared profile date, separate from KYC/ownership authority.
-- Existing rows remain unknown (NULL); no birth date is inferred or seeded.
ALTER TABLE customer_identity.customer_profiles
    ADD COLUMN date_of_birth date,
    ADD CONSTRAINT customer_profiles_date_of_birth_check CHECK (
        date_of_birth IS NULL OR (date_of_birth BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'
            AND date_of_birth <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date)
    );
