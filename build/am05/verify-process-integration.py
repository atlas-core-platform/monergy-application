"""Exercise real AM, C&I and Audit binaries with separate runtime credentials."""
import hashlib
import importlib.util
import json
import os
import pathlib
import subprocess
import time
import urllib.error
import urllib.request

root = pathlib.Path(__file__).resolve().parents[2]
source = root / ".artifacts/am05/access-management"
evidence = root / ".artifacts/am05"
container = os.environ["AM05_POSTGRES_CONTAINER_ID"]
checks = 0
processes = {}
logs = []
base_env = {key: value for key, value in os.environ.items()
            if not key.startswith(("AM05_", "MONERGY_AM_", "Monergy__"))}
base_env.update(ASPNETCORE_ENVIRONMENT="Development", DOTNET_ENVIRONMENT="Development")
base_env["Logging__LogLevel__Default"] = "Warning"
base_env["Monergy__AccessIntegration__Enabled"] = "true"
base_env["Monergy__ExecutionZone"] = "CI_EPHEMERAL"
urls = {"am": "http://127.0.0.1:5088", "ci": "http://127.0.0.1:5101", "audit": "http://127.0.0.1:5102"}
binaries = {
    "am": source / "src/Monergy.AccessManagement/bin/Release/net10.0/Monergy.Services.AccessManagement.dll",
    "ci": root / "services/customer-identity/bin/Release/net10.0/Monergy.Services.CustomerIdentity.dll",
    "audit": root / "build/local/Monergy.LocalAccessAuditHost/bin/Release/net10.0/Monergy.LocalAccessAuditHost.dll",
}
worker = source / "tools/Monergy.AccessManagement.DeliveryReference/bin/Release/net10.0/Monergy.AccessManagement.DeliveryReference.dll"


def token(name):
    return os.environ["AM05_" + name + "_TOKEN"]


def database(service, tenant, sql):
    result = subprocess.run(["docker", "exec", "-i", container, "psql", "-U", "postgres", "-d",
                             ("am_" + tenant.lower()) if service == "am" else ("am05_" + service + "_" + tenant.lower()),
                             "-At", "-v", "ON_ERROR_STOP=1", "-c", sql], check=True, capture_output=True, text=True, timeout=10)
    return result.stdout.strip()


def environment(kind):
    env = dict(base_env)
    if kind == "am":
        env.update({"Monergy__AccessManagement__Adapter": "postgres-reference",
                    "Monergy__AccessManagement__TrustedContext": "customer-identity-reference",
                    "Monergy__AccessManagement__CustomerIdentityUrl": urls["ci"] + "/",
                    "Monergy__AccessManagement__CustomerIdentityToken": token("CI_CONTEXT"),
                    "Monergy__AccessManagement__IdentityProvisioningToken": token("CI_PROVISIONING"),
                    "Monergy__AccessManagement__MembershipToken": token("MEMBERSHIP")})
        for tenant in ("T001", "T002"):
            env["Monergy__AccessManagement__TenantDatabases__" + tenant] = os.environ["MONERGY_AM_" + tenant + "_RUNTIME"]
    elif kind == "ci":
        env.update({"Monergy__AccessIntegration__AccessManagementUrl": urls["am"] + "/",
                    "Monergy__AccessIntegration__MembershipToken": token("MEMBERSHIP"),
                    "Monergy__AccessIntegration__CustomerIdentityToken": token("CI_CONTEXT"),
                    "Monergy__AccessIntegration__IdentityProvisioningToken": token("CI_PROVISIONING"),
                    "Monergy__AccessIntegration__CustomerIdentityEventToken": token("CI_EVENT")})
        index = 0
        for tenant in ("T001", "T002"):
            env["Monergy__AccessIntegration__CustomerIdentityDatabases__" + tenant] = os.environ["AM05_CI_" + tenant + "_RUNTIME"]
            for actor in ("A900", "A100"):
                prefix = "Monergy__AccessIntegration__Identities__" + str(index) + "__"
                env.update({prefix + "TenantId": tenant, prefix + "ActorId": actor, prefix + "Token": token(tenant + "_" + actor)})
                index += 1
    else:
        env["Monergy__AccessIntegration__AuditEventToken"] = token("AUDIT_EVENT")
        for tenant in ("T001", "T002"):
            env["Monergy__AccessIntegration__AuditDatabases__" + tenant] = os.environ["AM05_AUDIT_" + tenant + "_RUNTIME"]
    return env


