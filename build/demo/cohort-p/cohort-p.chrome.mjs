import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const playwrightModule = process.env.MONERGY_PLAYWRIGHT_MODULE;
if (!playwrightModule) throw new Error('MONERGY_PLAYWRIGHT_MODULE is required.');
const playwright = await import(playwrightModule);
const chromium = playwright.chromium ?? playwright.default?.chromium;
if (!chromium) throw new Error('Playwright Chromium is unavailable.');

const base = 'http://127.0.0.1:4311';
const profile = JSON.parse(fs.readFileSync(path.join(root, '.artifacts/uat/p2p-durable-documents/profile.json'), 'utf8'));
const token = actorId => profile.identities.find(value => value.tenantId === 'T001' && value.actorId === actorId)?.token;
const customerKey = token('A100');
const adminKey = token('A900');
if (!customerKey || !adminKey) throw new Error('The protected Cohort P LOCAL test identities are unavailable.');

const executablePath = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
if (!fs.existsSync(executablePath)) throw new Error('Installed Google Chrome was not found.');
const browser = await chromium.launch({ executablePath, headless: true });
const results = [];
const scenarioFilter = process.env.MONERGY_CHROME_FILTER ?? '';
const shared = { fileName: `cohort-p-chrome-unknown-${Date.now()}.txt`, versionId: '', sha256: '', storageVersion: '' };
const uploadDirectory = path.join(root, '.artifacts', 'cohort-p', 'chrome');
fs.mkdirSync(uploadDirectory, { recursive: true });
const uploadPath = path.join(uploadDirectory, shared.fileName);
fs.writeFileSync(uploadPath, 'Cohort P independent classification browser fixture.\nNo supported financial heading is present.\n', 'utf8');

async function heading(page, name) {
  await page.getByRole('heading', { name, exact: typeof name === 'string' }).first().waitFor({ state: 'visible', timeout: 30_000 });
}
async function visible(page, text) {
  await page.getByText(text, { exact: false }).first().waitFor({ state: 'visible', timeout: 30_000 });
}
async function localLogin(page, actorId, route, expectedHeading) {
  await page.goto(base + route, { waitUntil: 'domcontentloaded' });
  const local = page.getByRole('button', { name: 'Use a local test key' });
  const key = page.getByLabel('Local access key');
  await key.or(local).first().waitFor({ state: 'visible', timeout: 30_000 });
  if (!(await key.isVisible())) {
    await local.last().click();
    await key.waitFor({ state: 'visible', timeout: 30_000 });
  }
  await page.getByLabel('Organization').fill('T001');
  await key.fill(actorId === 'A900' ? adminKey : customerKey);
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  const setup = page.getByRole('heading', { name: 'Welcome to Monergy — Set up your organization' });
  const target = page.getByRole('heading', { name: expectedHeading, exact: typeof expectedHeading === 'string' });
  await setup.or(target).first().waitFor({ state: 'visible', timeout: 30_000 });
  if (await setup.isVisible()) {
    const reviewed = page.getByLabel('I have reviewed the organization details');
    await reviewed.or(target).first().waitFor({ state: 'visible', timeout: 30_000 });
    if (!(await target.isVisible())) {
      await reviewed.check();
      await page.getByLabel('I understand how roles and customer access work').check();
      await page.getByRole('button', { name: 'Complete setup' }).click();
      const resume = page.getByRole('button', { name: 'Resume activation' });
      await resume.or(target).first().waitFor({ state: 'visible', timeout: 30_000 });
      if (await resume.isVisible()) await resume.click();
    }
  }
  await heading(page, expectedHeading);
}
async function secretFree(page) {
  const body = await page.locator('body').innerText();
  const storage = await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));
  for (const secret of [customerKey, adminKey]) {
    if (body.includes(secret) || storage.includes(secret)) throw new Error('A protected LOCAL test key appeared in rendered or browser storage state.');
  }
  if (/authorization|bearer\s|authenticationContextId/i.test(storage)) throw new Error('Protected session material appeared in browser storage.');
}
async function scenario(name, run) {
  if (scenarioFilter && !name.startsWith(scenarioFilter)) return;
  const started = Date.now();
  const context = await browser.newContext({ locale: 'en-IN', timezoneId: 'Asia/Calcutta' });
  const page = await context.newPage();
  const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  try {
    await run(page);
    if (pageErrors.length) throw new Error('The page emitted an unhandled browser error.');
    await secretFree(page);
    results.push({ name, status: 'PASS', durationMs: Date.now() - started });
  } catch (error) {
    const rendered = (await page.locator('body').innerText().catch(() => ''))
      .replaceAll(customerKey, '[redacted]').replaceAll(adminKey, '[redacted]').slice(0, 1200);
    results.push({ name, status: 'FAIL', durationMs: Date.now() - started,
      error: error instanceof Error ? error.message : String(error), rendered });
  } finally {
    process.stderr.write(`${name}: ${results.at(-1).status}\n`);
    await context.close();
  }
}

