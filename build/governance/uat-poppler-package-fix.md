# LOCAL UAT Poppler package refresh

Reported Docker build failure: noble repositories no longer supplied pinned 24.02.0-1ubuntu9.9. Updated poppler-utils and libpoppler134 together to 24.02.0-1ubuntu9.10 in Runtime.Containerfile, toolchain versions and current OCR documentation. Historical implementation receipts remain unchanged.

Both amd64 packages were downloaded from archive.ubuntu.com and verified against its noble-updates Packages index SHA-256 values. pdfinfo and pdftoppm report 24.02.0; PDF metadata inspection and single-page PNG rendering passed on Ubuntu Noble using the extracted replacement library. Containerfile and toolchain pins agree. This is a current exact package pin, not a frozen Ubuntu snapshot; future repository supersession may require another reviewed refresh. Existing native version/data-hash guards remain enabled.

No Docker engine is available here, so full image build and live Windows UAT remain unverified. No profile, database, feature scope or acceptance boundary changes.
