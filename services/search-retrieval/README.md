# Search & Retrieval Service

Search & Retrieval owns only derived, rebuildable search representations. Evidence and Financial Profile remain authoritative for the customer information surfaced by search.

## Governed execution profiles

The historical MWP-03-D07 reference adapter remains available only at its recorded `SIMULATOR` evidence level in `LOCAL` and `CI_EPHEMERAL`. Its static document, financial and calculation fixtures and deterministic semantic representation remain engineering evidence; they are not live customer information or a production search-quality claim.

T1-11 adds a separate LOCAL/UAT product projection source. When `Monergy:ProductSearch:Enabled=true`, each customer query is freshly authorized and the derived customer index is rebuilt from:

- the current Evidence customer document library for Document results; and
- the current Financial Profile for supported `INCOME` and `EXPENSE` Financial results, with exact Financial provenance.

The product profile supports **lexical matching only**. Semantic matching is rejected at the Search owner boundary. Calculation results, AI/embedding behavior, provider selection, physical search/vector infrastructure and cross-customer retrieval are not part of T1-11.

Every product result remains `DERIVED_REBUILDABLE` and carries the authoritative source object/reference needed to navigate back to the exact Document version or Financial fact. This LOCAL/UAT integration is not Production acceptance.

## T1-106 current-owner snapshots

Product search, retrieval-context and explicit refresh use isolated request-local indexes. Retrieval and refresh read both current Evidence and Financial Profile owners, including provenance; both must succeed. No singleton customer projection or stale fallback remains. CID-046 requires current tenant search authority; source reads retain their separate owner permissions. Reference simulator behavior is unchanged. The refresh count/fingerprint describes the request snapshot, not a durable background rebuild. Existing 100-document scope remains; full corpus, durable indexing and broader lifecycle coverage keep P1-10 Partial. See the T1-106 governance receipt for tests and live-verification limits.
