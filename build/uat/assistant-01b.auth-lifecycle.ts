import { randomBytes, randomUUID } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { expect, type Browser, type Page } from '@playwright/test';
import { base, fixture, headers, login, open, origin, person, type Fixture } from './assistant-01b.fixture';
import { requireNaturalExpiry } from './assistant-01b.guards.mjs';

type PrivateLifecycleFile = 'auth-expiry.json' | 'auth-expiry-retry.json' |
  'auth-recovery.json' | 'auth-recovery-a600.json' | 'auth-recovery-disposable.json' |
  'auth-recovery-remediation.json' | 'auth-recovery-final.json' | 'fixture.json';

// These paths are reachable only after the runner's private ancestor/ACL guard.
// New profile/instance binding is independently checked by the lazy fixture loader.
function privateFile(name: PrivateLifecycleFile) {
  const actual = resolve(process.env.MONERGY_ASSISTANT_UAT_FIXTURE ?? 'FIXTURE_REQUIRED');
  if (!fixture.instanceId || !actual.endsWith('fixture.json')) throw new Error('Private fixture unavailable.');
  const file = resolve(dirname(actual), name);
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- fixed filename under the verified matching private parent.
  if (existsSync(file) && (!lstatSync(file).isFile() || lstatSync(file).isSymbolicLink()))
    throw new Error('An ordinary private lifecycle file is required.');
  return file;
}
function readPrivate(name: PrivateLifecycleFile): unknown {
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- closed private filename and verified parent.
  return JSON.parse(readFileSync(privateFile(name), 'utf8')) as unknown;
}
function savePrivate(name: PrivateLifecycleFile, value: unknown) {
  const file = privateFile(name);
  const temporary = `${file}.${randomUUID()}.pending`;
  // Keep the previous record intact if writing fails. Both copies remain private.
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- random sibling in private parent; contains secrets only when required by this new-profile journal.
  writeFileSync(temporary, JSON.stringify(value), { mode: 0o600, flag: 'wx', flush: true });
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- atomic replacement of the same guarded private record; no evidence export.
  renameSync(temporary, file);
}
function createPrivate(name: Exclude<PrivateLifecycleFile, 'fixture.json'>, value: unknown) {
  // Exclusive first publication: a concurrent phase cannot overwrite a journal
  // and then dispatch a second credential mutation. Its losing write stops here.
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- fixed filename under verified inheritable private ACLs; wx refuses an existing or linked record.
  writeFileSync(privateFile(name), JSON.stringify(value), { mode: 0o600, flag: 'wx', flush: true });
}
function requireAbsent(name: Exclude<PrivateLifecycleFile, 'fixture.json'>) {
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- read-only guard against resetting an existing lifecycle run.
  if (existsSync(privateFile(name))) throw new Error('This lifecycle fixture already exists. Preserve it; do not repeat or rotate credentials automatically.');
}
interface ExpiryCapture {
  schemaVersion: number; profile: string; instanceId: string; actor: string; tenant: string;
  session: string; expiresAt: string; capturedAt: string; run?: 'initial' | 'retry';
}

function expiryFile(): 'auth-expiry.json' | 'auth-expiry-retry.json' {
  const run = process.env.MONERGY_ASSISTANT_EXPIRY_RUN ?? 'initial';
  if (run !== 'initial' && run !== 'retry') throw new Error('Unknown bounded natural-expiry run.');
  return run === 'retry' ? 'auth-expiry-retry.json' : 'auth-expiry.json';
}

function recoverySelection() {
  const actor = process.env.MONERGY_ASSISTANT_RECOVERY_ACTOR ?? 'A800';
  if (actor !== 'A600' && actor !== 'ci-60000000000000000000000000000001' &&
      actor !== 'ci-60000000000000000000000000000002' &&
      actor !== 'ci-60000000000000000000000000000003' && actor !== 'A800')
    throw new Error('Unknown bounded recovery actor.');
  return { actor, file: actor === 'A600' ? 'auth-recovery-a600.json' as const :
    actor.endsWith('0001') ? 'auth-recovery-disposable.json' as const :
      actor.endsWith('0002') ? 'auth-recovery-remediation.json' as const :
        actor.endsWith('0003') ? 'auth-recovery-final.json' as const : 'auth-recovery.json' as const };
}

