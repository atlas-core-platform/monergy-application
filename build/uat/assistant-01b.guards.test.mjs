import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, rmdirSync, symlinkSync, writeFileSync } from 'node:fs';
import { join, resolve, sep } from 'node:path';
import { privateOutputDirectory, requireNaturalExpiry, requireUnactivatedPlatform, scenarioCounts, writePrivateFailure } from './assistant-01b.guards.mjs';

const repository = resolve('.');
const privateRoot = resolve('.artifacts/assistant-01b-private-fixtures');
let folder = ''; let fixture = ''; let output = '';
const permissionScript = String.raw`
$ErrorActionPreference='Stop'
$path=$env:MONERGY_HARNESS_PERMISSION_PATH
if($IsWindows){
 $who=[Security.Principal.WindowsIdentity]::GetCurrent().User
 $system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
 $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
 $inherit=if($env:MONERGY_HARNESS_NO_INHERIT -eq 'true'){'None'}else{'ContainerInherit, ObjectInherit'}
 $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($who,'FullControl',$inherit,'None','Allow'))
 $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($system,'ReadAndExecute','ContainerInherit, ObjectInherit','None','Allow'))
 if($env:MONERGY_HARNESS_BROAD -eq 'true'){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'),'ReadAndExecute','ContainerInherit, ObjectInherit','None','Allow'))}
 Set-Acl -LiteralPath $path -AclObject $acl
}else{& chmod $(if($env:MONERGY_HARNESS_BROAD -eq 'true'){'755'}else{'700'}) $path;if($LASTEXITCODE -ne 0){throw 'chmod failed'}}
`;
function protect(path, broad = false, noInheritance = false) {
  execFileSync('pwsh', ['-NoProfile', '-Command', permissionScript], { stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, MONERGY_HARNESS_PERMISSION_PATH: path, MONERGY_HARNESS_BROAD: String(broad), MONERGY_HARNESS_NO_INHERIT: String(noInheritance) } });
}
function command(mode, path = output) {
  return execFileSync('pwsh', ['-NoProfile', '-File', resolve('build/uat/assistant-01b.private-artifacts.ps1'),
    '-Mode', mode, '-FixtureFile', fixture, ...(mode === 'Verify' ? ['-OutputDirectory', path] : [])],
  { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}
function ownerProtectedRead() {
  const script = String.raw`
$ErrorActionPreference='Stop'
. $env:MONERGY_HARNESS_OWNER_HELPER
try { Read-AssistantOwnerProtectedJson $env:MONERGY_HARNESS_OWNER_FIXTURE | ConvertTo-Json -Compress }
catch { Write-Output ('SANITIZED: '+$_.Exception.Message) }
`;
  return execFileSync('pwsh', ['-NoProfile', '-Command', script], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, MONERGY_HARNESS_OWNER_HELPER: resolve('build/uat/assistant-01b.owner-common.ps1'), MONERGY_HARNESS_OWNER_FIXTURE: fixture } }).trim();
}
before(() => {
  // This creates only an empty, clearly bounded harness fixture, never a LOCAL profile.
  mkdirSync(privateRoot, { recursive: true });
  folder = mkdtempSync(join(privateRoot, 'harness-guard-')); protect(folder);
  fixture = join(folder, 'fixture.json');
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- newly generated private synthetic fixture, contains no credentials.
  writeFileSync(fixture, '{}', { mode: 0o600 });
  output = command('Prepare');
});
after(() => {
  const target = resolve(folder);
  if (!target.startsWith(privateRoot + sep) || !target.includes('harness-guard-')) throw new Error('Guard fixture cleanup escaped its owned directory.');
  // Only this test's freshly generated, path-checked empty fixture is removed.
  rmSync(target, { recursive: true });
});

