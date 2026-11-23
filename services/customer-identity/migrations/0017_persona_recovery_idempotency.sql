-- Recovery confirmations can commit before the caller receives the response.
-- Retain only an opaque operation identity and an owner-keyed payload fingerprint
-- so the exact committed operation can be acknowledged without a second mutation.
ALTER TABLE customer_identity.registration_challenges
    ADD COLUMN recovery_operation_id text,
    ADD COLUMN recovery_result_fingerprint text,
    ADD CONSTRAINT registration_challenges_recovery_result_check CHECK (
        (recovery_operation_id IS NULL AND recovery_result_fingerprint IS NULL) OR
        (purpose = 'persona-recovery' AND consumed AND
         recovery_operation_id ~ '^[0-9a-f]{32}$' AND
         recovery_result_fingerprint ~ '^[0-9a-f]{64}$')
    );
