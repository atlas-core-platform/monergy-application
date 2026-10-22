# T1-114 — Validated document intake and original Phase1 scope

Date: 2026-10-22. P1-05 Upload, P1-07 Classification and P1-15 Assets are implementation Complete. Original phase assignments and acceptance are unchanged.

## File validation

XLSX intake previously checked only the ZIP signature. A renamed ZIP could therefore pass the content-type check. Intake now verifies a bounded package,unique safe part names,all compressed members,workbook content type,root/workbook relationships and every declared worksheet. Missing or malformed parts,duplicate entries,traversal names,macros,XML entities and oversized expanded data reject before scanning/storage. No formulas execute and no external resource is fetched.

This is independent of financial extraction. A valid non-template or multi-sheet workbook can be uploaded and retained; it is not falsely presented as a supported financial extraction. Limits remain one MiB original,512 package entries,20 MiB expanded total,10 MiB per member,one MiB per interpreted XML part and50 sheets. PDF/PNG/JPEG/text/CSV support and scan/hash/owner/source gates remain. Legacy XLS is not claimed.

## Requirement and phase reconciliation

P1-05 delivers the upload portion of MON-DOC-002/003 and MON-SEC-008 through scanned original-byte intake,source identity,version metadata and configured PostgreSQL/S3 retention. Extraction and indexing remain separate journeys; their gaps do not erase implemented upload functionality.

P1-07 satisfies MON-DOC-004/005 through independent required-family detection and explicit OTHER/UNCLASSIFIED review. T1-111 added permission-controlled,version-checked review/history; T1-112 fixed the asynchronous tenant path. Unrecognized layouts are explicitly reviewable. Native quality/live UAT and production capability enablement remain separate verification/approval, consistent with other completed LOCAL/UAT journeys.

P1-15 satisfies the Phase1 normalized asset/position requirement through T1-90/102: Cash,Bank balances,Investment,Property,Vehicle and Other assets; dated/currency-bound entries,source notes and exact imported revisions,coverage review,Financial Rules totals,history and dashboard. T1-113 removed record-list truncation.

**Correction to T1-113:** the original Requirements151 phase column assigns MON-FIN-005 (detailed Bank Account fields) to Phase2, MON-FIN-007/008 to Phase2 and MON-FIN-009 to Phase3+. The T1-113 statement treating bank-account detail as a Phase1 Assets blocker was incorrect. This increment corrects that engineering overlay and leaves all original IDs,text and phases intact. No later-phase bank implementation is introduced. Customer declarations remain unverified.

## Verification and limits

661 unique VS02 backend checks pass across the regression and focused XLSX rerun; the focused XLSX set is50 and included in661. New cases cover a valid multi-sheet non-template workbook and eight malformed/disguised package variants. Initial failures in the new positive test were fixture/expected-exception mistakes,corrected without weakening production validation. Existing classification/asset UI evidence is carried from T1-102/111/112/113; no new frontend execution is claimed.

Physical PostgreSQL/S3/native scanner/OCR and authenticated browser UAT remain unverified. Production storage retention and key custody remain an operator decision. No acceptance advance,independent valuation/ownership verification or development GitHub Actions.

## Tracker

After paired merges:114 numbered units =113 implementations+1 governance closure;25 Complete,4 Partial,0 Pending. Remaining Phase1 journeys are Extraction,Indexing,Financial document search and Basic financial Q&A. Preserve151 IDs,81 features,126 Phase1 memberships,acceptedThrough T1-06 and approvedForExpertIngestion=false.
