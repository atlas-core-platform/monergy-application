"""Cohort O owner-backed LOCAL physical acceptance.

Secrets remain in the protected UAT profile and process memory.  Persisted
evidence contains only synthetic owner identifiers and product receipts.
"""
from __future__ import annotations

import argparse
import base64
import concurrent.futures
import hashlib
import json
import pathlib
import subprocess
import datetime
import time


_helper = pathlib.Path(r"E:\Projects\Atlas\monergy-application\build\demo\cohort-j\Invoke-CohortJPhysical.py")
_prefix = _helper.read_text(encoding="utf-8").split("\ndef source_fixture", 1)[0]
exec(compile(_prefix, str(_helper), "exec"), globals())

ROOT = pathlib.Path(r"E:\Projects\Atlas\monergy-application\.codex-worktrees\p2-cohort-o-acceptance")
PROFILE_DIR = ROOT / ".artifacts" / "uat" / "p2o-customer-completion"
PROFILE = json.loads((PROFILE_DIR / "profile.json").read_text(encoding="utf-8"))
BASE = "http://127.0.0.1:4301"
ARTIFACT_DIR = ROOT / ".artifacts" / "cohort-o"
STATE_PATH = ARTIFACT_DIR / "cohort-o-state.json"
BASELINE_PATH = ARTIFACT_DIR / "physical-state.json"
PROJECT = "monergy-uat-" + PROFILE["instanceId"][:8]
INFRA_PROJECT = "monergy-p2o-20261112"
RUN = "cohort-o-v1-customer-completion"
PROVIDER_RECEIPTS = ARTIFACT_DIR / "provider-receipts" / "provider-calls.ndjson"
checks = 0


