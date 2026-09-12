# GitHub Free Governance Exception

## Status

Accepted for MWP-03-D01 only. Branch protection is
`NOT_IMPLEMENTED — GITHUB FREE PLAN LIMITATION`; it is never represented as
`PASS`.

The repository remains private. Organization base access is read-only, and the
repository has no directly assigned collaborators or teams. Write and admin
access must remain limited to the minimum organization owners required to
administer the repository.

## Compensating controls

- All ordinary changes to `main` are required by policy to use a pull request.
- `Verify technology-neutral baseline` runs on every pull request and every push
  to `main`.
- `CODEOWNERS` names `@Magkhan060` as the advisory repository owner. GitHub Free
  cannot enforce that approval on this private repository.
- The hosted workflow queries the commit-to-pull-request association for every
  push to `main`. A direct push creates a visible workflow warning and job
  summary entry. Detection cannot prevent the push.
- Force pushes and deletion of `main` are prohibited by policy even though the
  current plan cannot enforce the prohibition.
- The reviewed initial publication and the GitHub Free exception realization
  are the only approved direct-push bootstrap activity.

## Immediate expiry triggers

This exception expires immediately when any one of these conditions occurs:

1. A second write-capable contributor joins.
2. External contractors or client developers receive write access.
3. SG-02 Integration becomes `READY`.
4. UAT or Production preparation begins.
5. Production-bound artifacts or deployment credentials are introduced.
6. Governance or contractual requirements demand enforced reviews.

At expiry, upgrade `atlas-core-platform` to GitHub Team or higher, or migrate to
a platform that provides equivalent protected-branch enforcement, before
continuing affected work.

## Residual risk

The controls detect and govern a direct push but cannot technically prevent one.
Advisory CODEOWNERS cannot enforce approval. The CTO accepts this residual risk
only within the scope and expiry conditions above.
