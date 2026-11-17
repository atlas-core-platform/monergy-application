import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import type { APIRequestContext, Page } from '@playwright/test';

test.describe.configure({ mode: 'serial' });

const tenantId = 'T001';
const password = process.env.MONERGY_PREAZURE_PASSWORD ?? '';
const personaTester = process.env.MONERGY_PERSONA_TESTER_SECRET ?? '';
const platformTester = process.env.MONERGY_PLATFORM_TESTER_SECRET ?? '';
const platformEmail = 'a950.platform@example.test';
const people = [
  { actor: 'A900', email: 'a900@example.test', path: '/access' },
  { actor: 'A100', email: 'a100@example.test', path: '/' },
  { actor: 'A200', email: 'a200@example.test', path: '/' },
  { actor: 'A300', email: 'a300@example.test', path: '/advisor' },
  { actor: 'A400', email: 'a400@example.test', path: '/dsa' },
  { actor: 'A500', email: 'a500@example.test', path: '/lender' },
  { actor: 'A600', email: 'a600@example.test', path: '/financial-professional' },
  { actor: 'A700', email: 'a700@example.test', path: '/property-valuer' },
  { actor: 'A800', email: 'a800@example.test', path: '/legal-advisor' },
] as const;

test.beforeAll(() => {
  if (password.length < 15 || !personaTester || !platformTester)
    throw new Error('Protected PRE-AZURE password and tester credentials are required.');
});

async function activatePersona(page: Page, tenant: string, email: string, path: string) {
  const existing = await page.request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId: tenant, email, password },
  });
  if (existing.status() === 200) return;
  const issued = await page.request.post('/identity-api/local/v1/persona-credentials/tester/welcome-link', {
    headers: { 'X-Monergy-Tester-Activation': personaTester },
    data: { tenantId: tenant, email, returnPath: path, requestId: randomUUID().replaceAll('-', '') },
  });
  const link = issued.status() === 409
    ? await page.request.post('/identity-api/local/v1/persona-credentials/tester/recovery-link', {
      headers: { 'X-Monergy-Tester-Activation': personaTester },
      data: { tenantId: tenant, email, returnPath: path, requestId: randomUUID().replaceAll('-', '') },
    })
    : issued;
  expect(link.status()).toBe(200);
  const value = await link.json() as { welcomePath: string };
  await page.goto(value.welcomePath);
  await page.getByLabel('Password (15–128 characters)').fill(password);
  await page.getByLabel('Confirm password').fill(password);
  const response = page.waitForResponse(item => item.url().endsWith('/tester/activation/confirm') ||
    item.url().endsWith('/tester/recovery/confirm'));
  await page.getByRole('button', { name: 'Set password' }).click();
  expect((await response).status()).toBe(200);
  await expect(page.getByText('Your password is set.')).toBeVisible();
}

async function signInPersona(page: Page, person: typeof people[number]) {
  await page.goto(person.path);
  await page.getByLabel('Organization').fill(tenantId);
  await page.getByLabel('Email address').fill(person.email);
  await page.getByLabel('Password').fill(password);
  const response = page.waitForResponse(item => item.url().endsWith('/persona-credentials/sign-in'));
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  expect((await response).status(), `${person.actor} password sign-in`).toBe(200);
  if (person.actor === 'A900') {
    const setup = page.getByRole('heading', { name: 'Welcome to Monergy — Set up your organization' });
    const workspace = page.locator('.mw-shell');
    await expect(setup.or(workspace)).toBeVisible();
    if (await workspace.isVisible()) expect(new URL(page.url()).pathname).toBe(person.path);
    return;
  }
  await expect(page.locator('.mw-shell')).toBeVisible();
  expect(new URL(page.url()).pathname).toBe(person.path);
}

async function personaSession(request: APIRequestContext, tenant: string, email: string) {
  const response = await request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId: tenant, email, password },
  });
  expect(response.status()).toBe(200);
  return await response.json() as { tenantId: string; actorId: string; authenticationContextId: string };
}

