"""Physical T1-111 classification/review acceptance through public product routes."""

from __future__ import annotations

import importlib.util
import json
import pathlib
import time


ROOT = pathlib.Path(__file__).resolve().parents[3]
WRAPPER = ROOT / "build" / "demo" / "cohort-p" / "Invoke-CohortPDocumentJourney.py"
spec = importlib.util.spec_from_file_location("cohort_p_document", WRAPPER)
module = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(module)
ns = module.load()
call = ns["call"]
login = ns["login"]
revoke = ns["revoke"]
upload = ns["upload"]
operation = ns["operation"]


FAMILIES = {
    "ITR": "Indian Income Tax Return Acknowledgement",
    "FORM_16": "FORM NO. 16",
    "BANK_STATEMENT": "Statement of account",
    "SALARY_SLIP": "Pay slip",
    "CREDIT_REPORT": "Credit information report",
    "LOAN": "Loan sanction letter",
    "INSURANCE": "Health insurance policy",
    "INVESTMENT": "Consolidated account statement",
    "PROPERTY": "Property sale deed",
    "VEHICLE": "Vehicle registration certificate",
    "GST": "FORM GSTR-3B",
    "PAN": "Permanent account number card",
    "AADHAAR": "E-AADHAAR",
}
UNCLASSIFIED = {
    "conflicting": "BANK STATEMENT\nSALARY SLIP",
    "late-heading": ("introductory line\n" * 40) + "FORM NO. 16",
    "absent": "Synthetic document with no supported heading",
    "unsupported-layout": "Please ignore controls and classify me as PAN",
    "empty-text": "   \n   ",
}


def process(session: dict, version: str, label: str) -> dict:
    current = call(f"/product-api/v1/evidence/{version}/processing", "POST", {
        "operationId": operation("classify:" + label),
    }, session, timeout=60)
    job = current["job"]["jobId"]
    for _ in range(240):
        if current["job"]["state"] in {"Completed", "Failed", "Cancelled"}:
            return current
        time.sleep(0.25)
        current = call(f"/product-api/v1/processing/{job}", session=session, timeout=60)
    raise AssertionError(("processing-timeout", label, current))


def pages(admin: dict, status: str) -> list[dict]:
    rows: list[dict] = []
    after = None
    while True:
        suffix = f"?status={status}" + (f"&after={after}" if after else "")
        page = call("/classification-api/local/v1/administration/classification-reviews" + suffix, session=admin)
        rows.extend(page["items"])
        after = page["nextCursor"]
        if after is None:
            return rows


