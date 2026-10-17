# T1-41 — Full current-source Expert compilation

Functional Application baseline: `363e8ddbf0b095a1be4dc2f17c3999cc6ebb7d97` (PR #90). Architecture: `90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Both source checkouts were verified clean with exact Git commit/tree/file identities.

The .NET Roslyn extractor builds with 0 warnings/errors. Full Roslyn, TypeScript, SQL, configuration, architecture, evidence and contract extraction executed successfully. The current graph contains 6,298 entities and 17,532 relationships. The model validator passes. Doctor reports HEALTHY structural state, VALID source locks, 0 error conflicts, 3 retained governance warnings (CID-068/069/070) and 10 unresolved knowledge states. These are structural/source results, not live business acceptance.

Fresh generated artifacts are published under Expert `generated/current-source/`, separately from the historical accepted snapshot. They retain exact source locks and a canonical content digest. Gzip storage preserves canonical logical JSON and historical artifacts; partial/corrupt current snapshots fail closed. The final manifest identifies the documented source revision, including this documentation-only receipt; it does not rewrite historical extraction as new evidence.

The paired Expert change records full compilation on T1-21–41 without advancing acceptance. Focused unit/native tests and the canonical source/digest checks are recorded in that companion PR. No development GitHub Actions.

## Remaining execution boundary

Live owner-backed PostgreSQL UAT remains NOT RUN. Docker/PostgreSQL executables are absent from this workspace. Existing source lint findings and governance warnings remain visible. Remaining provider, financial-methodology, professional-workflow and production decisions retain their approved gates. The full product is not declared complete or production-ready.