await scenario('01 durable Customer upload and independent classification', async page => {
  await localLogin(page, 'A100', '/evidence', 'Keep your financial documents together.');
  const responsePromise = page.waitForResponse(response => response.url().includes('/product-api/v1/evidence') && response.request().method() === 'POST', { timeout: 30_000 });
  await page.locator('input[type="file"]').first().setInputFiles(uploadPath);
  await heading(page, shared.fileName);
  await page.getByRole('button', { name: 'Upload document' }).click();
  const response = await responsePromise;
  if (response.status() !== 200) throw new Error(`Evidence upload returned HTTP ${response.status()}.`);
  const receipt = await response.json();
  shared.versionId = receipt.documentVersionId;
  shared.sha256 = receipt.contentSha256;
  shared.storageVersion = receipt.storageVersionId ?? '';
  if (!shared.versionId || !/^[a-f0-9]{64}$/.test(shared.sha256)) throw new Error('Upload receipt lacked a durable version identity or SHA-256.');
  await visible(page, 'Your document was uploaded.');
  await page.getByLabel('Find documents').fill(shared.fileName);
  await page.getByRole('button', { name: `View document details for ${shared.fileName}` }).click();
  await heading(page, shared.fileName);
  await page.getByRole('button', { name: 'Extract details' }).click();
  await heading(page, shared.fileName);
  await page.getByRole('button', { name: 'Extract details' }).click();
  await visible(page, "We couldn't extract details from this document");
});

await scenario('02 Vault exact durable version and checksum', async page => {
  await localLogin(page, 'A100', '/evidence', 'Keep your financial documents together.');
  await page.getByLabel('Search the vault').fill(shared.fileName);
  await page.locator('section[aria-labelledby="document-vault-title"] button.product-receipt-action').filter({ hasText: shared.fileName }).click();
  await heading(page, shared.fileName);
  await visible(page, 'Evidence owner managed storage');
  await visible(page, shared.sha256);
  await visible(page, 'Verified at intake');
  if (shared.storageVersion) await visible(page, shared.storageVersion);
});

await scenario('03 Document Monitoring scan and failed extraction classification', async page => {
  await localLogin(page, 'A900', '/access/documents', 'Document Monitoring');
  await page.getByLabel('Find document').fill(shared.fileName);
  await page.getByRole('button', { name: 'Search documents' }).click();
  await visible(page, shared.fileName);
  await page.getByLabel(`Inspect document ${shared.versionId}`).click();
  await heading(page, shared.fileName);
  await visible(page, 'Verified at intake');
  await visible(page, 'Classification and processing records');
  await visible(page, 'Failed');
});

await scenario('04 all classification queue contains supported families', async page => {
  await localLogin(page, 'A900', '/access/classifications', 'Document classification review');
  await page.getByLabel('Classification filter').click();
  await page.getByText('All classifications', { exact: true }).click();
  for (const family of ['ITR', 'FORM 16', 'BANK STATEMENT', 'SALARY SLIP', 'CREDIT REPORT', 'LOAN', 'INSURANCE', 'INVESTMENT', 'PROPERTY', 'VEHICLE', 'GST', 'PAN', 'AADHAAR']) await visible(page, family);
});

await scenario('05 unknown queue exposes uploaded version', async page => {
  await localLogin(page, 'A900', '/access/classifications', 'Document classification review');
  await visible(page, shared.versionId);
  await visible(page, 'Needs review');
});

await scenario('06 authorized explicit classification review', async page => {
  await localLogin(page, 'A900', '/access/classifications', 'Document classification review');
  await page.getByLabel(`Review classification ${shared.versionId}`).click();
  await visible(page, 'Review document type');
  await page.getByRole('dialog', { name: 'Review document type' }).getByTitle('OTHER').waitFor({ state: 'visible' });
  await page.getByLabel('I confirm the document type for this exact version.').check();
  await page.getByRole('button', { name: 'Save classification review' }).click();
  await visible(page, 'Revision 2: OTHER');
  await visible(page, 'A900');
});

