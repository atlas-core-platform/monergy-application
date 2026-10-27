# LOCAL UAT Poppler package refresh

Reported Docker build failure: noble repositories no longer supplied pinned 24.02.0-1ubuntu9.9. Updated poppler-utils and libpoppler134 together to 24.02.0-1ubuntu9.10 in Runtime.Containerfile, toolchain versions and current OCR documentation. Historical implementation receipts remain unchanged.

On 2026-10-26 the configured Noble update/security feeds advanced again and no longer supplied 24.02.0-1ubuntu9.10. The LOCAL UAT pin, toolchain manifest and current OCR documentation now use the feed-confirmed paired version 24.02.0-1ubuntu9.11. Scanning, PDF validation and production service images remain unchanged.

Both amd64 packages were downloaded from archive.ubuntu.com and verified against its noble-updates Packages index SHA-256 values. pdfinfo and pdftoppm report 24.02.0; PDF metadata inspection and single-page PNG rendering passed on Ubuntu Noble using the extracted replacement library. Containerfile and toolchain pins agree. This is a current exact package pin, not a frozen Ubuntu snapshot; future repository supersession may require another reviewed refresh. Existing native version/data-hash guards remain enabled.

No Docker engine is available here, so full image build and live Windows UAT remain unverified. No profile, database, feature scope or acceptance boundary changes.

The 2026-10-26 follow-up supersedes that environment limitation for the refreshed package revision: the isolated Windows Docker build completed, `prepare-ocr.sh` passed its data and native-version guards, aggregate LOCAL readiness reported 13 services, and ClamAV was healthy. The existing retained profiles were not started, reset or deleted.
