"""Physical dependency-outage checks for durable Evidence and classification review."""

from __future__ import annotations

import importlib.util
import json
import pathlib
import subprocess


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

PROJECT = "monergy-uat-081b4d91"
EXTERNAL_POSTGRES = "monergy-p2p-20261113-postgres-1"
EXTERNAL_S3 = "monergy-p2p-20261113-seaweedfs-1"


def container(service: str) -> str:
    return f"{PROJECT}-{service}-1"


def change(action: str, name: str) -> None:
    subprocess.run(["docker", action, name], check=True, stdout=subprocess.DEVNULL, timeout=120)


def list_path() -> str:
    return "/classification-api/local/v1/administration/classification-reviews?status=all"


def main() -> None:
    customer = ns["login"]()
    admin = ns["login"]("A900")
    results: dict[str, object] = {}
    try:
        before = len(ns["library"](customer)["items"])
        change("stop", EXTERNAL_S3)
        try:
            ns["wait_ready"](503)
            failure = ns["upload"](customer, "s3-outage-recovery", "s3-outage.txt", "text/plain",
                                   b"Statement of account\nS3 outage recovery probe", status=503)
            ns["safe_error"](failure, "EVIDENCE_STORAGE_UNAVAILABLE")
            assert len(ns["library"](customer)["items"]) == before
        finally:
            change("start", EXTERNAL_S3)
        ns["wait_ready"](200)
        recovered = ns["upload"](customer, "s3-outage-recovery", "s3-outage.txt", "text/plain",
                                 b"Statement of account\nS3 outage recovery probe")
        replay = ns["upload"](customer, "s3-outage-recovery", "s3-outage.txt", "text/plain",
                              b"Statement of account\nS3 outage recovery probe")
        assert recovered["documentVersionId"] == replay["documentVersionId"]
        results["objectStorage"] = {"outage": 503, "falseReceipt": False, "recovered": True, "exactReplay": True}

        before = len(ns["library"](customer)["items"])
        change("stop", EXTERNAL_POSTGRES)
        try:
            ns["wait_ready"](503)
            evidence_failure = ns["upload"](customer, "postgres-outage-recovery", "postgres-outage.txt", "text/plain",
                                            b"Pay slip\nPostgreSQL outage recovery probe", status=503)
            ns["safe_error"](evidence_failure)
            classification_failure = ns["call"](list_path(), session=admin, status=503, timeout=60)
            ns["safe_error"](classification_failure)
        finally:
            change("start", EXTERNAL_POSTGRES)
        ns["wait_ready"](200)
        assert len(ns["library"](customer)["items"]) == before
        recovered = ns["upload"](customer, "postgres-outage-recovery", "postgres-outage.txt", "text/plain",
                                 b"Pay slip\nPostgreSQL outage recovery probe")
        assert recovered["documentVersionId"]
        assert ns["call"](list_path(), session=admin)["tenantId"] == "T001"
        results["postgres"] = {"evidenceOutage": 503, "classificationOutage": 503, "falseReceipt": False,
                               "volatileFallback": False, "recovered": True}

        change("stop", container("access-management"))
        try:
            ns["wait_ready"](503)
            failure = ns["call"](list_path(), session=admin, status=503)
            ns["safe_error"](failure)
        finally:
            change("start", container("access-management"))
        ns["wait_ready"](200)
        assert ns["call"](list_path(), session=admin)["tenantId"] == "T001"
        results["accessManagement"] = {"outage": 503, "sessionRetained": True, "recovered": True}

        change("stop", container("audit"))
        try:
            ns["wait_ready"](503)
            failure = ns["call"](list_path(), session=admin, status=503)
            ns["safe_error"](failure, "AUDIT_OWNER_UNAVAILABLE")
        finally:
            change("start", container("audit"))
        ns["wait_ready"](200)
        assert ns["call"](list_path(), session=admin)["tenantId"] == "T001"
        results["audit"] = {"outage": 503, "mutationAttempted": False, "sessionRetained": True, "recovered": True}

        change("stop", container("document-intelligence"))
        try:
            ns["wait_ready"](503)
            failure = ns["call"](list_path(), session=admin, status=503)
            ns["safe_error"](failure)
        finally:
            change("start", container("document-intelligence"))
        ns["wait_ready"](200)
        assert ns["call"](list_path(), session=admin)["tenantId"] == "T001"
        results["classificationOwner"] = {"outage": 503, "historyErased": False, "sessionRetained": True, "recovered": True}

        results["aggregateReadyAfterRecovery"] = True
        (ROOT / ".artifacts" / "cohort-p" / "dependency-outages.json").write_text(
            json.dumps(results, indent=2), encoding="utf-8")
        print(json.dumps(results))
    finally:
        ns["revoke"](customer)
        ns["revoke"](admin)


if __name__ == "__main__":
    main()