test('listing resolves an unused private destination without reading files or creating it', () => {
  const result = privateOutputDirectory({ listing: true, repository: join(folder, 'does-not-exist') });
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- read-only absence assertion inside this test's generated fixture.
  assert.equal(existsSync(result), false);
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- read-only absence assertion inside this test's generated fixture.
  assert.equal(existsSync(join(folder, 'does-not-exist')), false);
});
test('execution fails before artifact creation when private paths are absent', () => {
  assert.throws(() => privateOutputDirectory({ listing: false, repository }), /prepared protected fixture/);
});
test('actual protected directory passes configuration verification', () => {
  assert.equal(privateOutputDirectory({ listing: false, repository, fixturePath: fixture, outputPath: output }), output);
});
test('ordinary evidence directory cannot become raw browser output', () => {
  assert.throws(() => command('Verify', resolve('.artifacts/assistant-01b-testing/browser/transient')));
});
test('another fixture subtree cannot become this fixture output', () => {
  assert.throws(() => command('Verify', join(privateRoot, 'another-fixture', 'playwright-raw', 'a'.repeat(32))));
});
test('broad parent readability is rejected even when the child is protected', () => {
  const parent = join(folder, 'playwright-raw');
  protect(parent, true);
  try { assert.throws(() => command('Verify')); } finally { protect(parent); }
});
test('Windows raw parent must pass private write permissions to recreated children', { skip: process.platform !== 'win32' }, () => {
  const parent = join(folder, 'playwright-raw'); protect(parent, false, true);
  try { assert.throws(() => command('Verify')); } finally { protect(parent); }
});
test('Windows fixture parent must pass private permissions to secret-bearing lifecycle siblings', { skip: process.platform !== 'win32' }, () => {
  protect(folder, false, true);
  try { assert.throws(() => command('Verify')); } finally { protect(folder); }
});
test('recreated Playwright output inherits private permissions from its protected parent', () => {
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- verified empty generated raw-artifact child; models Playwright's recreation.
  rmdirSync(output);
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- same bounded generated child, no retained data.
  mkdirSync(output, { mode: 0o700 });
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- synthetic diagnostic only in the newly recreated private child.
  writeFileSync(join(output, 'error-context.md'), 'Synthetic harness diagnostic.', { mode: 0o600 });
  assert.equal(command('Verify'), output);
});
test('a linked raw output directory is rejected', () => {
  const target = join(folder, 'junction-target');
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- target exists only inside this test's generated private fixture.
  mkdirSync(target, { mode: 0o700 });
  const link = join(folder, 'playwright-raw', 'b'.repeat(32));
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- both ends belong to this fixture; verifies the guard rejects the link.
  symlinkSync(target, link, process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => command('Verify', link));
});
test('unactivated platform credentials permit the first welcome only', () => {
  requireUnactivatedPlatform({ activated: false, credentialVersion: 0, lockedUntil: null });
});
for (const [name, value] of [['active', { activated: true }], ['absent', {}], ['malformed', { activated: 'false' }], ['null', null]])
  test(`platform ${name} state refuses welcome issuance`, () => assert.throws(() => requireUnactivatedPlatform(value)));
test('serial skipped cases are reported separately from executed attempts', () => {
  assert.deepEqual(scenarioCounts([{ status: 'passed' }, { status: 'failed' }, { status: 'skipped' }, { status: 'interrupted' }]),
    { reported: 4, executed: 3, pass: 1, fail: 1, interrupted: 1, skipped: 1 });
});
test('timeout and interruption are counted explicitly without false pass', () => {
  assert.deepEqual(scenarioCounts([{ status: 'timedOut' }, { status: 'interrupted' }]),
    { reported: 2, executed: 2, pass: 0, fail: 1, interrupted: 1, skipped: 0 });
});

const ordinaryCapture = { capturedAt: '2026-11-19T10:00:00.000Z', expiresAt: '2026-11-19T10:30:00.000Z' };
test('natural expiry readback refuses a still-current owner session', () => {
  assert.throws(() => requireNaturalExpiry(ordinaryCapture, Date.parse('2026-11-19T10:29:59.000Z')), /not naturally expired/);
});
test('natural expiry readback requires a small real deadline margin', () => {
  assert.throws(() => requireNaturalExpiry(ordinaryCapture, Date.parse('2026-11-19T10:30:01.000Z')), /not naturally expired/);
});
test('natural expiry readback accepts the original thirty-minute deadline after it passes', () => {
  requireNaturalExpiry(ordinaryCapture, Date.parse('2026-11-19T10:30:02.000Z'));
});
test('artificially short session fixture cannot substitute for natural expiry', () => {
  assert.throws(() => requireNaturalExpiry({ ...ordinaryCapture, expiresAt: '2026-11-19T10:00:05.000Z' },
    Date.parse('2026-11-19T10:31:00.000Z')), /thirty-minute owner session/);
});
test('malformed expiry metadata fails closed', () => {
  assert.throws(() => requireNaturalExpiry({ ...ordinaryCapture, expiresAt: 'invalid' }, Date.now()), /thirty-minute owner session/);
});
test('owner protected JSON reader accepts the ordinary synthetic private fixture', () => {
  assert.equal(ownerProtectedRead(), '{}');
});
test('malformed protected JSON never exposes its input in direct helper diagnostics', () => {
  try {
    // eslint-disable-next-line security/detect-non-literal-fs-filename -- existing synthetic fixture inside this test-owned private directory.
    writeFileSync(fixture, '{"password":"HARNESS_PARSE_CANARY",BROKEN}', { mode: 0o600 });
    const result = ownerProtectedRead();
    assert.match(result, /^SANITIZED: Protected owner configuration/);
    assert.equal(result.includes('HARNESS_PARSE_CANARY'), false);
    assert.equal(result.includes('BROKEN'), false);
  } finally {
    // eslint-disable-next-line security/detect-non-literal-fs-filename -- restores only this test's synthetic empty fixture.
    writeFileSync(fixture, '{}', { mode: 0o600 });
  }
});
test('owner protected JSON reader rejects broad file permissions', () => {
  protect(folder, true);
  try { assert.match(ownerProtectedRead(), /^SANITIZED: Protected owner configuration/); } finally { protect(folder); }
});

