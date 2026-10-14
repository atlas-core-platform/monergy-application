# APP-02 — Secure evidence library and provenance view

Status: implementation candidate. Production accepted: **no**.

Authority: APP-01 accepted LOCAL/UAT customer workspace and evidence intake, followed by the product-owner direction to proceed with the next Monergy implementation on 2026-10-14.

## Product outcome

APP-02 makes confirmed customer evidence discoverable beyond the upload interaction while keeping the Evidence service authoritative:

1. The protected Evidence page requests the current customer library only when `evidence.document.read` is present.
2. The gateway revalidates the session, resolves the customer from the authenticated actor and obtains a current customer-scoped decision.
3. The Evidence boundary independently re-evaluates the same capability through the existing tenant-boundary middleware.
4. The Evidence repository returns at most 100 versions for that customer, newest first.
5. The UI supports file-name/version search, type filtering, refresh, loading, error and empty states.
6. A provenance drawer exposes owner-issued document, version and evidence identities plus the SHA-256 source hash.
7. Raw content, content references and workload credentials are never returned to the browser.
8. A successful upload refreshes the owner-confirmed library without treating browser state as evidence authority.

## Boundary decisions

| Concern               | Decision                                                                                                                                                                                                           |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Authorization         | Reuse the governed `evidence.document.read` capability. No new role, permission or Consent contract is introduced.                                                                                                 |
| Customer context      | Customer identity is resolved server-side. No customer selector or customer identifier is accepted from the browser.                                                                                               |
| Evidence authority    | Listing is implemented by the Evidence application and repository, not a gateway or browser receipt cache.                                                                                                         |
| Response minimization | Metadata and provenance identifiers are returned; content and storage references remain owner-local.                                                                                                               |
| Bounded query         | LOCAL/UAT returns at most 100 newest versions. Pagination and Production-scale query design remain separate.                                                                                                       |
| Data durability       | The onboarding profile continues to use the in-memory reference adapter and is labeled `Runtime only`. The physical Evidence repository implements the same bounded read contract without changing D11 acceptance. |

## Explicit exclusions

- No Production deployment, identity, storage, retention, backup, HA/DR or release acceptance.
- No document download, preview, deletion, replacement, manual metadata editing or sharing.
- No extraction, OCR, classification, AI, financial-profile mutation, processing status or advice.
- No advisor customer selector and no expansion of Access Management contracts.
- No claim that runtime evidence survives Evidence-service recreation.

## Acceptance

The candidate must pass:

- customer-web formatting, typecheck, lint, component tests, production build and browser regressions;
- Evidence application and repository tests for customer scoping, bounded input and minimized output;
- product direct-route and reload isolation;
- customer, Tenant Administrator and cross-customer library behavior in Docker UAT;
- upload-to-library refresh, filtering, provenance drawer, compact viewport and reduced-motion verification;
- impact-aware continuous verification, Tenant Access owner integration and Local Docker UAT acceptance on the published candidate.

Passing LOCAL/UAT checks accepts only this bounded candidate. Production release remains separately governed.