def call(kind, method, path, body=None, headers=None, expected=200):
    global checks
    request = urllib.request.Request(urls[kind] + path, method=method,
        data=None if body is None else json.dumps(body).encode(), headers={"Content-Type": "application/json", **(headers or {})})
    try:
        response = urllib.request.urlopen(request, timeout=8)
    except urllib.error.HTTPError as error:
        response = error
    raw = response.read()
    assert response.status == expected, (kind, method, path, response.status, expected)
    checks += 1
    return json.loads(raw) if raw and response.headers.get("Content-Type", "").startswith("application/json") else None


def start(kind, lost_receipt=False, lost_provision=False):
    env = environment(kind)
    if lost_receipt:
        env["Monergy__AccessIntegration__VerificationLoseReceiptOnce"] = "true"
    if lost_provision:
        env["Monergy__AccessIntegration__VerificationLoseProvisioningReceiptOnce"] = "true"
    output = (evidence / (kind + "-process-" + str(len(logs)) + ".log")).open("w")
    logs.append(output)
    processes[kind] = subprocess.Popen(["dotnet", str(binaries[kind]), "--urls", urls[kind]], cwd=root, env=env, stdout=output, stderr=subprocess.STDOUT)
    path = "/health/ready" if kind == "am" else "/internal/health/ready"
    for _ in range(100):
        assert processes[kind].poll() is None, kind + " process exited"
        try:
            call(kind, "GET", path)
            return
        except (urllib.error.URLError, ConnectionError, AssertionError):
            time.sleep(0.1)
    raise AssertionError(kind + " did not become ready")


def stop(kind):
    process = processes.pop(kind)
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def login(tenant, actor):
    session = call("ci", "POST", "/local/v1/tenant-sessions", {"tenantId": tenant},
                   {"X-Monergy-Reference-Authentication": token(tenant + "_" + actor)})
    print("::add-mask::" + session["authenticationContextId"], flush=True)
    return session


def session_headers(session, tenant=None):
    return {"X-Monergy-Tenant": tenant or session["tenantId"], "X-Monergy-Session": session["authenticationContextId"],
            "X-Monergy-Reference-Actor": "A900", "X-Monergy-Reference-Workload": "forged-admin"}


def admin(session, method, path, body=None, expected=200):
    return call("am", method, "/v1/administration/" + path, body, session_headers(session), expected)


def dispatch(tenant):
    env = dict(base_env)
    env["MONERGY_AM_REFERENCE_DELIVERY"] = "1"
    env["MONERGY_AM_" + tenant + "_DELIVERY"] = os.environ["MONERGY_AM_" + tenant + "_DELIVERY"]
    for destination in ("authorization", "sessions", "audit"):
        owner = "audit" if destination == "audit" else "ci"
        env["MONERGY_AM_REFERENCE_" + destination.upper() + "_URL"] = urls[owner] + "/"
        env["MONERGY_AM_REFERENCE_" + destination.upper() + "_TOKEN"] = token("AUDIT_EVENT" if owner == "audit" else "CI_EVENT")
    result = subprocess.run(["dotnet", str(worker), "dispatch", tenant, "100"], env=env, check=True,
                            capture_output=True, text=True, timeout=30)
    lines = result.stdout.splitlines()
    return json.loads(lines[-2]), json.loads(lines[-1])