test('failure diagnostic listing does not inspect paths or error content', () => {
  const result = writePrivateFailure({ listing: true, repository: join(folder,'not-created'),
    get errors() { throw new Error('Listing must not read failure content.'); } });
  assert.equal(result,null);
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- absence check beneath this generated synthetic fixture.
  assert.equal(existsSync(join(folder,'not-created')),false);
});
test('failure stack and nested cause are preserved only in the protected run directory', () => {
  const canary='PRIVATE_DIAGNOSTIC_CANARY';
  const captured=writePrivateFailure({ listing:false,repository,fixturePath:fixture,outputPath:output,
    testId:'D-PRIVATE-DIAGNOSTIC',status:'failed',errors:[{message:canary,stack:`Error: ${canary}\n at source.ts:42:7`,
      location:{file:'source.ts',line:42,column:7},cause:{message:'Nested synthetic failure'}}] });
  assert.ok(captured); assert.match(captured.filename,/^D-PRIVATE-DIAGNOSTIC-[a-f0-9-]{36}\.error\.json$/);
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- generated filename from the guard, within the protected synthetic output directory.
  const text=readFileSync(join(output,captured.filename),'utf8');
  const result=JSON.parse(text);
  assert.ok(result.errors[0].message===canary);
  assert.ok(result.errors[0].stack===`Error: ${canary}\n at source.ts:42:7`);
  assert.deepEqual(result.errors[0].location,{file:'source.ts',line:42,column:7});
  assert.equal(result.errors[0].cause.message,'Nested synthetic failure');
  assert.equal(result.truncated,false); assert.equal(captured.truncated,false);
  assert.equal(JSON.stringify(captured).includes(canary),false);
  assert.equal(command('Verify'),output);
});
test('failure diagnostics cannot be redirected into public evidence and errors stay sanitized', () => {
  const canary='PRIVATE_DIAGNOSTIC_CANARY';
  assert.throws(()=>writePrivateFailure({ listing:false,repository,fixturePath:fixture,
    outputPath:resolve('.artifacts/assistant-01b-testing/browser'),testId:'D-PRIVATE-DIAGNOSTIC',status:'failed',errors:[{message:canary}] }),
  error=>error instanceof Error && error.message==='Protected failure diagnostic could not be stored; private details are suppressed.');
});
test('oversized failure diagnostics remain bounded with explicit truncation', () => {
  const captured=writePrivateFailure({ listing:false,repository,fixturePath:fixture,outputPath:output,
    testId:'D-PRIVATE-BOUND',status:'timedOut',errors:Array.from({length:9},()=>({message:'x'.repeat(300000),stack:'y'.repeat(300000)})) });
  assert.ok(captured);
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- bounded generated diagnostic in this test's private output directory.
  const bytes=readFileSync(join(output,captured.filename)); const result=JSON.parse(bytes.toString());
  assert.ok(bytes.length<=1048576); assert.equal(result.errorCount,9); assert.equal(result.errors.length,8);
  assert.equal(result.truncated,true); assert.equal(captured.truncated,true);
});
test('unsafe diagnostic test IDs are rejected before creating a file', () => {
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- metadata-only inventory of this test's generated private output directory.
  const beforeFiles=readdirSync(output);
  assert.throws(()=>writePrivateFailure({ listing:false,repository,fixturePath:fixture,outputPath:output,
    testId:'../escape',status:'failed',errors:[{message:'Synthetic failure'}] }),/private details are suppressed/);
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- confirms no file was created in the test-owned output directory.
  assert.deepEqual(readdirSync(output),beforeFiles);
});