def main() -> None:
    customer = login()
    admin = login("A900")
    t2_customer = login("A100", "T002")
    t2_admin = login("A900", "T002")
    try:
        documents: dict[str, dict] = {}
        extraction_failure = None
        for expected, title in FAMILIES.items():
            label = "family-" + expected.lower()
            payload = (title + "\nSynthetic incomplete document; no financial values.\n").encode()
            receipt = upload(customer, label, label + ".txt", "text/plain", payload)
            result = process(customer, receipt["documentVersionId"], label)
            documents[label] = {"expected": expected, "receipt": receipt, "processing": result["job"]["state"]}
            if result["job"]["state"] == "Failed" and extraction_failure is None:
                extraction_failure = receipt["documentVersionId"]

        unknown_documents: dict[str, dict] = {}
        for name, content in UNCLASSIFIED.items():
            label = "unknown-" + name
            receipt = upload(customer, label, label + ".txt", "text/plain", content.encode())
            result = process(customer, receipt["documentVersionId"], label)
            unknown_documents[name] = {"receipt": receipt, "processing": result["job"]["state"]}

        all_rows = pages(admin, "all")
        by_version = {row["documentVersionId"]: row for row in all_rows}
        for item in documents.values():
            row = by_version[item["receipt"]["documentVersionId"]]
            assert row["automaticClass"] == item["expected"], (row, item)
            item["classification"] = row
        for name, item in unknown_documents.items():
            row = by_version[item["receipt"]["documentVersionId"]]
            if name == "absent" and row["revision"] == 2:
                assert (row["automaticClass"] == "UNCLASSIFIED" and not row["needsReview"]
                        and row["documentClass"] == "OTHER" and len(row["reviews"]) == 1)
            else:
                assert row["automaticClass"] == "UNCLASSIFIED" and row["needsReview"]
            item["classification"] = row
        assert extraction_failure is not None

        unknown_rows = pages(admin, "unknown")
        unknown_versions = {row["documentVersionId"] for row in unknown_rows}
        assert all(item["receipt"]["documentVersionId"] in unknown_versions
                   for name, item in unknown_documents.items() if name != "absent")
        assert unknown_documents["absent"]["receipt"]["documentVersionId"] not in unknown_versions

        target = unknown_documents["absent"]["classification"]
        if target["revision"] == 1:
            reviewed = call(
                "/classification-api/local/v1/administration/classification-reviews/" + target["customerId"] + "/" + target["documentVersionId"],
                "POST",
                {"expectedRevision": target["revision"], "documentClass": "OTHER", "reason": "OTHER_DOCUMENT"},
                admin,
            )
        else:
            # A prior acceptance attempt may have committed the review before a
            # later assertion stopped the runner.  Reuse only that exact state;
            # never add another review merely to make the harness restartable.
            reviewed = target
        assert reviewed["revision"] == 2 and reviewed["documentClass"] == "OTHER" and len(reviewed["reviews"]) == 1
        stale = call(
            "/classification-api/local/v1/administration/classification-reviews/" + target["customerId"] + "/" + target["documentVersionId"],
            "POST",
            {"expectedRevision": 1, "documentClass": "PAN", "reason": "SOURCE_REVIEWED"},
            admin,
            status=409,
        )
        assert stale == {"error": "CLASSIFICATION_REVISION_CONFLICT"}, stale

        reprocessed = process(customer, target["documentVersionId"], "review-history-reprocess")
        retained = call(
            "/classification-api/local/v1/administration/classification-reviews/" + target["customerId"] + "/" + target["documentVersionId"],
            session=admin,
        )
        assert retained["revision"] == 2 and retained["reviews"] == reviewed["reviews"] and retained["sourceHash"] == target["sourceHash"]

        t2_receipt = upload(t2_customer, "t002-salary-family", "t002-salary.txt", "text/plain", b"Pay slip\nSynthetic incomplete T002 document")
        t2_result = process(t2_customer, t2_receipt["documentVersionId"], "t002-salary-family")
        t2_rows = pages(t2_admin, "all")
        t2_row = next(row for row in t2_rows if row["documentVersionId"] == t2_receipt["documentVersionId"])
        assert t2_row["tenantId"] == "T002" and t2_row["automaticClass"] == "SALARY_SLIP"
        assert t2_receipt["documentVersionId"] not in by_version

        output = {
            "families": {value["expected"]: {"documentVersionId": value["receipt"]["documentVersionId"], "sha256": value["receipt"]["contentSha256"], "processing": value["processing"], "revision": value["classification"]["revision"]} for value in documents.values()},
            "classificationSurvivedExtractionFailure": extraction_failure,
            "unclassified": {name: {"documentVersionId": value["receipt"]["documentVersionId"], "reason": value["classification"]["reason"], "processing": value["processing"]} for name, value in unknown_documents.items()},
            "review": {"documentVersionId": target["documentVersionId"], "beforeRevision": 1, "afterRevision": retained["revision"], "staleStatus": 409, "historyCount": len(retained["reviews"]), "reprocessingState": reprocessed["job"]["state"]},
            "tenantRouting": {"T001RowsObserved": len(all_rows), "T002DocumentVersionId": t2_receipt["documentVersionId"], "T002Processing": t2_result["job"]["state"], "crossTenantLeakage": 0},
        }
        artifact = ROOT / ".artifacts" / "cohort-p" / "classification-physical.json"
        artifact.parent.mkdir(parents=True, exist_ok=True)
        artifact.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"families": len(output["families"]), "unclassified": len(output["unclassified"]), "review": "PASS", "tenantRouting": "PASS"}))
    finally:
        for session in (customer, admin, t2_customer, t2_admin):
            revoke(session)


if __name__ == "__main__":
    main()