try:
    for tenant in ("T001", "T002"):
        database("ci", tenant, "TRUNCATE customer_identity.trusted_sessions,customer_identity.access_event_inbox,customer_identity.access_subject_versions; UPDATE customer_identity.access_policy_version SET version=0;")
        database("audit", tenant, "TRUNCATE audit.access_event_inbox,audit.inbox,audit.evidence CASCADE;")
    start("am")
    start("ci", lost_provision=True)
    start("audit", lost_receipt=True)
    call("am", "GET", "/internal/v1/access-subjects/A900", headers={"X-Monergy-Tenant": "T001"}, expected=403)
    call("ci", "POST", "/local/v1/tenant-sessions", {"tenantId": "T001"}, expected=401)
    call("ci", "POST", "/local/v1/tenant-sessions", {"tenantId": "T001", "actorId": "A900"}, expected=400)
    administrators = {tenant: login(tenant, "A900") for tenant in ("T001", "T002")}
    for tenant, administrator in administrators.items():
        revision = admin(administrator, "GET", "members")["policyVersion"]
        admin(administrator, "POST", "members", {"expectedPolicyVersion": revision, "actorId": "A100"})
    # UI-01: sign-out can revoke only the supplied tenant-scoped session;
    # repeated sign-out succeeds without revoking another session.
    ending = login("T001", "A900")
    end_request = {"tenantId": "T002", "authenticationContextId": ending["authenticationContextId"]}
    call("ci", "POST", "/local/v1/tenant-sessions/revoke", end_request)
    admin(ending, "GET", "members")
    end_request["tenantId"] = "T001"
    call("ci", "POST", "/local/v1/tenant-sessions/revoke", end_request)
    call("ci", "POST", "/local/v1/tenant-sessions/revoke", end_request)
    admin(ending, "GET", "members", expected=401)
    admin(administrators["T001"], "GET", "members")
    call("ci", "POST", "/local/v1/tenant-sessions/revoke", {**end_request, "actorId": "A100"}, expected=400)
    first = login("T001", "A100")
    admin(first, "GET", "members", expected=403)
    call("am", "GET", "/v1/administration/members", headers=session_headers(administrators["T001"], "T002"), expected=401)
    call("am", "GET", "/v1/administration/members", headers={"X-Monergy-Reference-Tenant": "T001", "X-Monergy-Reference-Actor": "A900",
         "X-Monergy-Reference-Authentication-Context": "legacy", "X-Monergy-Reference-Workload": "legacy"}, expected=401)
    owner = administrators["T001"]
    revision = admin(owner, "GET", "roles")["policyVersion"]
    permission = admin(owner, "POST", "permissions", {"expectedPolicyVersion": revision, "code": "owner-read", "label": "Read profile",
                         "grants": [{"capabilityId": "financial-profile.profile.read", "scope": "Tenant"}]})
    role = admin(owner, "POST", "roles", {"expectedPolicyVersion": permission["policyVersion"], "code": "owner-reader", "label": "Reader", "permissionIds": [permission["id"]]})
    spec = importlib.util.spec_from_file_location("am06_onboarding", root / "build/am06/verify-onboarding.py")
    onboarding = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(onboarding)
    onboarding.verify(call, admin, database, stop, start, token, owner)
    role["policyVersion"] = admin(owner, "GET", "roles")["policyVersion"]
    assigned = admin(owner, "PUT", "members/A100", {"expectedPolicyVersion": role["policyVersion"], "businessRoleId": role["id"], "active": True, "tenantAdmin": False})
    current = login("T001", "A100")
    unused = login("T001", "A100")
    other_tenant = login("T002", "A100")
    evaluation = {"capabilityId": "financial-profile.profile.read", "resourceType": "customer", "resourceId": "customer-100"}
    assert call("am", "POST", "/v1/authorization/evaluations", evaluation, session_headers(current))["outcome"] == "Allow"
    assert call("am", "POST", "/v1/authorization/evaluations", evaluation, session_headers(owner))["outcome"] == "Deny"
    admin(owner, "PUT", "members/A100", {"expectedPolicyVersion": assigned["policyVersion"], "businessRoleId": role["id"], "active": False, "tenantAdmin": False})
    call("am", "POST", "/v1/authorization/evaluations", evaluation, session_headers(current), expected=401)
    call("ci", "POST", "/local/v1/tenant-sessions", {"tenantId": "T001"}, {"X-Monergy-Reference-Authentication": token("T001_A100")}, expected=401)
    unused_hash = hashlib.sha256(unused["authenticationContextId"].encode()).hexdigest()
    assert database("ci", "T001", "SELECT revoked FROM customer_identity.trusted_sessions WHERE session_hash='" + unused_hash + "';") == "f"
    retries = 0
    for tenant in ("T001", "T002"):
        deadline = time.monotonic() + 45
        while True:
            summary, status = dispatch(tenant)
            retries += summary["retrying"]
            assert summary["deadLettered"] == 0
            if all(item["status"] == "Delivered" or item["count"] == 0 for item in status):
                break
            assert time.monotonic() < deadline, "Owner delivery did not converge"
            time.sleep(0.25)
        source_count = int(database("am", tenant, "SELECT count(*) FROM am.outbox WHERE event_type='AccessManagementAuditEvidence';"))
        assert int(database("audit", tenant, "SELECT count(*) FROM audit.evidence WHERE source_contract_id='CID-070';")) == source_count
        assert int(database("audit", tenant, "SELECT count(*) FROM audit.access_event_inbox;")) == source_count
    assert retries >= 1, "Lost Audit acknowledgement was not recovered"
    assert database("ci", "T001", "SELECT revoked FROM customer_identity.trusted_sessions WHERE session_hash='" + unused_hash + "';") == "t"
    assert call("am", "POST", "/v1/authorization/evaluations", evaluation, session_headers(other_tenant))["outcome"] == "Deny"
    envelope = json.loads(database("audit", "T001", "SELECT envelope::text FROM audit.access_event_inbox ORDER BY event_id LIMIT 1;"))
    event_headers = {"X-Monergy-Reference-Tenant": "T001", "X-Monergy-Reference-Transport": token("AUDIT_EVENT")}
    assert call("audit", "POST", "/reference/events/audit", envelope, event_headers)["disposition"] == "Duplicate"
    call("audit", "POST", "/reference/events/audit", {**envelope, "operation": "forged.operation"}, event_headers, expected=409)
    call("audit", "POST", "/reference/events/audit", envelope, {**event_headers, "X-Monergy-Reference-Tenant": "T002"}, expected=409)
    call("audit", "POST", "/reference/events/audit", envelope, {**event_headers, "X-Monergy-Reference-Transport": token("CI_EVENT")}, expected=403)
    call("audit", "POST", "/reference/events/audit", {**envelope, "password": "not-accepted"}, event_headers, expected=400)
    stop("ci")
    admin(owner, "GET", "members", expected=503)
    start("ci")
    admin(owner, "GET", "members")
    stop("am")
    call("ci", "POST", "/internal/v1/tenant-sessions/validate", {"tenantId": "T002", "authenticationContextId": other_tenant["authenticationContextId"]},
         {"X-Monergy-Owner-Token": token("CI_CONTEXT")}, expected=503)
    start("am")
    stop("audit")
    start("audit")
    for tenant in ("T001", "T002"):
        assert dispatch(tenant)[0]["delivered"] == 0
    call("am", "POST", "/v1/authorization/evaluations", evaluation, session_headers(unused), expected=401)
    assert call("am", "POST", "/v1/authorization/evaluations", evaluation, session_headers(other_tenant))["outcome"] == "Deny"
    for kind in ("ci", "audit"):
        env = environment(kind)
        env["ASPNETCORE_ENVIRONMENT"] = "Production"
        env["DOTNET_ENVIRONMENT"] = "Production"
        result = subprocess.run(["dotnet", str(binaries[kind]), "--urls", "http://127.0.0.1:5199"], env=env, capture_output=True, timeout=10)
        assert result.returncode != 0, "Production owner integration started"
        checks += 1
    print("AM-05 real owner HTTP/process checks passed:", checks)
    print("Confirmed: AM authorization, C&I durable invalidation, Audit evidence, lost acknowledgement, tenant isolation, owner outages, process restarts, Production rejection")
finally:
    for kind in list(processes):
        stop(kind)
    for output in logs:
        output.close()
