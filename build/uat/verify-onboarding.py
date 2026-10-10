"""Actual owner/process acceptance for the explicitly disposable onboarding CI profile.
Run after verify-uat.py. This is destructive fixture work, never a retained UAT profile.
"""
import datetime
import hashlib
import json
import os
import pathlib
import subprocess
import time
import urllib.error
import urllib.request

root = pathlib.Path(__file__).resolve().parents[2]
folder = root / '.artifacts/uat/ci'
profile = json.loads((folder / 'profile.json').read_text())
assert profile.get('onboarding') is True
assert os.environ.get('MONERGY_UAT_DISPOSABLE') == profile['instanceId'], 'Explicit disposable instance authorization required'
base = 'http://127.0.0.1:' + str(profile['port'])
compose_command = ['docker', 'compose', '--env-file', str(folder / 'compose.env'), '-f', str(root / 'build/uat/compose.yml'), '--project-name', 'monergy-uat-' + profile['instanceId'][:8]]
checks = 0


def call(path, method='GET', body=None, session=None, headers=None, status=200):
    global checks
    fields = {'Content-Type': 'application/json', **(headers or {})}
    if session:
        fields.update({'X-Monergy-Tenant': session['tenantId'], 'X-Monergy-Session': session['authenticationContextId']})
    request = urllib.request.Request(base + path, method=method, headers=fields, data=None if body is None else json.dumps(body).encode())
    try:
        response = urllib.request.urlopen(request, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    raw = response.read()
    assert response.status == status, (path, response.status, status)
    checks += 1
    return json.loads(raw) if raw else None


def login(actor='A900', tenant='T001'):
    identity = next(value for value in profile['identities'] if value['tenantId'] == tenant and value['actorId'] == actor)
    return call('/identity-api/local/v1/tenant-sessions', 'POST', {'tenantId': tenant}, headers={'X-Monergy-Reference-Authentication': identity['token']})


def admin(path, method='GET', body=None, status=200):
    return call('/access-api/v1/administration/' + path, method, body, owner, status=status)


def revision():
    return admin('context')['policyVersion']


def member(actor, role):
    return admin('members/' + actor, 'PUT', {'expectedPolicyVersion': revision(), 'businessRoleId': role, 'active': True, 'tenantAdmin': False})


def compose(*args):
    subprocess.run(compose_command + list(args), check=True, stdout=subprocess.DEVNULL, timeout=180)


def ready():
    for _ in range(90):
        try:
            call('/health/ready')
            return
        except (AssertionError, urllib.error.URLError, TimeoutError):
            time.sleep(1)
    raise AssertionError('Onboarding UAT readiness timed out')


def sql(database, query):
    # Cluster-owner fixture injection only, bounded to this disposable CI instance.
    assert database in {'uat_consent_t001', 'uat_customer_identity_t001', 'uat_am_t001'}
    return subprocess.run(compose_command + ['exec', '-T', 'postgres', 'psql', '-U', 'postgres', '-d', database, '-At', '-v', 'ON_ERROR_STOP=1', '-c', query],
                          check=True, capture_output=True, text=True, timeout=30).stdout.strip()


def envelope(session, name, payload, customer='reference-customer', key=None):
    return {'contractName': name, 'contractVersion': '1.0.0', 'requestId': 'onboarding-' + name, 'correlationId': 'onboarding-acceptance', 'causationId': None, 'idempotencyKey': key,
            'security': {'actor': {'actorId': session['actorId'], 'actorType': 'HUMAN', 'authenticatedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'authenticationContextId': session['authenticationContextId']},
                         'workload': {'workloadId': 'onboarding-uat', 'workloadIdentityId': 'reference-workload'},
                         'access': {'purpose': 'CUSTOMER_ADVICE', 'consentReferenceId': None, 'authorizationContextId': 'reference-authorization', 'customerId': customer}}, 'payload': payload}


def evaluate(session, capability='financial-profile.profile.read', customer='reference-customer', status=200):
    return call('/access-api/v1/authorization/customer-evaluations', 'POST', {'capabilityId': capability, 'customerId': customer, 'purpose': 'customer-advice'}, session, status=status)


def consent_version(session):
    return call('/consent-api/local/v1/consents/customers/reference-customer', session=session)['version']


def grant(session, request_id):
    return {'requestId': request_id, 'expectedVersion': consent_version(session), 'customerId': 'reference-customer', 'actorId': 'A300', 'purpose': 'customer-advice',
            'capabilityIds': ['financial-profile.profile.read', 'search.query.execute', 'evidence.document.read', 'reporting.report.generate', 'reporting.report.read'],
            'expiresAt': (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1)).isoformat()}


ready()
owner = login()
other_admin = login(tenant='T002')
role = next(item['id'] for item in admin('roles')['items'] if item['code'] == 'uat-boundary-tester')
for actor in ('A100', 'A200', 'A300'):
    member(actor, role)
customer = login('A100')
second_customer = login('A200')
advisor = login('A300')

# Setup is tenant/admin bound; a receipt cannot be fabricated or saved against a stale review.
call('/uat-api/v1/setup', status=401)
call('/uat-api/v1/setup', session=customer, status=403)
call('/uat-api/v1/setup/activate', 'POST', {}, owner, status=409)
state = call('/uat-api/v1/setup', session=other_admin)
assert state['state'] == 'ReadyForAdmin' and state['receiptReference'] is None
submission = {'requestId': 'ci-setup-T002', 'expectedPolicyVersion': state['policyVersion'], 'organizationReviewed': True, 'accessReviewed': True}
call('/uat-api/v1/setup', 'POST', {**submission, 'expectedPolicyVersion': state['policyVersion'] + 1}, other_admin, status=409)
call('/uat-api/v1/setup', 'POST', {**submission, 'accessReviewed': False}, other_admin, status=409)
completed = call('/uat-api/v1/setup', 'POST', submission, other_admin)
assert completed['state'] == 'Active'
assert call('/uat-api/v1/setup', 'POST', submission, other_admin)['receiptReference'] == completed['receiptReference']
call('/uat-api/v1/setup', 'POST', {**submission, 'requestId': 'conflicting-setup'}, other_admin, status=409)
assert call('/uat-api/v1/setup', session=owner)['state'] == 'ReadyForAdmin'  # Browser completes T001.

# Self-ownership is authoritative, not inferred from names or tenant membership.
assert evaluate(customer)['accessBasis'] == 'Self'
assert evaluate(customer, customer='C001')['outcome'] == 'Deny'
assert evaluate(owner)['outcome'] == 'Deny'
assert evaluate(advisor)['reasonCode'] == 'CustomerRelationshipRequired'
assert call('/access-api/v1/authorization/customer-evaluations', 'POST', {'capabilityId': 'search.query.execute', 'customerId': 'reference-customer', 'purpose': 'marketing'}, advisor)['outcome'] == 'Deny'
assignment = admin('customer-relationships', 'POST', {'expectedPolicyVersion': revision(), 'actorId': 'A300', 'customerId': 'reference-customer'})
evaluate(advisor, status=401)
advisor = login('A300')
assert evaluate(advisor)['reasonCode'] == 'ConsentRequired'
admin('customer-relationships', 'POST', {'expectedPolicyVersion': revision(), 'actorId': 'A100', 'customerId': 'reference-customer'}, status=409)
admin('customer-relationships', 'POST', {'expectedPolicyVersion': revision(), 'actorId': 'A300', 'customerId': 'other-customer'}, status=404)

# Tenant Admin cannot supply Consent on behalf of the customer.
consent_path = '/consent-api/local/v1/consents/customers/reference-customer/grants'
request = grant(customer, 'ci-consent-1')
call(consent_path, 'POST', request, owner, status=403)
# A missing durable audit/outbox write must roll back the entire owner mutation.
before_consent = consent_version(customer)
sql('uat_consent_t001', 'REVOKE INSERT ON consent.outbox FROM uat_consent_t001_runtime;')
try:
    call(consent_path, 'POST', request, customer, status=503)
finally:
    sql('uat_consent_t001', 'GRANT INSERT ON consent.outbox TO uat_consent_t001_runtime;')
assert consent_version(customer) == before_consent
assert sql('uat_consent_t001', 'SELECT (SELECT count(*) FROM consent.grants)+(SELECT count(*) FROM consent.receipts)+(SELECT count(*) FROM consent.audit)+(SELECT count(*) FROM consent.outbox);') == '0'
receipt = call(consent_path, 'POST', request, customer)
assert call(consent_path, 'POST', request, customer) == receipt
call(consent_path, 'POST', {**request, 'capabilityIds': ['search.query.execute']}, customer, status=409)
assert evaluate(advisor)['accessBasis'] == 'Advisor'
assert evaluate(advisor, 'financial-profile.profile.update')['outcome'] == 'Deny'

# Actual document creation, metadata and version lookup, plus report generation/read and search.
content = 'Monergy local onboarding acceptance fixture. Not customer financial data.'
digest = hashlib.sha256(content.encode()).hexdigest()
for actor, customer_id, suffix in ((customer, 'reference-customer', 'primary'), (second_customer, 'C001', 'other')):
    payload = {'documentId': 'onboarding-doc-' + suffix, 'documentVersionId': 'onboarding-version-' + suffix, 'evidenceId': 'onboarding-evidence-' + suffix,
               'customerId': customer_id, 'sourceName': 'Local acceptance fixture', 'originalFileName': 'local-fixture.txt', 'declaredContentType': 'text/plain',
               'contentReference': 'reference://onboarding-evidence', 'contentSha256': digest, 'receivedAt': datetime.datetime.now(datetime.timezone.utc).isoformat()}
    call('/contracts/cid-020/v1', 'POST', envelope(actor, 'CreateDocumentVersion', payload, customer_id, 'create-' + suffix), actor)
metadata = {'documentId': 'onboarding-doc-primary', 'customerId': 'reference-customer'}
call('/contracts/cid-021/v1', 'POST', envelope(advisor, 'GetEvidenceMetadata', metadata), advisor)
call('/contracts/cid-022/v1', 'POST', envelope(advisor, 'GetEvidenceReference', {'documentVersionId': 'onboarding-version-primary', 'customerId': 'reference-customer'}), advisor)
denied_document = call('/contracts/cid-021/v1', 'POST', envelope(advisor, 'GetEvidenceMetadata', {**metadata, 'documentId': 'onboarding-doc-other'}), advisor, status=400)
assert denied_document['error'] is not None
search = {'customerId': 'reference-customer', 'query': 'income', 'matchMode': 'Lexical', 'limit': 20}
call('/contracts/cid-042/v1', 'POST', envelope(advisor, 'SearchDocuments', search), advisor)
report = call('/contracts/cid-051/v1', 'POST', envelope(advisor, 'GenerateReport', {'customerId': 'reference-customer'}, key='onboarding-report'), advisor)
report_id = report['data']['reportId']
call('/contracts/cid-052/v1', 'POST', envelope(advisor, 'GetReport', {'customerId': 'reference-customer', 'reportId': report_id}), advisor)
call('/contracts/cid-052/v1', 'POST', envelope(second_customer, 'GetReport', {'customerId': 'C001', 'reportId': report_id}, 'C001'), second_customer, status=404)

# Revoke without AM delivery, then prove expiry, ownership change and dependency outage fail closed.
compose('stop', 'delivery-t001')
revocation = {'requestId': 'ci-revoke-1', 'expectedVersion': consent_version(customer)}
revoked = call(consent_path + '/' + receipt['id'], 'DELETE', revocation, customer)
assert call(consent_path + '/' + receipt['id'], 'DELETE', revocation, customer) == revoked
assert evaluate(advisor)['outcome'] == 'Deny'
call('/contracts/cid-021/v1', 'POST', envelope(advisor, 'GetEvidenceMetadata', metadata), advisor, status=403)
request = grant(customer, 'ci-consent-expiry')
receipt = call(consent_path, 'POST', request, customer)
sql('uat_consent_t001', "UPDATE consent.grants SET granted_at=clock_timestamp()-interval '2 days',expires_at=clock_timestamp()-interval '1 day' WHERE revoked_at IS NULL;")
assert evaluate(advisor)['outcome'] == 'Deny'
call(consent_path + '/' + receipt['id'], 'DELETE', {'requestId': 'ci-revoke-expired', 'expectedVersion': consent_version(customer)}, customer)
receipt = call(consent_path, 'POST', grant(customer, 'ci-consent-owner-change'), customer)
sql('uat_customer_identity_t001', "UPDATE customer_identity.customers SET version=version+1 WHERE customer_id='reference-customer';")
assert evaluate(advisor)['outcome'] == 'Deny'
call(consent_path + '/' + receipt['id'], 'DELETE', {'requestId': 'ci-revoke-owner-change', 'expectedVersion': consent_version(customer)}, customer)
receipt = call(consent_path, 'POST', grant(customer, 'ci-consent-current'), customer)
compose('stop', 'consent')
evaluate(advisor, status=503)
call('/contracts/cid-042/v1', 'POST', envelope(advisor, 'SearchDocuments', search), advisor, status=503)
compose('start', 'consent')
ready()
assert evaluate(advisor)['outcome'] == 'Allow'
version = revision()
admin('customer-relationships/' + assignment['id'], 'DELETE', {'expectedPolicyVersion': version})
admin('customer-relationships/' + assignment['id'], 'DELETE', {'expectedPolicyVersion': version}, status=409)
evaluate(advisor, status=401)
advisor = login('A300')
assert evaluate(advisor)['reasonCode'] == 'CustomerRelationshipRequired'
assignment = admin('customer-relationships', 'POST', {'expectedPolicyVersion': revision(), 'actorId': 'A300', 'customerId': 'reference-customer'})
member('A300', None)
assert not admin('customer-relationships')['items']
member('A300', role)
assert evaluate(login('A300'))['reasonCode'] == 'CustomerRelationshipRequired'
compose('start', 'delivery-t001')
# Recreate the shared network namespace together, retaining named data volumes.
compose('down')
compose('up', '-d', '--no-build')
ready()
assert call('/uat-api/v1/setup', session=other_admin)['receiptReference'] == completed['receiptReference']
assert consent_version(customer) >= 7
assert sql('uat_consent_t001', 'SELECT (SELECT count(*) FROM consent.audit)=(SELECT count(*) FROM consent.outbox);') == 't'
assert sql('uat_consent_t001', "SELECT NOT has_table_privilege('uat_consent_t001_runtime','consent.audit','UPDATE,DELETE,TRUNCATE') AND NOT has_column_privilege('uat_consent_t001_runtime','consent.grants','expires_at','UPDATE');") == 't'
assert sql('uat_customer_identity_t001', "SELECT NOT has_table_privilege('uat_customer_identity_t001_runtime','customer_identity.customers','INSERT,UPDATE,DELETE,TRUNCATE');") == 't'

summary = {'checks': checks, 'scope': 'LOCAL/UAT onboarding', 'productionAccepted': False,
           'validated': ['setup review and replay', 'verified customer self-access', 'advisor relationship and Consent', 'document/report owner tampering',
                         'Consent atomic rollback', 'Consent expiry and revocation', 'owner version invalidation', 'owner outage', 'role-change cleanup', 'durable receipts and owner audit'],
           'limitations': ['Business data uses owner reference adapters.', 'AI and unsupported business consumers are not claimed.', 'Consent outbox delivery is not claimed.']}
(root / '.artifacts/onboarding-acceptance.json').write_text(json.dumps(summary, indent=2) + '\n')
print('Local onboarding owner/process acceptance passed:', checks, 'checks.')
