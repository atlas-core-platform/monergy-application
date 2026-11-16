-- P3-10 LOCAL/UAT reference personas. Keep the generated C&I actor namespace
-- and the previously accepted reference identities while admitting only the
-- bounded persona actors seeded by the current LOCAL onboarding contract.
ALTER TABLE customer_identity.tenant_principals
    DROP CONSTRAINT tenant_principals_actor_id_check;

ALTER TABLE customer_identity.tenant_principals
    ADD CONSTRAINT tenant_principals_actor_id_check CHECK (
        actor_id ~ '^ci-[0-9a-f]{32}$'
        OR actor_id IN (
            'A100','A200','A300','A400','A500','A600',
            'A700','A800','A850','A900','A901'
        )
    );