await scenario('07 Customer cannot enter classification administration', async page => {
  await page.goto(base + '/access/classifications', { waitUntil: 'domcontentloaded' });
  const local = page.getByRole('button', { name: 'Use a local test key' });
  await local.last().click();
  await page.getByLabel('Organization').fill('T001');
  await page.getByLabel('Local access key').fill(customerKey);
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await visible(page, 'Organization Administrator access is required.');
});

await scenario('08 stale concurrent review is rejected authoritatively', async page => {
  const second = await page.context().newPage();
  await localLogin(page, 'A900', '/access/classifications', 'Document classification review');
  await localLogin(second, 'A900', '/access/classifications', 'Document classification review');
  const firstButton = page.getByRole('button', { name: /^Review classification / }).first();
  const label = await firstButton.getAttribute('aria-label');
  if (!label) throw new Error('No independent unclassified document remained for stale-review validation.');
  await firstButton.click();
  await second.getByLabel(label).click();
  for (const target of [page, second]) {
    await target.getByRole('dialog', { name: 'Review document type' }).getByTitle('OTHER').waitFor({ state: 'visible' });
    await target.getByLabel('I confirm the document type for this exact version.').check();
  }
  await page.getByRole('button', { name: 'Save classification review' }).click();
  await visible(page, 'Revision 2: OTHER');
  await second.getByRole('button', { name: 'Save classification review' }).click();
  await visible(second, 'This classification changed. Close this review and refresh before submitting again.');
  await second.close();
});

await scenario('09 human review history remains after reprocessing failure', async page => {
  await localLogin(page, 'A900', '/access/classifications', 'Document classification review');
  await page.getByLabel('Classification filter').click();
  await page.getByText('All classifications', { exact: true }).click();
  await page.getByLabel(`Review classification ${shared.versionId}`).click();
  await visible(page, 'Revision 2: OTHER');
  await visible(page, 'Administrator reviewed');
});

await scenario('10 Google Drive imported durable Evidence', async page => {
  await localLogin(page, 'A100', '/evidence', 'Keep your financial documents together.');
  await page.getByLabel('Search the vault').fill('google-statement.txt');
  await page.locator('section[aria-labelledby="document-vault-title"] button.product-receipt-action').filter({ hasText: 'google-statement.txt' }).click();
  await heading(page, 'google-statement.txt');
  await visible(page, 'Evidence owner managed storage');
  const details = await page.getByRole('dialog', { name: 'Vault document' }).innerText();
  if (!/Google/i.test(details) || !/Provider file version\s*10/i.test(details)) throw new Error('The Google Drive source identity/version was not rendered in the durable Vault record.');
});

await scenario('11 OneDrive imported durable Evidence', async page => {
  await localLogin(page, 'A100', '/evidence', 'Keep your financial documents together.');
  await page.getByLabel('Search the vault').fill('onedrive-statement.txt');
  await page.locator('section[aria-labelledby="document-vault-title"] button.product-receipt-action').filter({ hasText: 'onedrive-statement.txt' }).click();
  await heading(page, 'onedrive-statement.txt');
  await visible(page, 'ONEDRIVE');
  await visible(page, 'Evidence owner managed storage');
});

await scenario('12 post-restart durable readback remains visible', async page => {
  await localLogin(page, 'A100', '/evidence', 'Keep your financial documents together.');
  await page.getByLabel('Find documents').fill('google-statement.txt');
  await visible(page, 'google-statement.txt');
  await page.getByLabel('Find documents').fill('onedrive-statement.txt');
  await visible(page, 'onedrive-statement.txt');
  await page.getByLabel('Find documents').fill(shared.fileName);
  await visible(page, shared.fileName);
});

const failed = results.filter(value => value.status === 'FAIL');
const evidence = {
  recordedAt: new Date().toISOString(), browser: 'Google Chrome', browserVersion: browser.version(), executablePath,
  installedChannel: true, executed: results.length, passed: results.length - failed.length, failed: failed.length,
  blocked: 0, uploadedVersionId: shared.versionId, uploadedContentSha256: shared.sha256, scenarios: results,
};
await browser.close();
fs.writeFileSync(path.join(uploadDirectory, 'chrome-evidence.json'), JSON.stringify(evidence, null, 2), 'utf8');
process.stdout.write(JSON.stringify(evidence, null, 2) + '\n');
if (failed.length) process.exitCode = 1;
