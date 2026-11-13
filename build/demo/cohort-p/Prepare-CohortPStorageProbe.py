"""Create one live-scanned, public-route Evidence item for storage recovery probes."""

from __future__ import annotations

import importlib.util
import json
import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[3]
WRAPPER = ROOT / "build" / "demo" / "cohort-p" / "Invoke-CohortPDocumentJourney.py"
spec = importlib.util.spec_from_file_location("cohort_p_document", WRAPPER)
module = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(module)
ns = module.load()

session = ns["login"]()
try:
    payload = b"Statement of account\nCohort P exact-version storage probe.\n"
    receipt = ns["upload"](session, "storage-interruption-probe", "storage-probe.txt", "text/plain", payload)
    safe = {
        "documentId": receipt["documentId"],
        "documentVersionId": receipt["documentVersionId"],
        "sha256": receipt["contentSha256"],
        "length": len(payload),
        "contentType": "text/plain",
    }
    target = ROOT / ".artifacts" / "cohort-p" / "storage-probe-input.json"
    target.write_text(json.dumps(safe, indent=2), encoding="utf-8")
    print(json.dumps(safe))
finally:
    ns["revoke"](session)