test('protected LOCAL activation creates credentials for all nine tenant personas', async ({ page }) => {
  for (const person of people) await activatePersona(page, tenantId, person.email, person.path);
  await activatePersona(page, 'T002', 'a900@example.test', '/access');
});

test('separate A950 credential owner activates once without creating a tenant identity', async ({ page }) => {
  const denied = await page.request.post('/platform-api/local/v1/credentials/tester/welcome-link', { data: { email: platformEmail } });
  expect(denied.status()).toBe(403);
  const issued = await page.request.post('/platform-api/local/v1/credentials/tester/welcome-link', {
    headers: { 'X-Monergy-Platform-Tester': platformTester }, data: { email: platformEmail },
  });
  expect(issued.status()).toBe(200);
  const value = await issued.json() as { welcomePath: string };
  await page.goto(value.welcomePath);
  await page.getByLabel('Password (15–128 characters)').fill(password);
  await page.getByLabel('Confirm password').fill(password);
  const confirmation = page.waitForResponse(item => item.url().endsWith('/activation/confirm'));
  await page.getByRole('button', { name: 'Create password' }).click();
  expect((await confirmation).status()).toBe(204);
  const ticket = new URL(value.welcomePath, 'http://local.test').searchParams.get('ticket');
  const replay = await page.request.post('/platform-api/local/v1/credentials/activation/confirm', { data: { ticket, password } });
  expect(replay.status()).toBe(401);
});

test('all ten personas use email and password and reach only their workspace', async ({ page }) => {
  for (const person of people) {
    await signInPersona(page, person);
    await page.getByRole('button', { name: 'Sign out' }).click();
  }
  await page.goto('/platform');
  await page.getByLabel('Email').fill(platformEmail);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'System status' })).toBeVisible();
  await expect(page.getByText(/cannot open organization or customer information/)).toBeVisible();
});

test('wrong password, wrong tenant and cross-authority credentials fail closed', async ({ request }) => {
  expect((await request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId, email: 'a300@example.test', password: 'Wrong!Password12345' },
  })).status()).toBe(401);
  expect((await request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId: 'T002', email: 'a300@example.test', password },
  })).status()).toBe(401);
  expect((await request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId, email: platformEmail, password },
  })).status()).toBe(401);
  expect((await request.post('/platform-api/local/v1/sessions', {
    data: { email: 'a900@example.test', password },
  })).status()).toBe(401);
});

test('platform provider metadata is versioned, audited and secret values are never returned', async ({ page }) => {
  await page.goto('/platform');
  await page.getByLabel('Email').fill(platformEmail);
  await page.getByLabel('Password').fill(password);
  const response = page.waitForResponse(item => item.url().endsWith('/platform-api/local/v1/sessions'));
  await page.getByRole('button', { name: 'Sign in' }).click();
  const platform = await (await response).json() as { session: string };
  const headers = { 'X-Monergy-Platform-Session': platform.session };
  const before = await (await page.request.get('/platform-api/local/v1/integrations', { headers })).json() as { revision: number };
  const change = { operationId: randomUUID().replaceAll('-', ''), expectedRevision: before.revision, enabled: true,
    scope: 'Organization', endpoint: 'https://smtp.example.test', clientId: 'local-mail', callbackUri: null,
    model: null, options: { tenantId }, secretReferences: ['smtp-primary'], subscriptionStatus: 'Configured' };
  expect((await page.request.put('/platform-api/local/v1/integrations/email', { headers, data: change })).status()).toBe(200);
  expect((await page.request.put('/platform-api/local/v1/integrations/ai', {
    headers, data: { ...change, operationId: randomUUID().replaceAll('-', ''), expectedRevision: before.revision },
  })).status()).toBe(409);
  const configuration = await page.request.get('/platform-api/local/v1/integrations', { headers });
  const body = await configuration.text();
  expect(body).toContain('smtp-primary');
  expect(body).not.toMatch(/password|provider-secret-value|authorization/i);
  const audit = await (await page.request.get('/platform-api/local/v1/audit', { headers })).text();
  expect(audit).toContain(change.operationId);
  expect(audit).not.toMatch(/passwordHash|provider-secret-value|authorizationHeader|activationTicket|testerCredential/i);
  const browserStorage = await page.evaluate(() => JSON.stringify({
    local: Object.fromEntries(Object.entries(localStorage)),
    session: Object.fromEntries(Object.entries(sessionStorage)),
  }));
  expect(browserStorage).not.toContain(password);
  expect(browserStorage).not.toContain(platformTester);
  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.getByText('Email delivery')).toBeVisible();
  await expect(page.getByText('Configured for readiness check')).toBeVisible();
});

