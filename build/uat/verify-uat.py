"""Destructive acceptance checks for an explicitly disposable CI UAT profile only.
Never run against the operator's retained default profile. No credentials in evidence.
"""
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
assert os.environ.get('MONERGY_UAT_DISPOSABLE') == profile['instanceId'], 'Explicit disposable instance authorization required'
base = 'http://127.0.0.1:' + str(profile['port'])
checks = 0

def call(path, method='GET', body=None, session=None, headers=None, status=200):
    global checks
    fields = {'Content-Type': 'application/json', **(headers or {})}
    if session:
        fields.update({'X-Monergy-Tenant': session['tenantId'], 'X-Monergy-Session': session['authenticationContextId']})
    request = urllib.request.Request(base + path, method=method, headers=fields,
        data=None if body is None else json.dumps(body).encode())
    try:
        response = urllib.request.urlopen(request, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    assert response.status == status, (path, response.status, status)
    checks += 1
    raw = response.read()
    return json.loads(raw) if raw and response.headers.get('Content-Type', '').startswith('application/json') else None

def login(tenant='T001', actor='A900'):
    identity = next(item for item in profile['identities'] if item['tenantId'] == tenant and item['actorId'] == actor)
    return call('/identity-api/local/v1/tenant-sessions', 'POST', {'tenantId': tenant}, headers={'X-Monergy-Reference-Authentication': identity['token']})

def admin(path, method='GET', body=None, session=None, status=200):
    return call('/access-api/v1/administration/' + path, method, body, session or owner, status=status)

def revision():
    return admin('context')['policyVersion']

def compose(*args):
    subprocess.run(['docker','compose','--env-file',str(folder/'compose.env'),'-f',str(root/'build/uat/compose.yml'),
                    '--project-name','monergy-uat-'+profile['instanceId'][:8],*args], check=True, timeout=180, stdout=subprocess.DEVNULL)

def ready():
    for _ in range(90):
        try:
            call('/health/ready')
            return
        except (AssertionError, urllib.error.URLError, TimeoutError):
            time.sleep(1)
    raise AssertionError('UAT readiness timed out')

def contract(session, customer='reference-customer', forged_actor=None):
    return {'contractName':'SearchDocuments','contractVersion':'1.0.0','requestId':'uat-search','correlationId':'uat-search',
        'causationId':None,'idempotencyKey':None,
        'security':{'actor':{'actorId':forged_actor or session['actorId'],'actorType':'HUMAN','authenticatedAt':'2026-10-08T00:00:00Z','authenticationContextId':session['authenticationContextId']},
                    'workload':{'workloadId':'search-retrieval','workloadIdentityId':'reference-workload'},
                    'access':{'purpose':'REFERENCE_SEARCH','consentReferenceId':None,'authorizationContextId':'reference-authorization','customerId':customer}},
        'payload':{'customerId':customer,'query':'income','matchMode':'Lexical','limit':20}}

ready()
owner = login()
other = login('T002')
assert admin('context')['actorId'] == 'A900'
call('/uat-api/v1/topology', status=401)
services = call('/uat-api/v1/topology', session=owner)['services']
assert len(services) == 13 and all(item['available'] for item in services)
assert call('/uat-api/v1/readiness', session=owner)['productionAccepted'] is False
call('/internal/v1/tenant-sessions/validate', 'POST', {}, status=404)
call('/reference/events/audit', 'POST', {}, status=404)
call('/access-api/v1/administration/context', session={**owner, 'tenantId':'T002'}, status=401)
call('/access-api/v1/administration/context', session=owner, headers={'Origin':'https://untrusted.example'}, status=403)
for actor in ('A100','A300'):
    if not any(row.get('actorId') == actor for row in admin('members')['items']):
        admin('members','POST',{'expectedPolicyVersion':revision(),'actorId':actor})
no_role = login(actor='A300')
for path in ('/identity-api/local/v1/administration/sessions','/audit-api/local/v1/administration/access-events','/uat-api/v1/topology'):
    call(path, session=no_role, status=403)
catalog = call('/access-api/v1/capabilities', session=owner)['capabilities']
permission = admin('permissions','POST',{'expectedPolicyVersion':revision(),'code':'uat-all-boundaries','label':'Disposable UAT capabilities',
                  'grants':[{'capabilityId':item['capabilityId'],'scope':'Tenant'} for item in catalog]})
role = admin('roles','POST',{'expectedPolicyVersion':revision(),'code':'uat-boundary-tester','label':'Disposable UAT tester','permissionIds':[permission['id']]})
admin('members/A100','PUT',{'expectedPolicyVersion':revision(),'businessRoleId':role['id'],'active':True,'tenantAdmin':False})
analyst = login(actor='A100')
for service in services:
    if service['id'] == 'access-management':
        evaluation = {'capabilityId':'search.query.execute','resourceType':None,'resourceId':None}
        assert call('/access-api/v1/authorization/evaluations','POST',evaluation,analyst)['outcome'] == 'Allow'
        assert call('/access-api/v1/authorization/evaluations','POST',evaluation,owner)['outcome'] == 'Deny'
        continue
    prefix = 'search' if service['id'] == 'search-retrieval' else service['id']
    cap = next(item for item in catalog if item['service'] == prefix)
    body = {'capabilityId':cap['capabilityId'],'resourceType':cap['resourceType'],'resourceId':'uat-resource' if cap['resourceType'] else None}
    path = '/uat-api/v1/services/'+service['id']+'/access-check'
    call(path,'POST',body,status=401)
    allowed = call(path,'POST',body,analyst)
    assert allowed['decision']['outcome'] == 'Allow' and allowed['operationExecuted'] is False
    assert call(path,'POST',body,owner,status=403)['decision']['outcome'] == 'Deny'
    call(path,'POST',body,{**analyst,'tenantId':'T002'},status=401)
# Actual owner contract, not just a boundary probe; forged actor and customer must fail before owner execution.
call('/contracts/cid-042/v1','POST',contract(analyst),analyst)
call('/contracts/cid-042/v1','POST',contract(analyst,forged_actor='A900'),analyst,status=403)
call('/contracts/cid-042/v1','POST',contract(analyst,customer='other-customer'),analyst,status=403)
call('/contracts/cid-042/v1','POST',contract(analyst),owner,status=403)
# Explicit revocation is immediate, even with event delivery stopped.
compose('stop','delivery-t001')
version=revision()
admin('members/A100/revoke-sessions','POST',{'expectedPolicyVersion':version})
admin('members/A100/revoke-sessions','POST',{'expectedPolicyVersion':version},status=409)
call('/contracts/cid-042/v1','POST',contract(analyst),analyst,status=401)
compose('start','delivery-t001')
analyst=login(actor='A100')
assert admin('context',session=other)['tenantId']=='T002'
# Restart actual owners and dispatchers. Sessions, policy and audit must survive.
compose('restart','access-management','customer-identity','audit','delivery-t001','delivery-t002')
ready()
call('/contracts/cid-042/v1','POST',contract(analyst),analyst)
for _ in range(45):
    events=call('/audit-api/local/v1/administration/access-events',session=owner)['items']
    if any(item['event']['operation']=='member.sessions-revoked' for item in events): break
    time.sleep(1)
else: raise AssertionError('Revocation did not reach canonical Audit')
assert all(item['event']['tenantId']=='T001' for item in events)
assert all(item['event']['tenantId']=='T002' for item in call('/audit-api/local/v1/administration/access-events',session=other)['items'])
sessions=call('/identity-api/local/v1/administration/sessions',session=owner)['items']
assert any(row['actorId']=='A100' and row['revoked'] for row in sessions)
assert all(len(row['sessionReference'])==64 and 'authenticationContextId' not in row for row in sessions)
# Live authority outage fails closed. Recovery does not require a new deployment.
compose('stop','access-management')
call('/identity-api/local/v1/administration/sessions',session=owner,status=503)
call('/contracts/cid-042/v1','POST',contract(analyst),analyst,status=503)
compose('start','access-management'); ready()
# Disable membership; the existing session and fresh authentication are both rejected.
admin('members/A100','PUT',{'expectedPolicyVersion':revision(),'businessRoleId':role['id'],'active':False,'tenantAdmin':False})
call('/contracts/cid-042/v1','POST',contract(analyst),analyst,status=401)
identity=next(item for item in profile['identities'] if item['tenantId']=='T001' and item['actorId']=='A100')
call('/identity-api/local/v1/tenant-sessions','POST',{'tenantId':'T001'},headers={'X-Monergy-Reference-Authentication':identity['token']},status=401)
# Keep the disposable administrator available for the browser acceptance journey.
# All selected local compositions reject Production before binding their service ports.
for service in services:
    result=subprocess.run(['docker','compose','--env-file',str(folder/'compose.env'),'-f',str(root/'build/uat/compose.yml'),
        '--project-name','monergy-uat-'+profile['instanceId'][:8],'run','--rm','--no-deps',
        '-e','ASPNETCORE_ENVIRONMENT=Production','-e','DOTNET_ENVIRONMENT=Production',
        '-e','Monergy__TenantBoundary__Enabled=true',service['id']],
        capture_output=True,timeout=30)
    assert result.returncode != 0, 'Local composition accepted Production: '+service['id']
    checks += 1
summary={'checks':checks,'services':13,'actualContract':'SearchDocuments','productionAccepted':False,
         'validated':['live authorization','tenant isolation','admin authority','scope checks','revocation','disablement','durable audit','restart recovery','owner outage','gateway allowlist']}
(root/'.artifacts/uat-acceptance.json').write_text(json.dumps(summary,indent=2)+'\n')
print('Local UAT owner/process acceptance passed:',checks,'checks across 13 boundaries.')
