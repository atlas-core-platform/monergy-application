# P2-29 — Customer-reported vehicle ownership lifecycle

Financial Profile remains the owner of reviewed vehicle records and their immutable revision history. A customer with current self read, document-read and profile-update authority can now append a dated `OWNED` or `DISPOSED` ownership review to an existing vehicle record. Existing payloads remain `UNREVIEWED` with no invented date.

The owner revalidates the exact original financial fact and provenance before every lifecycle mutation. The request is tenant/customer/actor bound, uses an immutable operation identifier, requires the current expected revision and is retry safe. Stale reviews lose; a correction preserves the latest ownership state and cannot move the value date beyond an already recorded disposal.

The Gateway resolves the customer from the current session, evaluates authority before the write, validates the owner receipt, reads the exact saved revision back and re-evaluates authority afterwards. Customer Web keeps the original request for ambiguous retry, exposes effective-dated ownership and complete version history, and labels the result as customer reported.

Asset snapshots exclude a vehicle when its latest owner record says it was disposed on or before the requested value date. Earlier snapshots remain historically eligible, and restoring ownership appends a new version without rewriting the disposed receipt.

This journey does not verify registration or legal ownership, finance or liens, insurance, professional valuation, sale proceeds, tax, external provider data or advice. The existing governed JSON persistence supplies backward-compatible defaults, so no database migration is introduced. Physical PostgreSQL, installed-browser and retained LOCAL/live acceptance remain separate gates.
