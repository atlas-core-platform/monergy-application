import fs from 'node:fs';
import path from 'node:path';
const repositoryRoot = process.cwd();
const playwrightModule = process.env.MONERGY_PLAYWRIGHT_MODULE;
if (!playwrightModule) throw new Error('MONERGY_PLAYWRIGHT_MODULE is required.');
const { chromium } = await import(playwrightModule);
const base = 'http://127.0.0.1:4291';
const profile = JSON.parse(fs.readFileSync(path.join(repositoryRoot, '.artifacts/uat/p2n-admin-connectivity/profile.json'), 'utf8'));
const privateState = JSON.parse(fs.readFileSync(path.join(repositoryRoot, '.artifacts/cohort-n/private/registration-private.json'), 'utf8'));
const adminKey = profile.identities.find(value => value.tenantId === 'T001' && value.actorId === 'A900')?.token;
if (!adminKey) throw new Error('The protected A900 test key is unavailable.');

const executablePath = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
if (!fs.existsSync(executablePath)) throw new Error('Installed Google Chrome was not found.');
const browser = await chromium.launch({ executablePath, headless: true });
const results = [];
const scenarioFilter = process.env.MONERGY_CHROME_FILTER ?? '';

async function waitForText(page, value) {
  await page.getByText(value, { exact: false }).first().waitFor({ state: 'visible', timeout: 30_000 });
}
async function assertSecretFree(page, secrets) {
  const body = await page.locator('body').innerText();
  const storage = await page.evaluate(() => JSON.stringify({
    localStorage: Object.fromEntries(Object.entries(localStorage)),
    sessionStorage: Object.fromEntries(Object.entries(sessionStorage)),
  }));
  for (const secret of secrets) {
    if (secret && (body.includes(secret) || storage.includes(secret))) throw new Error('A protected credential appeared in rendered or browser storage state.');
  }
  if (/\b\d{6}\b/.test(storage)) throw new Error('A verification-code-shaped value appeared in browser storage.');
}
async function adminLogin(page, route, expectedHeading) {
  await page.goto(base + route, { waitUntil: 'domcontentloaded' });
  const local = page.getByRole('button', { name: 'Use a local test key' });
  const key = page.getByLabel('Local access key');
  await key.or(local).first().waitFor({ state: 'visible', timeout: 30_000 });
  if (!(await key.isVisible())) {
    try {
      await local.click({ force: true, timeout: 5_000 });
    } catch (error) {
      if (!(await key.isVisible())) throw error;
    }
    await key.waitFor({ state: 'visible', timeout: 30_000 });
  }
  await page.getByLabel('Organization').fill('T001');
  await key.fill(adminKey);
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  const setup = page.getByRole('heading', { name: 'Welcome to Monergy — Set up your organization' });
  const target = page.getByRole('heading', { name: expectedHeading, exact: true });
  await setup.or(target).first().waitFor({ state: 'visible', timeout: 30_000 });
  if (await setup.isVisible()) {
    await page.getByLabel('I have reviewed the organization details').check();
    await page.getByLabel('I understand how roles and customer access work').check();
    await page.getByRole('button', { name: 'Complete setup' }).click();
    const resume = page.getByRole('button', { name: 'Resume activation' });
    await resume.or(target).first().waitFor({ state: 'visible', timeout: 30_000 });
    if (await resume.isVisible()) {
      await resume.click();
      await setup.waitFor({ state: 'hidden', timeout: 30_000 });
      await target.waitFor({ state: 'visible', timeout: 30_000 });
    }
  }
}
async function customerLogin(page, route) {
  await page.goto(base + route, { waitUntil: 'domcontentloaded' });
  const registration = page.getByRole('button', { name: 'Sign in or create an account' });
  const email = page.getByLabel('Email');
  await email.or(registration).first().waitFor({ state: 'visible', timeout: 30_000 });
  if (!(await email.isVisible())) {
    await registration.click({ force: true });
    await email.waitFor({ state: 'visible', timeout: 30_000 });
  }
  await page.getByRole('heading', { name: 'Sign in with email' }).waitFor({ state: 'visible' });
  await page.getByLabel('Organization').fill('T001');
  await email.fill(privateState.Email);
  await page.getByLabel('Password').fill(privateState.Password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
}
async function scenario(name, run) {
  if (scenarioFilter && !name.includes(scenarioFilter)) return;
  const started = Date.now();
  const context = await browser.newContext({ locale: 'en-IN', timezoneId: 'Asia/Calcutta' });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await run(page);
    if (errors.length) throw new Error('The page emitted an unhandled browser error.');
    results.push({ name, status: 'PASS', durationMs: Date.now() - started });
  } catch (error) {
    const visible = (await page.locator('body').innerText().catch(() => ''))
      .replaceAll(adminKey, '[redacted]')
      .replaceAll(privateState.Password, '[redacted]')
      .replaceAll(privateState.SessionId, '[redacted]')
      .slice(0, 1200);
    const alerts = await page.getByRole('alert').allInnerTexts().catch(() => []);
    results.push({ name, status: 'FAIL', durationMs: Date.now() - started,
      error: error instanceof Error ? error.message : String(error), alerts, visible });
  } finally {
    await context.close();
  }
}

