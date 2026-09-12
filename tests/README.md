# Verification

D02 adds xUnit architecture/boundary tests, Vitest/React Testing Library
component and accessibility tests, and a Playwright keyboard smoke test for the
toolchain shell. Contract, integration, and end-to-end directories still reserve
the D07 evidence layers; their future Feature tests remain `NOT_RUN`.

Tests must use public service contracts and must not bypass service boundaries
through cross-service persistence access.