export async function captureNaturalExpiry(page: Page) {
  const file = expiryFile();
  requireAbsent(file);
  const selected = person('A700', 'T001');
  const signedIn = page.waitForResponse(response => new URL(response.url()).pathname ===
    '/identity-api/local/v1/persona-credentials/sign-in' && response.request().method() === 'POST');
  const session = await login(page, selected);
  const response = await signedIn;
  const body = await response.json() as { authenticationContextId: string; expiresAt: string };
  expect(body.authenticationContextId).toBe(session);
  const remaining = Date.parse(body.expiresAt) - Date.now();
  expect(remaining).toBeGreaterThan(20 * 60_000);
  expect(remaining).toBeLessThanOrEqual(31 * 60_000);
  expect((await page.request.get(origin(selected) + base(selected) + '/status', { headers: headers(selected, session) })).status()).toBe(200);
  const capture: ExpiryCapture = { schemaVersion: 1, profile: fixture.profile, instanceId: fixture.instanceId,
    actor: 'A700', tenant: 'T001', session, expiresAt: body.expiresAt, capturedAt: new Date().toISOString(),
    run: file === 'auth-expiry-retry.json' ? 'retry' : 'initial' };
  createPrivate(file, capture);
  return capture;
}

export async function verifyLiveNaturalExpiry(page: Page, setTimeout: (milliseconds: number) => void) {
  const capture = await captureNaturalExpiry(page);
  const remaining = Date.parse(capture.expiresAt) - Date.now();
  setTimeout(Math.min(31 * 60_000, remaining + 60_000));
  await open(page);
  const canary = 'Temporary natural expiry lifecycle canary';
  await page.locator('#assistant-question').fill(canary);
  // A new inherently thirty-minute physical test. No fake clock, owner TTL,
  // database write or fabricated session is involved. Other phases can run in
  // another process while this real browser and owner session remain alive.
  await page.waitForTimeout(Math.max(0, Date.parse(capture.expiresAt) - Date.now() - 5000));
  expect(Date.now()).toBeLessThan(Date.parse(capture.expiresAt));
  await expect(page.locator('#assistant-question')).toHaveValue(canary);
  await expect(page.locator('#assistant-question')).toHaveCount(0, { timeout: 15_000 });
  await expect(page.getByRole('button', { name: 'Ask Monergy', exact: true })).toHaveCount(0);
  await expect.poll(() => Date.now(), { timeout: 15_000 }).toBeGreaterThan(Date.parse(capture.expiresAt) + 1000);
  requireNaturalExpiry(capture, Date.now());
  const selected = person('A700', 'T001');
  for (const suffix of ['/status', '/answers']) {
    const response = await page.request.fetch(origin(selected) + base(selected) + suffix, {
      method: suffix === '/answers' ? 'POST' : 'GET', headers: headers(selected, capture.session), maxRedirects: 0,
      ...(suffix === '/answers' ? { data: { contractVersion: '1.0.0', question: 'How does Consent work?' } } : {}),
    });
    expect(response.status()).toBe(401);
  }
}

export async function verifyNaturalExpiry(page: Page) {
  const file = expiryFile();
  const capture = readPrivate(file) as ExpiryCapture;
  if (capture.schemaVersion !== 1 || capture.profile !== fixture.profile || capture.instanceId !== fixture.instanceId ||
      capture.actor !== 'A700' || capture.tenant !== 'T001' || !/^[a-f0-9]{64}$/.test(capture.session) ||
      (file === 'auth-expiry-retry.json' ? capture.run !== 'retry' : capture.run !== undefined && capture.run !== 'initial'))
    throw new Error('Expiry capture does not belong to this isolated fixture.');
  requireNaturalExpiry(capture, Date.now());
  const selected = person('A700', 'T001');
  for (const suffix of ['/status', '/answers']) {
    const response = await page.request.fetch(origin(selected) + base(selected) + suffix, {
      method: suffix === '/answers' ? 'POST' : 'GET', headers: headers(selected, capture.session),
      ...(suffix === '/answers' ? { data: { contractVersion: '1.0.0', question: 'How does Consent work?' } } : {}),
      maxRedirects: 0,
    });
    expect(response.status(), 'Actual expired owner session must be denied').toBe(401);
    expect(await response.text()).not.toContain('Reviewed guidance');
  }
  // This is a fresh normal login, never an expired-session storage injection.
  await login(page, selected); await open(page);
  await expect(page.locator('#assistant-question')).toHaveValue('');
}