await scenario('T1-91 Customer Management', async page => {
  await adminLogin(page, '/access/customers', 'Customers');
  await page.getByRole('heading', { name: 'Customers', exact: true }).waitFor({ state: 'visible', timeout: 30_000 });
  await waitForText(page, 'Cohort N Registered Customer');
  await waitForText(page, 'Suspension stops customer workspace access');
  await assertSecretFree(page, [adminKey, privateState.Password, privateState.SessionId]);
});

await scenario('T1-92 Processing Status', async page => {
  await adminLogin(page, '/access/processing', 'Processing Status');
  await page.getByRole('heading', { name: 'Processing Status' }).waitFor({ state: 'visible', timeout: 30_000 });
  await waitForText(page, 'Review jobs recorded for your organization');
  if (await page.locator('tbody tr').count() < 1) throw new Error('No physical processing record was rendered.');
  await assertSecretFree(page, [adminKey, privateState.Password, privateState.SessionId]);
});

await scenario('T1-93 Document Monitoring', async page => {
  await adminLogin(page, '/access/documents', 'Document Monitoring');
  await page.getByRole('heading', { name: 'Document Monitoring' }).waitFor({ state: 'visible', timeout: 30_000 });
  await waitForText(page, 'Verified at intake');
  await waitForText(page, 'google-statement.txt');
  await waitForText(page, 'onedrive-statement.txt');
  await assertSecretFree(page, [adminKey, privateState.Password, privateState.SessionId]);
});

await scenario('T1-94 Audit Log', async page => {
  await adminLogin(page, '/access/audit', 'Audit Log');
  await page.getByRole('heading', { name: 'Audit Log' }).waitFor({ state: 'visible', timeout: 30_000 });
  await waitForText(page, 'metadata, not documents or financial values');
  await waitForText(page, 'matching records');
  await assertSecretFree(page, [adminKey, privateState.Password, privateState.SessionId]);
});

await scenario('T1-95 Customer Registration and recovered sign-in', async page => {
  await customerLogin(page, '/');
  await page.getByRole('heading', { name: /Good (morning|to see you)\./ }).waitFor({ state: 'visible', timeout: 30_000 });
  await assertSecretFree(page, [adminKey, privateState.Password, privateState.SessionId]);
});

await scenario('T1-96 Google Drive LOCAL', async page => {
  await customerLogin(page, '/connections/google-drive');
  await page.getByRole('heading', { name: 'Google Drive' }).waitFor({ state: 'visible', timeout: 30_000 });
  await waitForText(page, 'Disconnected');
  await waitForText(page, 'Imported documents');
  await waitForText(page, 'google-statement.txt');
  await assertSecretFree(page, [adminKey, privateState.Password, privateState.SessionId]);
});

await scenario('T1-97 OneDrive LOCAL', async page => {
  await customerLogin(page, '/connections/onedrive');
  await page.getByRole('heading', { name: 'OneDrive' }).waitFor({ state: 'visible', timeout: 30_000 });
  await waitForText(page, 'Disconnected');
  await waitForText(page, 'Imported documents');
  await waitForText(page, 'onedrive-statement.txt');
  await assertSecretFree(page, [adminKey, privateState.Password, privateState.SessionId]);
});

const failed = results.filter(value => value.status === 'FAIL');
const evidence = {
  recordedAt: new Date().toISOString(),
  browser: 'Google Chrome',
  browserVersion: browser.version(),
  executablePath,
  installedChannel: true,
  executed: results.length,
  passed: results.length - failed.length,
  failed: failed.length,
  blocked: 0,
  scenarios: results,
};
await browser.close();
const output = path.join(repositoryRoot, '.artifacts/cohort-n/chrome-evidence.json');
fs.writeFileSync(output, JSON.stringify(evidence, null, 2), 'utf8');
process.stdout.write(JSON.stringify(evidence, null, 2) + '\n');
if (failed.length) process.exitCode = 1;