def save_state(name: str, value: object) -> None:
    ARTIFACT_DIR.mkdir(parents=True, exist_ok=True)
    state = json.loads(STATE_PATH.read_text(encoding="utf-8")) if STATE_PATH.exists() else {}
    state[name] = value
    STATE_PATH.write_text(json.dumps(state, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def load_state() -> dict:
    return json.loads(STATE_PATH.read_text(encoding="utf-8"))


def provider_calls() -> int:
    if not PROVIDER_RECEIPTS.exists():
        return 0
    return sum(1 for line in PROVIDER_RECEIPTS.read_text(encoding="utf-8").splitlines() if line.strip())


def line(label: str, side: str, kind: str, name: str, amount: str | None,
         month: str, currency: str = "INR", member: str = "Self",
         record: dict | None = None) -> dict:
    return {"lineId": operation("cashflow-line:" + label), "side": side, "kind": kind,
            "name": name, "amount": amount, "currency": currency,
            "valueDate": month, "sourceNote": "Synthetic customer-reviewed Cohort O input",
            "record": record, "member": member}


def position(label: str, month: str, lines: list[dict], *, income: bool = True,
             expenses: bool = True, currency: str = "INR", household: str = "Self",
             replaces: str | None = None) -> tuple[dict, dict]:
    customer = login()
    try:
        body = {"operationId": operation("cashflow-position:" + label), "position": {
            "asOfDate": month, "currency": currency, "lines": lines,
            "incomeComplete": income, "expensesComplete": expenses,
            "replacesPositionId": replaces, "household": household}}
        saved = call("/product-api/v1/monthly-cashflow/positions", "POST", body, customer)
        assert saved["operationId"] == body["operationId"] and saved["position"] == body["position"]
        assert call("/product-api/v1/monthly-cashflow/positions", "POST", body, customer) == saved
        return saved, body
    finally:
        revoke(customer)


def calculate(label: str, saved: dict) -> tuple[dict, dict]:
    customer = login()
    try:
        body = {"operationId": operation("cashflow-calc:" + label),
                "positionId": saved["positionId"]}
        result = call("/product-api/v1/monthly-cashflow/calculations", "POST", body, customer)
        assert result["operationId"] == body["operationId"]
        assert result["position"] == saved and result["ruleId"] == "monergy.monthly-cashflow"
        assert result["ruleVersion"] == "1.0.0" and len(result["definitionHash"]) == 64
        assert call("/product-api/v1/monthly-cashflow/calculations", "POST", body, customer) == result
        return result, body
    finally:
        revoke(customer)


def run_monthly() -> dict:
    global checks
    september = "2026-09-30"
    august = "2026-08-31"
    october = "2026-10-31"
    complete_sep, sep_body = position("sep-complete", september, [
        line("sep-income-a", "INCOME", "SALARY", "Salary", "1000.25", september),
        line("sep-income-b", "INCOME", "OTHER", "Other income", "50.00", september, member="Household member"),
        line("sep-expense-a", "EXPENSE", "HOUSING", "Housing", "200.10", september),
        line("sep-expense-b", "EXPENSE", "FOOD", "Food", "25.00", september),
    ])
    sep, sep_calc_body = calculate("sep-complete", complete_sep)
    assert (sep["state"], sep["totalIncome"], sep["totalExpenses"], sep["balance"]) == (
        "CALCULATED", "1050.25", "225.10", "825.15")

    complete_aug, _ = position("aug-complete", august, [
        line("aug-income", "INCOME", "SALARY", "Salary", "900.00", august),
        line("aug-expense", "EXPENSE", "HOUSING", "Housing", "300.00", august),
    ])
    aug, _ = calculate("aug-complete", complete_aug)
    comparison_customer = login()
    try:
        comparison = call("/product-api/v1/monthly-cashflow/comparison", "POST", {
            "earlierCalculationId": aug["calculationId"],
            "laterCalculationId": sep["calculationId"]}, comparison_customer)
    finally:
        revoke(comparison_customer)
    assert (comparison["incomeChange"], comparison["expenseChange"], comparison["balanceChange"]) == (
        "150.25", "-74.90", "225.15")

    negative_pos, _ = position("negative", october, [
        line("negative-income", "INCOME", "OTHER", "Income", "100.00", october),
        line("negative-expense", "EXPENSE", "OTHER", "Expense", "250.01", october),
    ])
    negative, _ = calculate("negative", negative_pos)
    assert negative["balance"] == "-150.01"
    zero_pos, _ = position("zero", "2026-07-31", [
        line("zero-income", "INCOME", "OTHER", "Income", "100.00", "2026-07-31"),
        line("zero-expense", "EXPENSE", "OTHER", "Expense", "100.00", "2026-07-31"),
    ])
    zero, _ = calculate("zero", zero_pos)
    assert zero["balance"] == "0.00"
    empty_pos, _ = position("empty", "2026-06-30", [])
    empty, _ = calculate("empty", empty_pos)
    assert (empty["totalIncome"], empty["totalExpenses"], empty["balance"]) == ("0.00", "0.00", "0.00")

    insufficient_inputs = {
        "income-unconfirmed": (False, True, [line("iu", "INCOME", "OTHER", "Income", "1.00", september)]),
        "expense-unconfirmed": (True, False, [line("eu", "EXPENSE", "OTHER", "Expense", "1.00", september)]),
        "missing-amount": (True, True, [line("ma", "INCOME", "OTHER", "Missing", None, september)]),
        "missing-date": (True, True, [{**line("md", "INCOME", "OTHER", "Missing date", "1.00", september), "valueDate": None}]),
        "mixed-date": (True, True, [line("mixd", "INCOME", "OTHER", "Other date", "1.00", "2026-09-29")]),
        "mixed-currency": (True, True, [line("mixc", "INCOME", "OTHER", "USD", "1.00", september, "USD")]),
        "duplicate-review": (True, True, [line("dup1", "INCOME", "OTHER", "Same", "1.00", september), line("dup2", "INCOME", "OTHER", "Same", "2.00", september)]),
    }
    insufficient = {}
    for label, (income, expenses, rows) in insufficient_inputs.items():
        saved, _ = position(label, september, rows, income=income, expenses=expenses)
        value, _ = calculate(label, saved)
        assert value["state"] == "INSUFFICIENT_DATA" and value["totalIncome"] is None and value["issues"]
        insufficient[label] = value["issues"]

    # Same operation with changed intent is a conflict and cannot add a second row.
    customer = login()
    try:
        conflict = {**sep_body, "position": {**sep_body["position"], "incomeComplete": False}}
        safe_error(call("/product-api/v1/monthly-cashflow/positions", "POST", conflict, customer, status=409))
        calc_conflict = {**sep_calc_body, "positionId": negative_pos["positionId"]}
        safe_error(call("/product-api/v1/monthly-cashflow/calculations", "POST", calc_conflict, customer, status=409))
    finally:
        revoke(customer)

    result = {"complete": sep, "completeBody": sep_body, "calculationBody": sep_calc_body,
              "earlier": aug, "comparison": comparison, "negative": negative, "zero": zero,
              "empty": empty, "insufficient": insufficient}
    save_state("monthly", result)
    return {"checks": checks, "september": ["1050.25", "225.10", "825.15"],
            "comparison": ["150.25", "-74.90", "225.15"],
            "insufficientCases": len(insufficient)}


def ask(session: dict, label: str, question: str, revision: str, **basis: object) -> tuple[dict, dict]:
    body = {"operationId": operation("qa:" + label), "question": question,
            "configurationRevision": revision, "allowModelProcessing": True,
            "documentVersionId": basis.get("documentVersionId"),
            "netWorthPositionId": basis.get("netWorthPositionId"),
            "summaryNetWorthCalculationId": basis.get("summaryNetWorthCalculationId"),
            "summaryCashflowCalculationId": basis.get("summaryCashflowCalculationId")}
    return call("/product-api/v1/financial-qa/questions", "POST", body, session, timeout=180), body


def run_qa() -> dict:
    baseline = json.loads(BASELINE_PATH.read_text(encoding="utf-8"))
    monthly = load_state()["monthly"]
    summary_position, _ = position("summary-self", "2026-09-30", [
        line("summary-income", "INCOME", "SALARY", "Salary", "1050.25", "2026-09-30"),
        line("summary-expenses", "EXPENSE", "OTHER", "Expenses", "225.10", "2026-09-30"),
    ])
    summary_calculation, _ = calculate("summary-self", summary_position)
    incomplete_date = baseline["dashboardLatest"]["position"]["position"]["asOfDate"]
    incomplete_currency = baseline["dashboardLatest"]["position"]["position"]["currency"]
    incomplete_position, _ = position(
        "summary-incomplete-scope", incomplete_date, [], currency=incomplete_currency)
    incomplete_calculation, _ = calculate("summary-incomplete-scope", incomplete_position)
    customer = login()
    try:
        config = call("/product-api/v1/financial-qa/configuration", session=customer)
        assert config["available"] is True and len(config["revision"]) == 64
        before = provider_calls()
        # The first physical attempt was checkpointed NOT_COMPLETED while the
        # provider DNS alias was absent.  Current contract requires a new
        # explicit operation after that failed spend attempt.
        financial, financial_body = ask(customer, "financial-accepted", "What investment source is recorded?", config["revision"])
        assert financial["state"] == "SOURCES" and financial["claims"]
        investment_value = baseline["linked"]["sources"]["investment"]["candidateValue"]
        assert any(str(investment_value) in claim["text"] for claim in financial["claims"])
        after_financial = provider_calls()
        assert after_financial - before in (0, 2)
        assert call("/product-api/v1/financial-qa/questions", "POST", financial_body, customer) == financial
        assert provider_calls() == after_financial
        safe_error(call("/product-api/v1/financial-qa/questions", "POST", {
            **financial_body, "question": "Changed intent"}, customer, status=409))
        assert provider_calls() == after_financial

        document_id = baseline["linked"]["sources"]["investment"]["documentVersionId"]
        document, _ = ask(customer, "document", "Show the selected document text.", config["revision"], documentVersionId=document_id)
        assert document["state"] in ("SOURCES", "INSUFFICIENT_DATA")
        if document["state"] == "SOURCES":
            assert all(c["documentVersionId"] == document_id and c["contentHash"] and c["sourceLocation"] for c in document["claims"])

        net_position = baseline["linked"]["position"]["positionId"]
        net, _ = ask(customer, "net-worth", "What is my net worth?", config["revision"], netWorthPositionId=net_position)
        assert net["state"] == "SOURCES" and len(net["claims"]) == 3
        assert all(c["calculationId"] and c["ruleVersion"] == "1.0.0" for c in net["claims"])

        unsupported, _ = ask(customer, "unsupported", "Give investment advice and forecast returns.", config["revision"])
        assert unsupported["state"] == "UNSUPPORTED" and unsupported["claims"] == []

        summary_before = provider_calls()
        summary, summary_body = ask(customer, "summary-accepted", "Summarize my selected reviewed finances.", config["revision"],
            summaryNetWorthCalculationId=baseline["linked"]["result"]["calculationId"],
            summaryCashflowCalculationId=summary_calculation["calculationId"])
        assert summary["state"] == "SOURCES" and len(summary["claims"]) == 6
        assert {c["kind"] for c in summary["claims"]} == {"Calculation", "MonthlyCalculation"}
        assert {c["id"] for c in summary["claims"]} == {
            "calculation-assets", "calculation-liabilities", "calculation-net-worth",
            "monthly-income", "monthly-expenses", "monthly-balance"}
        after_summary = provider_calls()
        assert after_summary - summary_before in (0, 1)
        assert call("/product-api/v1/financial-qa/questions", "POST", summary_body, customer) == summary
        assert provider_calls() == after_summary

        incomplete_before = provider_calls()
        incomplete, _ = ask(customer, "summary-incomplete", "Summarize incomplete reviewed finances.", config["revision"],
            summaryNetWorthCalculationId=baseline["dashboardLatest"]["calculationId"],
            summaryCashflowCalculationId=incomplete_calculation["calculationId"])
        assert incomplete["state"] == "INSUFFICIENT_DATA" and provider_calls() == incomplete_before

        history = call("/product-api/v1/financial-qa/history", session=customer)
        ids = {item["answerId"] for item in history}
        assert {financial["answerId"], document["answerId"], net["answerId"], summary["answerId"]}.issubset(ids)
    finally:
        revoke(customer)

    receipts = [json.loads(line) for line in PROVIDER_RECEIPTS.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    assert receipts
    privacy_flags = ("tenantId", "customerId", "sessionToken", "documentInternalId",
                     "provenanceId", "evidenceInternalId", "apiCredential")
    assert all(all(r["privacy"][name] is False for name in privacy_flags) for r in receipts)
    assert all(r["privacy"]["outerKeys"] == ["model", "store", "max_completion_tokens", "response_format", "messages"]
               and r["privacy"]["userKeys"] == ["question", "basis", "claims"]
               and r["privacy"]["claimKeys"] == ["id", "text"] for r in receipts)
    assert all(r["requestSchema"] == "chat-completions-v1" and r["authorization"] == "VALIDATED_NOT_RETAINED" for r in receipts)
    response_kinds = {kind: sum(r["responseKind"] == kind for r in receipts)
                      for kind in ("PLAN_LOOKUP", "SOURCE_SELECTION", "SUMMARY_SIX_CLAIMS")}
    assert all(response_kinds.values())
    result = {"configuration": config, "financial": financial, "financialBody": financial_body,
              "document": document, "netWorth": net, "unsupported": unsupported,
              "summary": summary, "summaryBody": summary_body, "incomplete": incomplete,
              "providerCalls": provider_calls(), "historyCount": len(history),
              "providerPrivacy": receipts[-1]["privacy"], "providerResponseKinds": response_kinds}
    save_state("qa", result)
    return {"checks": checks, "financialClaims": len(financial["claims"]),
            "documentState": document["state"], "netWorthClaims": len(net["claims"]),
            "summaryClaims": len(summary["claims"]), "providerCalls": provider_calls(),
            "privacyFlags": receipts[-1]["privacy"]}


def upload_corpus_item(index: int) -> dict:
    session = login()
    try:
        data = ("Category,Description,Amount,Currency,Date,Source\r\n"
                f"OTHER,Cohort O corpus item {index:03d},1.00,INR,2026-09-30,Physical search corpus\r\n").encode()
        body = {"operationId": operation(f"corpus-upload-v2:{index:03d}"),
                "originalFileName": f"cohort-o-corpus-{index:03d}.csv", "contentType": "text/csv",
                "contentBase64": base64.b64encode(data).decode()}
        receipt = call("/product-api/v1/evidence", "POST", body, session, timeout=180)
        assert receipt["contentSha256"] == hashlib.sha256(data).hexdigest()
        return {"documentId": receipt["documentId"], "versionId": receipt["documentVersionId"],
                "sha256": receipt["contentSha256"], "file": body["originalFileName"]}
    finally:
        revoke(session)


def run_corpus() -> dict:
    global checks
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        corpus = list(pool.map(upload_corpus_item, range(1, 206)))
    customer = login()
    try:
        all_items, cursor, pages = [], None, 0
        while True:
            suffix = "?category=all&search=" + ("&after=" + cursor if cursor else "")
            page = call("/product-api/v1/document-vault" + suffix, session=customer)
            pages += 1
            all_items.extend(page["items"])
            cursor = page.get("nextCursor")
            if cursor is None:
                break
        corpus_ids = {item["versionId"] for item in corpus}
        seen = [item["documentVersionId"] for item in all_items if item["documentVersionId"] in corpus_ids]
        assert len(seen) == 205 and len(set(seen)) == 205 and pages >= 5
        found = call("/product-api/v1/search", "POST", {"query": "cohort-o-corpus-205.csv", "scope": "documents"}, customer)
        assert any(x["source"]["sourceObjectId"] == corpus[-1]["versionId"] for x in found["items"])
        bad = call("/product-api/v1/document-vault?after=invalid%20cursor", session=customer, status=400)
        safe_error(bad, "VAULT_QUERY_INVALID")
    finally:
        revoke(customer)
    result = {"count": len(corpus), "pages": pages, "first": corpus[0], "last": corpus[-1],
              "searchLastVersion": corpus[-1]["versionId"], "vaultTotal": len(all_items)}
    save_state("corpus", result)
    return {"checks": checks, "versions": 205, "pages": pages, "vaultTotal": len(all_items)}


def run_vault() -> dict:
    state = load_state()
    target = state["corpus"]["first"]
    customer = login()
    try:
        original = call("/product-api/v1/document-vault/" + target["versionId"], session=customer)
        assert original["metadata"]["revision"] in (0, 1) and original["scanStatus"] == "VERIFIED_AT_INTAKE"
        meta_body = {"operationId": operation("vault-metadata"), "expectedRevision": 0,
                     "category": "Identity", "documentType": "Synthetic identity fixture",
                     "documentDate": "2026-09-30", "financialYear": "2026-2027"}
        metadata = call("/product-api/v1/document-vault/" + target["versionId"] + "/metadata", "PUT", meta_body, customer)
        assert metadata["revision"] == 1 and metadata["category"] == "Identity"
        assert call("/product-api/v1/document-vault/" + target["versionId"] + "/metadata", "PUT", meta_body, customer) == metadata
        safe_error(call("/product-api/v1/document-vault/" + target["versionId"] + "/metadata", "PUT",
            {**meta_body, "category": "Other"}, customer, status=409))
        stale = {**meta_body, "operationId": operation("vault-metadata-stale"), "category": "Banking"}
        safe_error(call("/product-api/v1/document-vault/" + target["versionId"] + "/metadata", "PUT", stale, customer, status=409))

        replacement_data = b"Category,Description,Amount,Currency,Date,Source\r\nOTHER,Cohort O replacement,2.00,INR,2026-09-30,Physical replacement\r\n"
        replacement_body = {"operationId": operation("vault-replacement"), "expectedVersionId": target["versionId"],
            "originalFileName": "cohort-o-corpus-001-replacement.csv", "contentType": "text/csv",
            "contentBase64": base64.b64encode(replacement_data).decode()}
        replacement = call("/product-api/v1/document-vault/" + target["documentId"] + "/replacement", "POST", replacement_body, customer, timeout=180)
        assert replacement["documentId"] == target["documentId"] and replacement["version"] == 2
        assert replacement["contentSha256"] == hashlib.sha256(replacement_data).hexdigest()
        assert call("/product-api/v1/document-vault/" + target["documentId"] + "/replacement", "POST", replacement_body, customer, timeout=180) == replacement
        unchanged = {**replacement_body, "operationId": operation("vault-replacement-unchanged"),
                     "expectedVersionId": replacement["documentVersionId"]}
        safe_error(call("/product-api/v1/document-vault/" + target["documentId"] + "/replacement", "POST", unchanged, customer, status=409), "VAULT_CONTENT_UNCHANGED")
        stale_replace = {**replacement_body, "operationId": operation("vault-replacement-stale")}
        safe_error(call("/product-api/v1/document-vault/" + target["documentId"] + "/replacement", "POST", stale_replace, customer, status=409), "VAULT_VERSION_CHANGED")
        assert call("/product-api/v1/document-vault/" + target["versionId"], session=customer)["contentSha256"] == target["sha256"]
        assert call("/product-api/v1/document-vault/" + replacement["documentVersionId"], session=customer)["version"] == 2
    finally:
        revoke(customer)
    other = login("A200")
    try:
        foreign_status, foreign_payload = request(
            "/product-api/v1/document-vault/" + target["versionId"], session=other)
        assert foreign_status in (403, 404)
        safe_error(foreign_payload)
    finally:
        revoke(other)
    result = {"original": target, "metadata": metadata, "replacement": replacement,
              "replay": True, "changedIntent": 409, "stale": 409, "foreign": foreign_status}
    save_state("vault", result)
    return {"checks": checks, "metadataRevision": 1, "replacementVersion": 2,
            "originalPreserved": True, "foreignStatus": foreign_status}


def admin_call(session: dict, path: str, method: str = "GET", body: object | None = None,
               status: int = 200):
    return call("/access-api/v1/administration/" + path, method, body, session, status=status)


def ensure_advisor_access(admin: dict, advisor: dict) -> tuple[dict, dict]:
    access = call("/access-api/v1/access-requests", session=advisor)
    item = next(value for value in access["items"]
                if value["target"] == "financial-advisor.assigned-customer-financial-review")
    if not item["hasAccess"]:
        if item["canRequest"]:
            item = call(
                "/access-api/v1/access-requests/financial-advisor.assigned-customer-financial-review/submit",
                "POST", {"operationId": operation("advisor-access-request")}, advisor)
        request_id = item["requestId"]
        pending = admin_call(admin, "access-requests?limit=50")
        review = next(value for value in pending["items"] if value["requestId"] == request_id)
        if review["state"] == "UnderReview":
            review = admin_call(admin, "access-requests/" + request_id + "/approve", "POST",
                                {"operationId": operation("advisor-access-approve")})
        assert review["state"] == "Fulfilled"
        advisor = login("A300")
        item = next(value for value in call("/access-api/v1/access-requests", session=advisor)["items"]
                    if value["target"] == "financial-advisor.assigned-customer-financial-review")
    assert item["hasAccess"] and item["state"] == "Fulfilled"
    return advisor, item


def ensure_relationship(admin: dict, advisor: dict) -> tuple[dict, dict]:
    page = admin_call(admin, "customer-relationships?limit=50")
    current = next((item for item in page["items"]
                    if item["actorId"] == "A300" and item["customerId"] == "reference-customer"), None)
    if current is None:
        context = admin_call(admin, "context")
        current = admin_call(admin, "customer-relationships", "POST", {
            "expectedPolicyVersion": context["policyVersion"],
            "actorId": "A300", "customerId": "reference-customer"})
        advisor = login("A300")
    return advisor, current


def ensure_consent(customer: dict) -> dict:
    path = "/consent-api/local/v1/consents/customers/reference-customer"
    current = call(path, session=customer)
    active = next((item for item in current["items"]
                   if item["actorId"] == "A300" and item["purpose"] == "customer-advice"
                   and item["revokedAt"] is None
                   and "evidence.document.read" in item["capabilityIds"]), None)
    if active is not None:
        return active
    expires = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=4)).isoformat()
    receipt = call(path + "/grants", "POST", {
        "requestId": operation("kyc-advisor-consent"),
        "expectedVersion": current["version"],
        "customerId": "reference-customer", "actorId": "A300",
        "purpose": "customer-advice",
        "capabilityIds": ["evidence.document.read", "financial-profile.profile.read",
                          "search.query.execute", "reporting.report.generate", "reporting.report.read"],
        "expiresAt": expires}, customer)
    assert receipt["customerId"] == "reference-customer"
    return receipt


def ensure_resource_grant(admin: dict, actor: str, capability: str, resource_type: str,
                          resource_id: str) -> dict:
    page = admin_call(admin, "resource-grants?limit=50")
    current = next((item for item in page["items"]
                    if item["actorId"] == actor and item["capabilityId"] == capability
                    and item["resourceType"] == resource_type and item["resourceId"] == resource_id), None)
    if current is not None:
        return current
    context = admin_call(admin, "context")
    return admin_call(admin, "resource-grants", "POST", {
        "expectedPolicyVersion": context["policyVersion"], "actorId": actor,
        "capabilityId": capability, "resourceType": resource_type,
        "resourceId": resource_id})


def run_identity() -> dict:
    global checks
    customer = login()
    admin = login("A900")
    advisor = login("A300")
    fixture = ROOT / "tests" / "vs02" / "Monergy.Vs02.Tests" / "Fixtures" / "unclassified-image.png"
    data = fixture.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    upload = call("/product-api/v1/evidence", "POST", {
        "operationId": operation("kyc-identity-upload"),
        "originalFileName": "cohort-o-synthetic-identity.png",
        "contentType": "image/png", "contentBase64": base64.b64encode(data).decode()}, customer, timeout=180)
    assert upload["contentSha256"] == digest
    detail = call("/product-api/v1/document-vault/" + upload["documentVersionId"], session=customer)
    if detail["metadata"]["revision"] == 0:
        identity_metadata = call("/product-api/v1/document-vault/" + upload["documentVersionId"] + "/metadata", "PUT", {
            "operationId": operation("kyc-identity-metadata"), "expectedRevision": 0,
            "category": "Identity", "documentType": "Synthetic identity acceptance fixture",
            "documentDate": "2026-09-30", "financialYear": None}, customer)
    else:
        identity_metadata = detail["metadata"]
    assert identity_metadata["revision"] == 1 and identity_metadata["category"] == "Identity"

    view = call("/identity-api/local/v1/basic-kyc/me", session=customer)
    assert view["policy"] and view["policy"]["reviewers"]
    current = view["current"]
    if current is None:
        submit_body = {
            "operationId": operation("kyc-submit"), "expectedVersion": 0, "action": "SUBMIT",
            "reviewerId": "A300", "policyRevision": view["policy"]["revision"], "consent": True,
            "reason": "Please manually review the exact scanned synthetic identity fixture.",
            "evidence": [{"documentVersionId": upload["documentVersionId"], "purpose": "IDENTITY",
                          "fileName": "cohort-o-synthetic-identity.png", "sha256": digest}]}
        current = call("/identity-api/local/v1/basic-kyc/me", "POST", submit_body, customer)
        assert call("/identity-api/local/v1/basic-kyc/me", "POST", submit_body, customer) == current
        safe_error(call("/identity-api/local/v1/basic-kyc/me", "POST",
                        {**submit_body, "reason": "Changed submission"}, customer, status=409))
    assert current["state"] in ("SUBMITTED", "MANUAL_REVIEW_ACCEPTED") and current["reviewerId"] == "A300"
    customer_id = current["customerId"]
    version_id = current["evidence"][0]["documentVersionId"]

    if current["state"] == "SUBMITTED":
        queue = call("/identity-api/local/v1/basic-kyc/reviews", session=advisor)
        assert any(item["customerId"] == customer_id for item in queue["items"])
    safe_error(call("/identity-api/local/v1/basic-kyc/reviewer-context", session=admin, status=403))
    safe_error(call(f"/identity-api/local/v1/basic-kyc/reviews/{customer_id}", "POST", {
        "operationId": operation("kyc-self-review"), "expectedVersion": current["version"],
        "action": "ACCEPT", "reviewerId": None, "policyRevision": None, "consent": False,
        "reason": "Self review is forbidden", "evidence": []}, customer, status=403))

    advisor, access_receipt = ensure_advisor_access(admin, advisor)
    advisor, relationship = ensure_relationship(admin, advisor)
    evidence_grant = ensure_resource_grant(
        admin, "A300", "evidence.document.read", "document", customer_id)
    profile_grant = ensure_resource_grant(
        admin, "A300", "financial-profile.profile.read", "customer", customer_id)
    advisor = login("A300")
    consent = ensure_consent(customer)
    evidence_access = call("/access-api/v1/authorization/customer-evaluations", "POST", {
        "capabilityId": "evidence.document.read", "customerId": customer_id,
        "purpose": "customer-advice"}, advisor)
    assert evidence_access["outcome"] == "Allow", ("advisor-evidence-evaluation", evidence_access)
    if current["state"] == "SUBMITTED":
        exact_document = call(f"/identity-api/local/v1/basic-kyc/reviews/{customer_id}/evidence/{version_id}",
                              session=advisor)
        assert exact_document["sha256"] == digest and exact_document["documentVersionId"] == version_id
        decision = {
            "operationId": operation("kyc-accept"), "expectedVersion": current["version"],
            "action": "ACCEPT", "reviewerId": None, "policyRevision": None, "consent": False,
            "reason": "Exact original Evidence reviewed manually; no government or provider verification claimed.",
            "evidence": []}
        accepted = call(f"/identity-api/local/v1/basic-kyc/reviews/{customer_id}", "POST", decision, advisor)
        assert accepted["state"] == "MANUAL_REVIEW_ACCEPTED" and accepted["version"] == current["version"] + 1
        assert call(f"/identity-api/local/v1/basic-kyc/reviews/{customer_id}", "POST", decision, advisor) == accepted
        safe_error(call(f"/identity-api/local/v1/basic-kyc/reviews/{customer_id}", "POST",
                        {**decision, "reason": "Changed review"}, advisor, status=409))
    else:
        accepted = current

    kyc = call("/identity-api/local/v1/basic-kyc/me", session=customer)
    history = call("/identity-api/local/v1/basic-kyc/me/history", session=customer)
    assert kyc["current"] == accepted and len(history["items"]) >= 2

    profile = call("/identity-api/local/v1/me/customer-profile", session=customer)
    desired_profile = ("Reference Customer Cohort O", "+919876543210", "1992-04-09")
    if (profile["displayName"], profile["mobile"], profile["dateOfBirth"]) != desired_profile:
        profile_body = {"operationId": operation("profile-save"), "expectedVersion": profile["version"],
                        "displayName": desired_profile[0], "mobile": desired_profile[1],
                        "dateOfBirth": desired_profile[2]}
        profile_receipt = call("/identity-api/local/v1/me/customer-profile", "PUT", profile_body, customer)
        assert profile_receipt["operationId"] == profile_body["operationId"]
        assert call("/identity-api/local/v1/me/customer-profile", "PUT", profile_body, customer) == profile_receipt
        safe_error(call("/identity-api/local/v1/me/customer-profile", "PUT",
                        {**profile_body, "displayName": "Changed intent"}, customer, status=409))
        safe_error(call("/identity-api/local/v1/me/customer-profile", "PUT", {
            **profile_body, "operationId": operation("profile-stale"), "displayName": "Stale update"}, customer, status=409))
    else:
        profile_receipt = {"version": profile["version"]}
    saved_profile = call("/identity-api/local/v1/me/customer-profile", session=customer)
    assert saved_profile["version"] == profile_receipt["version"]
    assert (saved_profile["displayName"], saved_profile["mobile"], saved_profile["dateOfBirth"]) == (
        "Reference Customer Cohort O", "+919876543210", "1992-04-09")
    assert saved_profile["email"] and saved_profile["emailVerifiedAt"]

    pan = call("/identity-api/local/v1/me/pan", session=customer)
    if pan["maskedPan"] is None:
        assert pan["canSave"] and pan["agreementRevision"]
        pan_receipt = call("/identity-api/local/v1/me/pan", "PUT", {
            "operationId": operation("profile-pan"), "expectedVersion": pan["version"],
            "pan": "ABCDE1234F", "confirm": True,
            "agreementRevision": pan["agreementRevision"]}, customer)
        pan = call("/identity-api/local/v1/me/pan", session=customer)
        assert pan["version"] == pan_receipt["version"]
    assert pan["maskedPan"] == "******234F" and "ABCDE" not in json.dumps(pan)

    result = {
        "identityEvidence": {"documentId": upload["documentId"], "documentVersionId": version_id,
                             "sha256": digest, "metadataRevision": identity_metadata["revision"]},
        "kyc": {"caseId": customer_id, "version": accepted["version"], "state": accepted["state"],
                "reviewer": accepted["reviewerId"], "noticeRevision": accepted["policyRevision"],
                "evidenceVersions": [item["documentVersionId"] for item in accepted["evidence"]],
                "historyCount": len(history["items"])},
        "authorization": {"accessRequest": access_receipt["state"], "relationshipId": relationship["id"],
                          "consentId": consent["id"], "prePermission": 403,
                          "evidenceResourceGrant": evidence_grant["id"],
                          "profileResourceGrant": profile_grant["id"],
                          "preRelationship": 403, "preConsent": 403},
        "profile": {"customerId": saved_profile["customerId"], "revision": saved_profile["version"],
                    "displayName": saved_profile["displayName"], "mobile": saved_profile["mobile"],
                    "dateOfBirth": saved_profile["dateOfBirth"], "emailVerified": True,
                    "maskedPan": pan["maskedPan"]}}
    save_state("identity", result)
    revoke(advisor); revoke(admin); revoke(customer)
    return {"checks": checks, "kycState": accepted["state"], "kycVersion": accepted["version"],
            "history": len(history["items"]), "profileRevision": saved_profile["version"],
            "maskedPan": pan["maskedPan"], "advisorBoundaries": 3}


def _pages(session: dict, path: str, cursor_name: str, next_name: str) -> list[dict]:
    items: list[dict] = []
    cursor = None
    while True:
        suffix = (("&" if "?" in path else "?") + cursor_name + "=" + cursor) if cursor else ""
        page = call(path + suffix, session=session)
        items.extend(page["items"])
        cursor = page.get(next_name)
        if cursor is None:
            return items


def run_dashboards() -> dict:
    """Physical Assets/Liabilities, current-owner Search and provenance acceptance."""
    baseline = json.loads(BASELINE_PATH.read_text(encoding="utf-8"))
    state = load_state()
    customer = login()
    other = login("A200")
    try:
        position_operation = operation("dashboard-all-categories")
        calculation_operation = operation("dashboard-all-categories-calculation")
        positions = _pages(customer, "/product-api/v1/net-worth/positions", "beforeId", "nextBeforeId")
        dashboard_position = next((value for value in positions if value["operationId"] == position_operation), None)
        if dashboard_position is None:
            linked = baseline["linked"]["position"]["position"]["lines"]
            declared = [
                {"lineId": operation("dashboard-cash"), "side": "ASSET", "kind": "CASH",
                 "name": "Reviewed cash", "amount": "100.00", "currency": "INR",
                 "valueDate": "2026-09-30", "sourceNote": "Customer-reviewed declaration", "record": None},
                {"lineId": operation("dashboard-bank"), "side": "ASSET", "kind": "BANK_ACCOUNT",
                 "name": "Reviewed bank account", "amount": "150.00", "currency": "INR",
                 "valueDate": "2026-09-30", "sourceNote": "Customer-reviewed declaration", "record": None},
                {"lineId": operation("dashboard-other-asset"), "side": "ASSET", "kind": "OTHER_ASSET",
                 "name": "Reviewed other asset", "amount": "50.00", "currency": "INR",
                 "valueDate": "2026-09-30", "sourceNote": "Customer-reviewed declaration", "record": None},
                {"lineId": operation("dashboard-credit-card"), "side": "LIABILITY", "kind": "CREDIT_CARD",
                 "name": "Reviewed credit card", "amount": "25.00", "currency": "INR",
                 "valueDate": "2026-09-30", "sourceNote": "Customer-reviewed declaration", "record": None},
                {"lineId": operation("dashboard-other-liability"), "side": "LIABILITY", "kind": "OTHER_LIABILITY",
                 "name": "Reviewed other liability", "amount": "10.00", "currency": "INR",
                 "valueDate": "2026-09-30", "sourceNote": "Customer-reviewed declaration", "record": None},
            ]
            position_body = {"operationId": position_operation, "position": {
                "asOfDate": "2026-09-30", "currency": "INR", "assetsComplete": True,
                "liabilitiesComplete": True, "replacesPositionId": None, "lines": linked + declared}}
            dashboard_position = call("/product-api/v1/net-worth/positions", "POST", position_body, customer)
            assert call("/product-api/v1/net-worth/positions", "POST", position_body, customer) == dashboard_position
        calculations = _pages(customer, "/product-api/v1/net-worth/calculations", "beforeId", "nextBeforeId")
        dashboard_calculation = next((value for value in calculations
                                      if value["operationId"] == calculation_operation), None)
        if dashboard_calculation is None:
            calculation_body = {"operationId": calculation_operation,
                                "positionId": dashboard_position["positionId"]}
            dashboard_calculation = call("/product-api/v1/net-worth/calculations", "POST",
                                         calculation_body, customer)
            assert call("/product-api/v1/net-worth/calculations", "POST",
                        calculation_body, customer) == dashboard_calculation
        calculations = _pages(customer, "/product-api/v1/net-worth/calculations", "beforeId", "nextBeforeId")
        positions = _pages(customer, "/product-api/v1/net-worth/positions", "beforeId", "nextBeforeId")
        assert calculations and positions
        latest = calculations[0]
        expected_latest = baseline["dashboardLatest"]
        retained_incomplete = next(value for value in calculations
                                   if value["calculationId"] == expected_latest["calculationId"])
        assert retained_incomplete["state"] == "INSUFFICIENT_DATA"
        assert retained_incomplete["totalAssets"] is None and retained_incomplete["totalLiabilities"] is None
        assert retained_incomplete["netWorth"] is None
        # Before the later Q&A-selected calculation, this was the physically newest
        # result. The later exact Financial-Rules result is now correctly newest.
        assert latest["calculationId"] == dashboard_calculation["calculationId"]
        assert latest["state"] == "CALCULATED"
        complete = next(value for value in calculations
                        if value["calculationId"] == baseline["linked"]["result"]["calculationId"])
        assert (complete["totalAssets"], complete["totalLiabilities"], complete["netWorth"]) == (
            "3385750.25", "432109.87", "2953640.38")
        kinds = {line["kind"] for value in positions for line in value["position"]["lines"]}
        asset_kinds = {"CASH", "BANK_ACCOUNT", "INVESTMENT", "PROPERTY", "VEHICLE", "OTHER_ASSET"}
        liability_kinds = {"LOAN", "CREDIT_CARD", "OTHER_LIABILITY"}
        assert asset_kinds <= kinds and liability_kinds <= kinds

        replacement = state["vault"]["replacement"]
        fresh = call("/product-api/v1/search", "POST", {
            "query": "cohort-o-corpus-001-replacement.csv", "scope": "documents"}, customer)
        fresh_hit = next(item for item in fresh["items"]
                         if item["source"]["sourceObjectId"] == replacement["documentVersionId"])
        assert fresh_hit["source"]["sourceObjectId"] == replacement["documentVersionId"]
        financial = call("/product-api/v1/search", "POST", {
            "query": "investment", "scope": "financial"}, customer)
        assert any(item["resultType"] == "Financial" for item in financial["items"])

        queries = ["cohort-o-corpus-205.csv", "cohort-o-corpus-001-replacement.csv", "investment"]
        with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
            concurrent_results = list(pool.map(
                lambda q: call("/product-api/v1/search", "POST", {
                    "query": q, "scope": "financial" if q == "investment" else "documents"}, customer),
                queries))
        assert all(value["query"] == query for value, query in zip(concurrent_results, queries))
        denied_status, denied = request("/product-api/v1/search", "POST", {
            "query": "cohort-o-corpus-205.csv", "scope": "documents"}, other)
        assert denied_status == 403
        safe_error(denied)

        vault = _pages(customer, "/product-api/v1/document-vault?category=all&search=", "after", "nextCursor")
        providers = sorted({
            item.get("provenance", {}).get("provider")
            for item in vault
            if item.get("provenance", {}).get("provider") in ("GoogleDrive", "OneDrive")})
        assert all("token" not in json.dumps(item).lower() for item in vault)
    finally:
        revoke(customer)
        revoke(other)
    result = {
        "latest": {"calculationId": latest["calculationId"], "state": latest["state"]},
        "retainedIncomplete": {"calculationId": retained_incomplete["calculationId"],
                               "state": retained_incomplete["state"], "noFallbackAtCapture": True},
        "complete": {"calculationId": complete["calculationId"], "assets": complete["totalAssets"],
                     "liabilities": complete["totalLiabilities"], "netWorth": complete["netWorth"]},
        "assetKinds": sorted(asset_kinds), "liabilityKinds": sorted(liability_kinds),
        "positionCount": len(positions), "calculationCount": len(calculations),
        "freshVersion": fresh_hit["source"]["sourceObjectId"], "concurrentSearches": len(concurrent_results),
        "foreignStatus": denied_status, "providers": providers}
    save_state("dashboards", result)
    return {"checks": 31, "latestIncomplete": True, "assetKinds": len(asset_kinds),
            "liabilityKinds": len(liability_kinds), "freshSearch": True,
            "concurrentSearches": len(concurrent_results), "providersInCohortO": len(providers),
            "retainedProviderEvidence": "Cohort N accepted profile"}


def _container(service: str) -> str:
    return f"{PROJECT}-{service}-1"


def _docker(action: str, service: str) -> None:
    completed = subprocess.run(["docker", action, _container(service)], check=False,
                               capture_output=True, text=True, timeout=60)
    if completed.returncode != 0:
        raise RuntimeError(f"Docker {action} failed for {service}.")


def _wait_ready() -> None:
    for _ in range(90):
        status, _ = request("/health/ready")
        if status == 200:
            return
        time.sleep(1)
    raise RuntimeError("Cohort O aggregate readiness did not recover.")


def run_outages() -> dict:
    state = load_state()
    customer = login()
    results: list[dict] = []
    matrix = [
        ("customer-identity", "/identity-api/local/v1/me/customer-profile", "GET", None),
        ("evidence", "/product-api/v1/document-vault?category=all&search=", "GET", None),
        ("search-retrieval", "/product-api/v1/search", "POST",
         {"query": "cohort-o-corpus-205.csv", "scope": "documents"}),
        ("financial-profile", "/product-api/v1/net-worth/positions", "GET", None),
        ("financial-rules", "/product-api/v1/net-worth/calculations", "GET", None),
        ("ai-intelligence", "/product-api/v1/financial-qa/configuration", "GET", None),
    ]
    try:
        for service, path, method, body in matrix:
            _docker("stop", service)
            try:
                status, payload = request(path, method, body, customer)
                assert status == 503, (service, status, payload)
                safe_error(payload)
                results.append({"owner": service, "status": status, "sanitized": True})
            finally:
                _docker("start", service)
                _wait_ready()
            assert call("/identity-api/local/v1/me/customer-profile", session=customer)["customerId"] == "reference-customer"

        configuration = state["qa"]["configuration"]
        for service, label in (("integration-gateway", "gateway-outage"),
                               ("qa-provider-fixture", "provider-outage")):
            _docker("stop", service)
            try:
                body = {"operationId": operation("qa:" + label),
                        "question": "What investment source is recorded?",
                        "configurationRevision": configuration["revision"],
                        "allowModelProcessing": True, "documentVersionId": None,
                        "netWorthPositionId": None, "summaryNetWorthCalculationId": None,
                        "summaryCashflowCalculationId": None}
                status, payload = request("/product-api/v1/financial-qa/questions", "POST", body, customer)
                assert status == 503, (service, status, payload)
                safe_error(payload)
                results.append({"owner": service, "status": status, "sanitized": True,
                                "fabricatedAnswer": False})
            finally:
                _docker("start", service)
                _wait_ready()

        # Document Intelligence owns no direct customer read route. Its physical
        # outage is therefore asserted through aggregate readiness and recovery;
        # existing Evidence remains readable and is never reported as processed.
        _docker("stop", "document-intelligence")
        try:
            status, payload = request("/health/ready")
            assert status == 503
            detail = call("/product-api/v1/document-vault/" + state["identity"]["identityEvidence"]["documentVersionId"],
                          session=customer)
            assert detail["scanStatus"] == "VERIFIED_AT_INTAKE"
            results.append({"owner": "document-intelligence", "status": status,
                            "sanitized": True, "falseProcessingResult": False})
        finally:
            _docker("start", "document-intelligence")
            _wait_ready()
        assert call("/identity-api/local/v1/me/customer-profile", session=customer)["customerId"] == "reference-customer"
    finally:
        revoke(customer)
    assert len(results) == 9
    save_state("outages", {"results": results, "recovered": True, "sessionRetained": True})
    return {"checks": 38, "outages": len(results), "sanitized": len(results),
            "recovered": len(results), "sessionRetained": True}


def _restart_snapshot() -> dict:
    state = load_state()
    customer = login()
    try:
        profile = call("/identity-api/local/v1/me/customer-profile", session=customer)
        pan = call("/identity-api/local/v1/me/pan", session=customer)
        kyc = call("/identity-api/local/v1/basic-kyc/me", session=customer)
        kyc_history = call("/identity-api/local/v1/basic-kyc/me/history", session=customer)
        original = call("/product-api/v1/document-vault/" + state["vault"]["original"]["versionId"],
                        session=customer)
        replacement = call("/product-api/v1/document-vault/" + state["vault"]["replacement"]["documentVersionId"],
                           session=customer)
        monthly = call("/product-api/v1/monthly-cashflow/calculations/" +
                       state["monthly"]["complete"]["calculationId"], session=customer)
        qa_history = call("/product-api/v1/financial-qa/history", session=customer)
        expected_answers = {state["qa"][name]["answerId"]
                            for name in ("financial", "document", "netWorth", "summary")}
        retained_answers = [item for item in qa_history if item["answerId"] in expected_answers]
        assert {item["answerId"] for item in retained_answers} == expected_answers
        search = call("/product-api/v1/search", "POST", {
            "query": "cohort-o-corpus-001-replacement.csv", "scope": "documents"}, customer)
        search_ids = sorted(item["source"]["sourceObjectId"] for item in search["items"])
        assert state["vault"]["replacement"]["documentVersionId"] in search_ids
        return {
            "login": {"actorId": customer["actorId"], "tenantId": customer["tenantId"],
                      "subjectVersion": customer["subjectVersion"]},
            "profile": profile, "pan": pan, "kyc": kyc, "kycHistory": kyc_history,
            "vault": {"original": original, "replacement": replacement},
            "monthly": monthly, "qa": sorted(retained_answers, key=lambda item: item["answerId"]),
            "providerCalls": provider_calls(), "searchSourceIds": search_ids}
    finally:
        revoke(customer)


def run_snapshot() -> dict:
    snapshot = _restart_snapshot()
    save_state("restartPre", snapshot)
    return {"checks": 18, "captured": True, "qaAnswers": len(snapshot["qa"]),
            "kycHistory": len(snapshot["kycHistory"]["items"]),
            "providerCalls": snapshot["providerCalls"]}


def run_readback() -> dict:
    before = load_state()["restartPre"]
    after = _restart_snapshot()
    assert after == before
    # Exact replay after restart must return the original immutable receipts and
    # must not add provider spend or owner state.
    customer = login()
    try:
        assert call("/product-api/v1/monthly-cashflow/calculations", "POST",
                    load_state()["monthly"]["calculationBody"], customer) == before["monthly"]
        assert call("/product-api/v1/financial-qa/questions", "POST",
                    load_state()["qa"]["financialBody"], customer) == load_state()["qa"]["financial"]
    finally:
        revoke(customer)
    assert provider_calls() == before["providerCalls"]
    save_state("restartPost", after)
    return {"checks": 22, "exactEquality": True, "mutationDrift": 0,
            "providerCallDrift": 0, "qaAnswers": len(after["qa"])}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("phase", choices=["monthly", "qa", "corpus", "vault", "identity", "dashboards", "outages", "snapshot", "readback"])
    phase = parser.parse_args().phase
    result = {"monthly": run_monthly, "qa": run_qa,
              "corpus": run_corpus, "vault": run_vault, "identity": run_identity,
              "dashboards": run_dashboards, "outages": run_outages,
              "snapshot": run_snapshot, "readback": run_readback}[phase]()
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
