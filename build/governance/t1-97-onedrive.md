# T1-97 — OneDrive integration (P1-12)

## Delivered journey

Connections > OneDrive provides configuration status, explicit Microsoft read/offline permission, confidential OAuth with PKCE, account confirmation, root/nested folder browsing, explicit selected-folder permission, bounded file pages, exact-version import, refresh/freshness, retained import history and confirmed local disconnect. The existing Google Drive journey remains separately accessible. Supported PDF, PNG, JPEG, text, CSV and XLSX originals up to 1 MiB go through Evidence's malware scan, independent SHA-256 and document/version registration. Financial information changes only through the existing customer review flow. Original provider files are never edited or deleted.

Microsoft Graph v1.0 uses delegated Files.Read plus offline_access on the selected account's default personal/business drive in the global cloud. Remote/shared items, packages/OneNote, unsupported formats, recursive import and other drives/sites are excluded. Folder browsing supports nested folders and bounded next pages; import is limited to direct children of the explicitly bound folder. HTTP endpoints and page scope are fixed/validated. eTag-derived version identity is checked before and after download and again after scanning, with current ownership, access, consent and configuration. Downloads have size limits; Microsoft preauthenticated redirects are allowed only to HTTPS Microsoft storage host families, with no forwarded bearer token and no second redirect. Unrecognized download hosts fail closed. Sensitive outbound requests and incoming callbacks are excluded from tracing/request-start diagnostics.

## Owner boundaries and persistence

Integration Gateway owns OneDrive credentials, server-held tokens, account/folder binding and import receipts. It reuses current Customer & Identity ownership/setup, Access Management connection/upload capabilities and separate Consent document-import permission. Consent migration0003 extends the existing provider constraint, retaining Google rows. Every provider-consent read and mutation is provider-bound, with existing revision/expiry/ownership checks, tenant transaction locking, narrow runtime grants and append-only evidence. One provider cannot read or revoke another provider's connection.

OneDrive has a separate AES-GCM encrypted LOCAL/CI store and per-profile key, with exclusive file lease, process serialization and atomic replacement. Ten-minute single-use OAuth state and PKCE verifier bind the initiating tenant/customer/actor/session/owner/configuration; callback consumption is checkpointed before exchange. Tokens never reach browser storage. Stable import intents are checkpointed before Evidence registration, preserving exact connection/file/version/document/version/hash replay. Connection metadata is durable locally; imported Evidence bytes remain temporary LOCAL data. Production distributed persistence and key custody are separate.

Disconnect checkpoints token erasure before owner/provider cleanup and always prevents further imports, retaining previously imported documents/history. Microsoft has no per-token Google-style revoke endpoint. The adapter does not request directory permissions or revoke unrelated account sessions. UI therefore explicitly shows Microsoft revocation unconfirmed and directs the customer to remove the app's permission in Microsoft account settings. Monergy permission is revoked independently when Consent is reachable.

## Operator configuration

External dependencies remain optional for organization activation. Supply `-OneDriveConfigurationFile <protected-json>` with the existing named onboarding profile to `build/uat/Invoke-MonergyUat.ps1 -Action Prepare` or `Start`, alongside `-EnableOnboarding` where appropriate. Re-supply it when regenerating configuration. JSON is keyed by T001/T002 with ClientId, ClientSecret and RedirectUri. Register a Microsoft confidential Web application supporting the intended personal/work/school accounts, delegated Files.Read, offline access and exact callback `http://localhost:4173/connections-api/local/v1/onedrive/callback` for the default port. Organization tenant policy may require administrator approval. Use the actual configured port. No credentials are included in source.

Generated configuration is delivered only to Integration Gateway under existing protected profile permissions. The retained drive-data volume has a distinct onedrive-connections directory and dedicated oneDriveProtection key. Preserve volume and key together. Access Pack 2.5 and its canonical pin are unchanged; existing installations use the established reviewed upgrade flow. No browser credential administration is claimed.

## Verification and limits

104 Integration Gateway tests (43 added OneDrive cases and 61 retained cases) and 322 nonphysical Access Integration tests pass: 426 backend checks. All35 focused UI checks pass (8 OneDrive,6 Google Drive,15 Consent,6 onboarding). TypeScript, changed-file ESLint, Vite and Integration Gateway, Consent and Gateway builds pass. New tests cover PKCE, scope/refresh rotation, hostile redirect/page destinations, nested folders, version identity, token isolation, rate limits, explicit permission, context changes and disconnect. Existing journey cases exercise owner/tenant/consent/configuration races, exact import replay, encrypted restart/tamper and denied/expired callbacks.

Live Microsoft authorization/API and tenant approval, physical PostgreSQL migration/grants/restart/concurrency, PowerShell/Docker/ClamAV and full authenticated UAT were NOT RUN. Migrator compilation remains unverified because dbup-postgresql is absent from the offline dependency cache, as recorded at T1-96. Unchanged Evidence/native OCR and broader UI results are carried forward, not counted as fresh tests. No new package, service, hosted development Actions or deployment. Source completion does not advance production or business acceptance.

After paired Application/Expert merges: 97 numbered units =96 implementations +1 governance closure. Original29:8 Complete,19 Partial,2 Pending,21 incomplete. Remaining pending journeys: P1-04 Basic KYC and P1-24 Basic Financial Q&A. Original requirement IDs, source phases and acceptance through T1-06 remain unchanged.

## Provider specifications reviewed

- https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow
- https://learn.microsoft.com/en-us/graph/api/drive-get?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/driveitem-list-children?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0

Specifications establish the adapter contract, not live-provider verification.