test('Organization Administrator sees only its connected-service projection', async ({ page, request }) => {
  const t001 = await personaSession(request, 'T001', 'a900@example.test');
  const own = await request.get('/uat-api/v1/connected-services', { headers: {
    'X-Monergy-Tenant': 'T001', 'X-Monergy-Session': t001.authenticationContextId,
  } });
  expect(own.status()).toBe(200);
  expect(await own.text()).toContain('Email delivery');
  const t002 = await personaSession(request, 'T002', 'a900@example.test');
  const foreign = await request.get('/uat-api/v1/connected-services', { headers: {
    'X-Monergy-Tenant': 'T002', 'X-Monergy-Session': t002.authenticationContextId,
  } });
  expect(foreign.status()).toBe(200);
  expect((await foreign.json() as { providers: unknown[] }).providers).toHaveLength(0);
  await page.goto('/access/connected-services');
  await page.getByLabel('Organization').fill('T001');
  await page.getByLabel('Email address').fill('a900@example.test');
  await page.getByLabel('Password').fill(password);
  const administratorSignIn = page.waitForResponse(item => item.url().endsWith('/persona-credentials/sign-in'));
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  expect((await administratorSignIn).status()).toBe(200);
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
  const setup = page.getByRole('heading', { name: 'Welcome to Monergy — Set up your organization' });
  const connected = page.getByRole('region', { name: 'Connected Services' });
  await expect(setup.or(connected)).toBeVisible();
  if (await setup.isVisible()) {
    const reviewed = page.getByRole('checkbox', { name: 'I have reviewed the organization details' });
    await expect(reviewed.or(connected)).toBeVisible();
    if (await reviewed.isVisible()) {
      await reviewed.check();
      await page.getByRole('checkbox', { name: 'I understand how roles and customer access work' }).check();
      await page.getByRole('button', { name: 'Complete setup' }).click();
      const resume = page.getByRole('button', { name: 'Resume activation' });
      await expect(resume.or(connected)).toBeVisible();
      if (await resume.isVisible()) {
        await resume.click();
        await expect(page.getByRole('heading', { name: 'A Monergy access update is available.' })).toBeVisible();
        await page.goto('/access/connected-services');
      }
    }
  }
  await expect(connected).toBeVisible();
  await expect(page.getByText('Email delivery')).toBeVisible();
  await expect(page.getByText(/Provider secrets, platform-wide settings and other organizations are never shown/)).toBeVisible();
});

test('revoked tenant session cannot read Connected Services', async ({ request }) => {
  const session = await personaSession(request, tenantId, 'a900@example.test');
  expect((await request.post('/identity-api/local/v1/tenant-sessions/revoke', {
    data: { tenantId, authenticationContextId: session.authenticationContextId },
  })).status()).toBe(200);
  const denied = await request.get('/uat-api/v1/connected-services', { headers: {
    'X-Monergy-Tenant': tenantId, 'X-Monergy-Session': session.authenticationContextId,
  } });
  expect(denied.status()).toBe(401);
});
