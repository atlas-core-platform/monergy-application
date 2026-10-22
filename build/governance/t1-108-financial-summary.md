# T1-108 — Basic financial summary (P1-25)

Date: 2026-10-21. Completes a basic personal financial summary from selected customer-reviewed balance-sheet and monthly cash-flow results. Financial Rules owns every numeric value. This is implementation completion, not business acceptance or external verification.

## Customer journey

Workspace > Create a basic financial summary opens Q&A in summary mode. The customer selects exact saved net-worth and monthly cash-flow results from paged history. Both must use the same month-end date/currency; monthly household and all members must be Self to match the personal net-worth scope. There is no implicit latest-result selection, FX, proration or inference of comparability. Incomplete coverage returns its owner gaps without a model request or invented totals.

For complete comparable reviews, six canonical claims explain assets, liabilities, net worth, monthly income, expenses and income-minus-expenses. The model may organize all six IDs or return insufficient data. It cannot omit one side, calculate, add prose, invent facts or give recommendations. The fixed introduction distinguishes monthly balance from a bank balance or net-worth change. Exact calculation links preserve owner review/source/version/provenance details. These are explicitly customer-reviewed declarations, not verified financial truth.

The existing per-request provider notice and permission remain required. Prompt version3 changes the configuration revision, requiring renewed permission. Summary uses one model call; replay does not repeat spend. The existing encrypted attempt/history store reserves before spend; interrupted/rejected attempts remain NOT_COMPLETED. History revalidates source claims under current owner authority. The original financial-source, document-quotation and net-worth Q&A journeys remain supported.

## Boundaries and configuration

AI Intelligence coordinates owner reads and validation; Integration Gateway alone performs provider I/O. Financial Profile and Financial Rules retain inputs and arithmetic. The response is revalidated after generation, including exact position/results, capability/customer/session changes and provider revision. Only canonical claim IDs/descriptions are sent to the model; document originals and internal source IDs are not sent for summary. Frontend confirms the selected calculation IDs and all six citations before display. The owner response limit now accommodates bounded complete inventories.

Existing onboarding configuration supplies provider endpoint, model, API key, label and published notice; no new credential or manual setup is introduced. No database/schema/package/capability change. Existing sensitive product-ingress Audit applies. Live provider quality/OD-11 approval, production retention/key custody, physical PostgreSQL and full authenticated UAT remain separate and open.

## Verification

151 Integration Gateway/AI backend checks and12 UI checks pass. TypeScript, changed source/test ESLint and Vite production build pass. Tests include all-six grounding, omission/invention rejection, incomplete coverage with zero calls, single-call/replay economics, exact comparable owner sources, wrong date/currency/household/member/customer/input, current access and history revocation, provider payload minimization, explicit selection/permission, citation substitution and pagination. No development GitHub Actions.

## Tracker

P1-25 Complete for the supported basic personal summary. P1-24 remains Partial for general relevance/grounding and model approval. After paired merges:17 Complete,12 Partial,0 Pending;T1-108=107 implementation units+1 governance closure. Original IDs/phases, acceptance through T1-06 and approvedForExpertIngestion=false retained.
