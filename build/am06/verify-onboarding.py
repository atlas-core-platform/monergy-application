"""Real owner onboarding checks, called by the AM-05 process composition."""


def verify(call, admin, database, stop, start, token, owner):
    def stage(identifier, csv):
        preview = admin(owner, "POST", "imports/preview", {"csv": csv})
        assert preview["valid"]
        return admin(owner, "POST", "imports/" + identifier + "/commit", {
            "expectedPolicyVersion": preview["policyVersion"], "csv": csv, "contentHash": preview["contentHash"]})

    def process(identifier):
        revision = admin(owner, "GET", "members")["policyVersion"]
        return admin(owner, "POST", "imports/" + identifier + "/process", {"expectedPolicyVersion": revision})

    csv = "email,role_code,group_codes\nfirst@onboarding.test,owner-reader,\nsecond@onboarding.test,,\n"
    stage("am06-batch", csv)
    failed = process("am06-batch")
    assert failed["status"] == "Failed" and failed["errorCode"] == "IDENTITY_PROVISIONING_UNAVAILABLE"
    assert database("ci", "T001", "SELECT count(*) FROM customer_identity.tenant_principals WHERE normalized_email LIKE '%@onboarding.test';") == "1"
    assert database("am", "T001", "SELECT count(*) FROM am.access_subjects WHERE normalized_email LIKE '%@onboarding.test';") == "0"
    first_actor = database("ci", "T001", "SELECT actor_id FROM customer_identity.tenant_principals WHERE normalized_email='first@onboarding.test';")
    stop("ci")
    start("ci")
    complete = process("am06-batch")
    assert complete["status"] == "Activated"
    assert complete["identities"][0]["actorId"] == first_actor
    assert database("ci", "T001", "SELECT count(*) FROM customer_identity.tenant_principals WHERE normalized_email LIKE '%@onboarding.test';") == "2"
    assert database("ci", "T001", "SELECT count(*) FROM customer_identity.provisioning_receipts WHERE import_id='am06-batch';") == "2"
    assert database("am", "T001", "SELECT count(*) FROM am.access_subjects WHERE normalized_email LIKE '%@onboarding.test' AND status='Active';") == "2"
    assert database("am", "T001", "SELECT count(*) FROM am.access_subjects WHERE normalized_email='second@onboarding.test' AND business_role_id IS NULL;") == "1"
    replay = process("am06-batch")
    assert replay["activatedPolicyVersion"] == complete["activatedPolicyVersion"]
    assert stage("am06-batch", csv)["status"] == "Activated"

    headers = {"X-Monergy-Owner-Token": token("CI_PROVISIONING"), "X-Monergy-Tenant": "T001"}
    key = database("ci", "T001", "SELECT idempotency_key FROM customer_identity.provisioning_receipts WHERE import_id='am06-batch' AND row_number=1;")
    request = {"tenantId": "T001", "importId": "am06-batch", "rowNumber": 1,
               "normalizedEmail": "first@onboarding.test", "idempotencyKey": key}
    path = "/internal/v1/tenant-identities/provision"
    assert call("ci", "POST", path, request, headers)["actorId"] == first_actor
    call("ci", "POST", path, request, {**headers, "X-Monergy-Owner-Token": token("CI_CONTEXT")}, expected=403)
    call("ci", "POST", path, request, {**headers, "X-Monergy-Tenant": "T002"}, expected=400)
    call("ci", "POST", path, {**request, "password": "must-not-be-accepted"}, headers, expected=400)
    call("ci", "POST", path, {**request, "normalizedEmail": "changed@onboarding.test"}, headers, expected=409)
    other = call("ci", "POST", path, {**request, "tenantId": "T002"}, {**headers, "X-Monergy-Tenant": "T002"})
    assert other["actorId"] != first_actor
    assert database("am", "T002", "SELECT count(*) FROM am.access_subjects WHERE normalized_email='first@onboarding.test';") == "0"

    # Lose another receipt, then cancel. An orphan C&I identity is not access.
    stop("ci")
    start("ci", lost_provision=True)
    cancelled_csv = "email,role_code,group_codes\ncancelled@onboarding.test,,\nnever-created@onboarding.test,,\n"
    stage("am06-cancel", cancelled_csv)
    assert process("am06-cancel")["status"] == "Failed"
    revision = admin(owner, "GET", "members")["policyVersion"]
    cancelled = admin(owner, "POST", "imports/am06-cancel/cancel", {"expectedPolicyVersion": revision})
    assert cancelled["status"] == "Cancelled"
    assert database("am", "T001", "SELECT count(*) FROM am.access_subjects WHERE normalized_email='cancelled@onboarding.test';") == "0"
    assert database("ci", "T001", "SELECT count(*) FROM customer_identity.tenant_principals WHERE normalized_email='never-created@onboarding.test';") == "0"
    orphan_actor = database("ci", "T001", "SELECT actor_id FROM customer_identity.tenant_principals WHERE normalized_email='cancelled@onboarding.test';")
    stage("am06-restage", "email,role_code,group_codes\ncancelled@onboarding.test,,\n")
    resumed = process("am06-restage")
    assert resumed["status"] == "Activated" and resumed["identities"][0]["actorId"] == orphan_actor
    print("AM-06 real owner onboarding passed: lost acknowledgement, restart, full-batch activation, replay, tenant isolation, cancellation and safe restaging")
