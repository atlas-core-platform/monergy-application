"""Prove product processing still uses the registered object version after a newer S3 version exists."""

from __future__ import annotations

import importlib.util
import json
import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[3]
WRAPPER = ROOT / "build" / "demo" / "cohort-p" / "Invoke-CohortPDocumentJourney.py"
CLASSIFICATION = ROOT / "build" / "demo" / "cohort-p" / "Invoke-CohortPClassification.py"
spec = importlib.util.spec_from_file_location("cohort_p_document", WRAPPER)
module = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(module)
ns = module.load()
class_spec = importlib.util.spec_from_file_location("cohort_p_classification", CLASSIFICATION)
class_module = importlib.util.module_from_spec(class_spec)
assert class_spec and class_spec.loader
class_spec.loader.exec_module(class_module)

probe = json.loads((ROOT / ".artifacts" / "cohort-p" / "storage-exact-version.json").read_text(encoding="utf-8"))
customer = ns["login"]()
admin = ns["login"]("A900")
try:
    result = class_module.process(customer, probe["DocumentVersionId"], "exact-registered-version-after-newer-object")
    rows = class_module.pages(admin, "all")
    row = next(item for item in rows if item["documentVersionId"] == probe["DocumentVersionId"])
    assert row["sourceHash"] == probe["originalSha256"]
    assert row["automaticClass"] == "BANK_STATEMENT"
    evidence = {
        "documentVersionId": probe["DocumentVersionId"],
        "registeredObjectVersion": probe["registeredObjectVersion"],
        "newerObjectVersion": probe["newerObject"]["versionId"],
        "processingState": result["job"]["state"],
        "classification": row["automaticClass"],
        "sourceHash": row["sourceHash"],
        "newestSubstitution": False,
    }
    (ROOT / ".artifacts" / "cohort-p" / "exact-version-processing.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    print(json.dumps(evidence))
finally:
    ns["revoke"](customer)
    ns["revoke"](admin)
