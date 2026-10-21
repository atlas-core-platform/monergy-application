# T1-106 — Current-owner indexing freshness (P1-10)

Date: 2026-10-21. P1-10 remains Partial. This increment closes stale projection reuse in the existing LOCAL/UAT search, retrieval-context and explicit index-refresh contracts. It does not claim a durable index, complete document corpus, background indexing or semantic search.

## Behavior and ownership

Product projections are returned as immutable request-local snapshots, never retained in the singleton projection source or shared customer index. Each search reads its relevant owner family. Retrieval-context and explicit refresh read both Evidence and Financial Profile, including financial provenance, under the current tenant/session and existing owner checks. Both source families must succeed; unavailable, denied or malformed owner responses cannot fall back to an earlier projection or yield a partial combined snapshot. A missing selected record rejects the entire retrieval selection. A selected stable financial ID resolves its current owner data, not a promised historical version.

The application checks customer/authorization scope, cancellation and authorization policy after the source read. Each call builds an isolated derived index, so concurrent requests cannot overwrite or mix projections. Explicit refresh returns the count and deterministic fingerprint of that request's owner snapshot; it is not a background rebuild or persistence receipt. CID-046 now passes through the same current tenant search capability boundary as CID-042/043/044. Owner read permissions remain separately required. Reference simulator behavior stays separate and unchanged.

Current Evidence reads retain the existing 100-document bound. Financial source categories remain Income, Expense, Loan, Asset, Investment, Insurance and Tax. No model/provider, new capability, migration, package or UI contract is introduced. Retention policy and durable indexing remain open; no cached customer projection is retained by this product adapter.

## Verification

41 Search & Retrieval checks and26 TenantBoundary checks pass (67 backend total). New cases cover removed selection, current replacement source, empty refresh/fingerprint change, owner failure without fallback, forged customer/authorization, concurrent snapshots, cancellation, authorization loss, combined-source failure and CID-046 allow/deny. .NET build/analyzer checks pass with the test builds. No UI code changed. Physical persistence, live service-to-service UAT and full Windows/Docker UAT were not run. No development GitHub Actions.

## Tracker

Repository JSON status is reconciled with workbook version13 through T1-105 before this increment; it had still described T1-97. Original29 remains14 Complete,15 Partial,0 Pending. T1-106=105 implementation units+1 governance closure after paired merges. P1-10 retains durable indexing, complete corpus/rebuild/replacement coverage and live verification gaps. Broader privacy/retention and model decisions are unchanged. acceptedThrough T1-06 and approvedForExpertIngestion false remain unchanged. No user input is required for this increment.