export async function verifyOwnPasswordRecovery(browser: Browser, page: Page) {
  const selection = recoverySelection();
  requireAbsent(selection.file);
  const selected = selection.actor.startsWith('ci-')
    ? { ...person('A600', 'T001'), actor: selection.actor,
        email: selection.actor.endsWith('0001') ? 'disposable-recovery@example.test' :
          selection.actor.endsWith('0002') ? 'disposable-recovery-2@example.test' : 'disposable-recovery-3@example.test' }
    : person(selection.actor, 'T001');
  const oldSession = await login(page, selected); await open(page);
  await page.locator('#assistant-question').fill('Temporary private recovery lifecycle canary');
  const journal = { schemaVersion: 1, profile: fixture.profile, instanceId: fixture.instanceId,
    actor: selection.actor, tenant: 'T001', oldPassword: selected.password,
    newPassword: `Aa9!${randomBytes(24).toString('base64url')}`, oldSession,
    requestId: randomUUID().replaceAll('-', ''), state: 'Prepared', welcomePath: '', updatedAt: new Date().toISOString() };
  // Persist both possible credentials BEFORE requesting/consuming the challenge.
  // A lost reply is indeterminate; never issue another reset to make a test green.
  createPrivate(selection.file, journal);
  function advance(expectedState: string, state: string) {
    const current = readPrivate(selection.file) as typeof journal;
    if (current.profile !== journal.profile || current.instanceId !== journal.instanceId ||
        current.actor !== journal.actor || current.tenant !== journal.tenant || current.requestId !== journal.requestId ||
        current.state !== expectedState || current.newPassword !== journal.newPassword || current.oldPassword !== journal.oldPassword)
      throw new Error('The private recovery journal changed. Preserve both credential records and stop.');
    journal.state = state; journal.updatedAt = new Date().toISOString(); savePrivate(selection.file, journal);
  }
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- exact profile path already bound to this new fixture instance.
  const profile = JSON.parse(readFileSync(fixture.profilePath, 'utf8')) as { tokens: { personaTesterAccess: string } };
  const recoveryContext = await browser.newContext({ serviceWorkers: 'block' });
  try {
    const recoveryPage = await recoveryContext.newPage();
    const issued = await recoveryPage.request.post(origin(selected) + '/identity-api/local/v1/persona-credentials/tester/recovery-link', {
      headers: { 'X-Monergy-Tester-Activation': Buffer.from(profile.tokens.personaTesterAccess, 'hex').toString('base64') },
      data: { tenantId: 'T001', email: selected.email, returnPath: selected.path, requestId: journal.requestId }, maxRedirects: 0,
    });
    expect(issued.status()).toBe(200);
    const issuedBody = await issued.json() as { welcomePath: string };
    const fragment = issuedBody.welcomePath.startsWith('/welcome#')
      ? new URLSearchParams(issuedBody.welcomePath.slice('/welcome#'.length)) : new URLSearchParams();
    if ([...fragment.keys()].length !== 4 || fragment.get('tenant') !== 'T001' ||
        !/^[a-f0-9]{64}$/.test(fragment.get('ticket') ?? '') || fragment.get('return') !== selected.path ||
        fragment.get('mode') !== 'recovery')
      throw new Error('The recovery owner returned an unexpected bound welcome path.');
    journal.welcomePath = issuedBody.welcomePath; advance('Prepared', 'LinkIssued');
    await recoveryPage.goto(origin(selected) + journal.welcomePath);
    await expect(recoveryPage.getByText('Choose a new password. This ends your previous sessions;', { exact: false })).toBeVisible();
    await recoveryPage.getByLabel('Password (15–128 characters)', { exact: true }).fill(journal.newPassword);
    await recoveryPage.getByLabel('Confirm password', { exact: true }).fill(journal.newPassword);
    let confirmationAttempts = 0;
    const confirmationBodies: string[] = [];
    let committedResponseWithheld = false;
    if (selection.actor.startsWith('ci-')) {
      await recoveryPage.route('**/identity-api/local/v1/persona-credentials/tester/recovery/confirm', async route => {
        confirmationAttempts++;
        confirmationBodies.push(route.request().postData() ?? '');
        if (confirmationAttempts === 1) {
          const response = await route.fetch();
          expect(response.status(), 'Owner committed before the first acknowledgement was withheld').toBe(200);
          committedResponseWithheld = true;
          await route.fulfill({ status: 503, contentType: 'application/problem+json',
            body: JSON.stringify({ error: 'AUDIT_OWNER_UNAVAILABLE' }) });
          return;
        }
        await route.continue();
      });
    }
    const committed = selection.actor.startsWith('ci-') ? undefined : recoveryPage.waitForResponse(response =>
      new URL(response.url()).pathname === '/identity-api/local/v1/persona-credentials/tester/recovery/confirm' &&
      response.request().method() === 'POST');
    advance('LinkIssued', 'ConfirmationDispatched');
    await recoveryPage.getByRole('button', { name: 'Set password', exact: true }).click();
    if (selection.actor.startsWith('ci-')) {
      await expect(recoveryPage.getByText('Your account is ready. Continue to sign in.', { exact: true })).toBeVisible();
      expect(committedResponseWithheld).toBe(true);
      expect(confirmationAttempts).toBe(2);
      expect(confirmationBodies[0]).toBe(confirmationBodies[1]);
    } else {
      const confirmation = await committed!;
      expect(confirmation.status()).toBe(200);
      expect(await confirmation.json()).toEqual({ recovered: true });
    }
    advance('ConfirmationDispatched', 'PasswordConfirmed');
    const stored = readPrivate('fixture.json') as Fixture;
    if (stored.instanceId !== fixture.instanceId || stored.profile !== fixture.profile)
      throw new Error('Credential fixture changed during recovery; both possible credentials remain in the private journal.');
    const original = stored.personas.find(value => value.actor ===
      (selection.actor.startsWith('ci-') ? 'A600' : selection.actor) && value.tenant === 'T001');
    if (original?.password !== journal.oldPassword)
      throw new Error('A concurrent credential fixture change cannot be overwritten.');
    if (!selection.actor.startsWith('ci-')) {
      original.password = journal.newPassword;
      savePrivate('fixture.json', stored);
    }
    selected.password = journal.newPassword;
    advance('PasswordConfirmed', 'FixtureUpdated');
    await expect(recoveryPage.getByRole('button', { name: 'Ask Monergy', exact: true })).toHaveCount(0);
    expect((await page.request.get(origin(selected) + base(selected) + '/status', { headers: headers(selected, oldSession) })).status()).toBe(401);
    // Actual owner revocation is observed by the live drawer's status refresh.
    await expect(page.locator('#assistant-question')).toHaveCount(0, { timeout: 35_000 });
    await expect(page.getByRole('button', { name: 'Ask Monergy', exact: true })).toHaveCount(0);
    const oldPassword = await recoveryPage.request.post(origin(selected) + '/identity-api/local/v1/persona-credentials/sign-in', {
      data: { tenantId: 'T001', email: selected.email, password: journal.oldPassword }, maxRedirects: 0,
    });
    expect(oldPassword.status()).toBe(401);
    await login(recoveryPage, selected); await open(recoveryPage);
    await expect(recoveryPage.locator('#assistant-question')).toHaveValue('');
    advance('FixtureUpdated', 'Verified');
  } finally { await recoveryContext.close(); }
}
