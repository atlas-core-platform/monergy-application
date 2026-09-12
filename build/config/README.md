# Configuration Boundaries

Only LOCAL and CI/EPHEMERAL examples exist. They contain non-secret settings and
empty secret-reference collections. D01 creates no DEV, QA, UAT, or PRODUCTION
endpoint, credential, secret, identity, or deployment configuration.

Future secret values must be supplied through a governed secret/key capability;
ordinary configuration may hold a reference but never the value.
