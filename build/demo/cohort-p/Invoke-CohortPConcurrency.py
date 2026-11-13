"""Physical replay/concurrency probes for durable intake and classification review."""

from __future__ import annotations

import concurrent.futures
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


def main() -> None:
    customer_one = ns["login"]()
    customer_two = ns["login"]()
    admin_one = ns["login"]("A900")
    admin_two = ns["login"]("A900")
    try:
        content = b"Statement of account\nConcurrent exact durable intake.\n"
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            receipts = list(executor.map(
                lambda session: ns["upload"](session, "concurrent-identical-intake", "concurrent.txt", "text/plain", content),
                (customer_one, customer_two),
            ))
        assert receipts[0]["documentVersionId"] == receipts[1]["documentVersionId"]
        items = ns["library"](customer_one)["items"]
        assert sum(item["documentVersionId"] == receipts[0]["documentVersionId"] for item in items) == 1
        changed = ns["upload"](customer_one, "concurrent-identical-intake", "concurrent.txt", "text/plain",
                               content + b"changed", status=409)
        ns["safe_error"](changed)
        changed_type = ns["upload"](customer_one, "concurrent-identical-intake", "concurrent.txt", "application/pdf",
                                    content, status=400)
        ns["safe_error"](changed_type, "EVIDENCE_STAGING_FAILED")

        artifact = json.loads((ROOT / ".artifacts" / "cohort-p" / "classification-physical.json").read_text(encoding="utf-8"))
        version = artifact["unclassified"]["empty-text"]["documentVersionId"]
        path = "/classification-api/local/v1/administration/classification-reviews/reference-customer/" + version
        bodies = [
            {"expectedRevision": 1, "documentClass": "OTHER", "reason": "OTHER_DOCUMENT"},
            {"expectedRevision": 1, "documentClass": "PAN", "reason": "SOURCE_REVIEWED"},
        ]
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            outcomes = list(executor.map(lambda pair: ns["request"](path, "POST", pair[0], pair[1], timeout=60)[:2],
                                         zip(bodies, (admin_one, admin_two))))
        assert sorted(status for status, _ in outcomes) == [200, 409], outcomes
        conflict = next(payload for status, payload in outcomes if status == 409)
        assert conflict == {"error": "CLASSIFICATION_REVISION_CONFLICT"}
        row = ns["call"](path, session=admin_one)
        assert row["revision"] == 2 and len(row["reviews"]) == 1
        processing = class_module.process(customer_one, version, "concurrent-review-reprocess")
        retained = ns["call"](path, session=admin_one)
        assert retained["revision"] == 2 and retained["reviews"] == row["reviews"]

        result = {
            "intake": {"attempts": 2, "acceptedVersions": 1, "sameReceipt": True, "changedBytes": 409, "changedType": 400},
            "review": {"attempts": 2, "winners": 1, "conflicts": 1, "revision": 2, "historyEntries": 1},
            "reprocessing": {"state": processing["job"]["state"], "historyPreserved": True},
        }
        (ROOT / ".artifacts" / "cohort-p" / "concurrency.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps(result))
    finally:
        for session in (customer_one, customer_two, admin_one, admin_two):
            ns["revoke"](session)


if __name__ == "__main__":
    main()
