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
  { actor: 'A850', email: 'a850@example.test', path: '/operations-support' },
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
  let link = issued;
  if (issued.status() === 409) {
    const requestId = randomUUID().replaceAll('-', '');
    for (let retry = 0; retry < 65; retry += 1) {
      link = await page.request.post('/identity-api/local/v1/persona-credentials/tester/recovery-link', {
        headers: { 'X-Monergy-Tester-Activation': personaTester },
        data: { tenantId: tenant, email, returnPath: path, requestId },
      });
      if (link.status() === 200) break;
      expect(link.status()).toBe(409);
      await page.waitForTimeout(1000);
    }
  }
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

test('compact tenant persona smoke keeps administrator, customer and advisor routing separate', async ({ page }) => {
  const smokePeople = people.filter(person => ['A900', 'A100', 'A300'].includes(person.actor));
  for (const person of smokePeople) {
    await activatePersona(page, tenantId, person.email, person.path);
    await signInPersona(page, person);
    await page.getByRole('button', { name: 'Sign out' }).click();
  }
  await activatePersona(page, 'T002', 'a900@example.test', '/access');
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

test('A850 Operations Support signs out and signs in again with human credentials', async ({ page }) => {
  const operationsSupport = people.find(person => person.actor === 'A850')!;
  await activatePersona(page, tenantId, operationsSupport.email, operationsSupport.path);
  await signInPersona(page, operationsSupport);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await signInPersona(page, operationsSupport);
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

test('five failed password attempts lock the credential until protected recovery', async ({ page }) => {
  for (let attempt = 0; attempt < 5; attempt += 1) {
    const denied = await page.request.post('/identity-api/local/v1/persona-credentials/sign-in', {
      data: { tenantId, email: 'a850@example.test', password: 'Wrong!Password12345' },
    });
    expect(denied.status(), `failed sign-in ${attempt + 1}`).toBe(401);
  }
  const locked = await page.request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId, email: 'a850@example.test', password },
  });
  expect(locked.status()).toBe(401);

  await activatePersona(page, tenantId, 'a850@example.test', '/operations-support');
  expect((await page.request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId, email: 'a850@example.test', password },
  })).status()).toBe(200);
});

test('platform lockout and credential-version recovery revoke old sessions and allow normal relogin', async ({ request }) => {
  const signedIn = await request.post('/platform-api/local/v1/sessions', { data: { email: platformEmail, password } });
  expect(signedIn.status()).toBe(200);
  const old = await signedIn.json() as { session: string };
  for (let attempt = 0; attempt < 5; attempt += 1) {
    expect((await request.post('/platform-api/local/v1/sessions', {
      data: { email: platformEmail, password: 'incorrect platform password' },
    })).status()).toBe(401);
  }
  expect((await request.post('/platform-api/local/v1/sessions', {
    data: { email: platformEmail, password },
  })).status()).toBe(423);
  const issued = await request.post('/platform-api/local/v1/credentials/tester/welcome-link', {
    headers: { 'X-Monergy-Platform-Tester': platformTester }, data: { email: platformEmail },
  });
  expect(issued.status()).toBe(200);
  const welcomePath = (await issued.json() as { welcomePath: string }).welcomePath;
  const ticket = new URL(welcomePath, 'http://local.test').searchParams.get('ticket');
  expect((await request.post('/platform-api/local/v1/credentials/activation/confirm', {
    data: { ticket, password },
  })).status()).toBe(204);
  expect((await request.get('/platform-api/local/v1/context', {
    headers: { 'X-Monergy-Platform-Session': old.session },
  })).status()).toBe(401);
  const recovered = await request.post('/platform-api/local/v1/sessions', { data: { email: platformEmail, password } });
  expect(recovered.status()).toBe(200);
  const current = await recovered.json() as { session: string };
  expect((await request.delete('/platform-api/local/v1/sessions/current', {
    headers: { 'X-Monergy-Platform-Session': current.session },
  })).status()).toBe(204);
  expect((await request.post('/platform-api/local/v1/sessions', { data: { email: platformEmail, password } })).status()).toBe(200);
});

test('governed provider secrets are one-way and all four runtime owners report truthful readiness', async ({ page }) => {
  await page.goto('/platform');
  await page.getByLabel('Email').fill(platformEmail);
  await page.getByLabel('Password').fill(password);
  const response = page.waitForResponse(item => item.url().endsWith('/platform-api/local/v1/sessions'));
  await page.getByRole('button', { name: 'Sign in' }).click();
  const platform = await (await response).json() as { session: string };
  const headers = { 'X-Monergy-Platform-Session': platform.session };
  const before = await (await page.request.get('/platform-api/local/v1/integrations', { headers })).json() as { revision: number };
  let revision = before.revision;
  const secrets = {
    email: 'smtp-secret-not-for-responses', google: 'google-secret-not-for-responses',
    onedrive: 'onedrive-secret-not-for-responses', ai: 'ai-secret-not-for-responses',
  };
  const acceptedOperationIds: string[] = [];
  const configure = async (code: string, data: Record<string, unknown>) => {
    const operationId = randomUUID().replaceAll('-', '');
    const result = await page.request.put('/platform-api/local/v1/integrations/' + code, { headers, data: {
      operationId, expectedRevision: revision, enabled: true,
      scope: 'Organization', subscriptionStatus: 'Configured', clearSecret: false, ...data,
    } });
    expect(result.status(), code).toBe(200);
    revision = (await result.json() as { revision: number }).revision;
    acceptedOperationIds.push(operationId);
  };
  await configure('email', { endpoint: 'smtp://mail.local.test:2525', clientId: 'local-mail-user',
    callbackUri: 'sender@example.test', model: null, options: { tenantId }, secretValue: secrets.email });
  await configure('email', { endpoint: 'smtp://mail.local.test:2525', clientId: 'local-mail-user',
    callbackUri: 'sender@example.test', model: null, options: { tenantId }, secretValue: 'smtp-replacement-not-for-responses' });
  await configure('google-drive', { endpoint: null, clientId: 'local-google-client',
    callbackUri: 'http://127.0.0.1:4322/connections-api/local/v1/google-drive/callback', model: null,
    options: { tenantId }, secretValue: secrets.google });
  await configure('onedrive', { endpoint: null, clientId: 'local-onedrive-client',
    callbackUri: 'http://127.0.0.1:4322/connections-api/local/v1/onedrive/callback', model: null,
    options: { tenantId }, secretValue: secrets.onedrive });
  await configure('ai', { endpoint: 'https://model.local.example.test/v1/chat/completions', clientId: null,
    callbackUri: null, model: 'local-deterministic', options: { tenantId, providerLabel: 'LOCAL deterministic fixture',
      notice: 'LOCAL deterministic contract; not a Production provider approval.' }, secretValue: secrets.ai });
  const staleOperationId = randomUUID().replaceAll('-', '');
  expect((await page.request.put('/platform-api/local/v1/integrations/ai', {
    headers, data: { operationId: staleOperationId, expectedRevision: before.revision, enabled: true,
      scope: 'Organization', endpoint: 'https://model.local.example.test/v1/chat/completions', clientId: null,
      callbackUri: null, model: 'local-deterministic', options: { tenantId, providerLabel: 'LOCAL fixture', notice: 'LOCAL only' },
      secretValue: 'stale-secret-must-not-persist', clearSecret: false, subscriptionStatus: 'Configured' },
  })).status()).toBe(409);
  const configuration = await page.request.get('/platform-api/local/v1/integrations', { headers });
  const body = await configuration.text();
  expect(body).toContain('secretConfigured');
  expect(body.match(/"readiness":"Ready"/g)).toHaveLength(4);
  for (const secret of [...Object.values(secrets), 'smtp-replacement-not-for-responses', 'stale-secret-must-not-persist'])
    expect(body).not.toContain(secret);
  expect(body).not.toMatch(/secretReferences|password|authorization/i);
  const audit = await (await page.request.get('/platform-api/local/v1/audit', { headers })).text();
  expect(audit).toContain(acceptedOperationIds.at(-1)!);
  expect(audit).toContain(staleOperationId);
  expect(audit).toContain('ConfigurationChangeRejected');
  expect(audit).toContain('Conflict');
  expect(audit).toContain('Organization');
  for (const secret of [...Object.values(secrets), 'smtp-replacement-not-for-responses', 'stale-secret-must-not-persist'])
    expect(audit).not.toContain(secret);
  expect(audit).not.toMatch(/passwordHash|provider-secret-value|authorizationHeader|activationTicket|testerCredential/i);
  const browserStorage = await page.evaluate(() => JSON.stringify({
    local: Object.fromEntries(Object.entries(localStorage)),
    session: Object.fromEntries(Object.entries(sessionStorage)),
  }));
  expect(browserStorage).not.toContain(password);
  expect(browserStorage).not.toContain(platformTester);
  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.getByText('Email delivery')).toBeVisible();
  expect(await page.getByText('Ready').count()).toBeGreaterThanOrEqual(4);
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

test('disabled membership and removed role deny current credentials, then restoration recovers authority', async ({ request }) => {
  const administrator = await personaSession(request, tenantId, 'a900@example.test');
  const headers = {
    'X-Monergy-Tenant': tenantId,
    'X-Monergy-Session': administrator.authenticationContextId,
  };
  const readMember = async () => {
    const response = await request.get('/access-api/v1/administration/members?limit=100', { headers });
    expect(response.status()).toBe(200);
    const body = await response.json() as {
      policyVersion: number;
      items: { actorId: string; businessRoleId: string | null; active: boolean; tenantAdmin: boolean }[];
    };
    const member = body.items.find(value => value.actorId === 'A300');
    expect(member).toBeTruthy();
    return { body, member: member! };
  };
  const original = (await readMember()).member;
  const update = async (active: boolean, businessRoleId: string | null) => {
    const current = await readMember();
    const response = await request.put('/access-api/v1/administration/members/A300', {
      headers,
      data: {
        expectedPolicyVersion: current.body.policyVersion,
        businessRoleId,
        active,
        tenantAdmin: false,
      },
    });
    expect(response.status()).toBe(200);
  };

  try {
    await update(false, original.businessRoleId);
    const disabled = await request.post('/identity-api/local/v1/persona-credentials/sign-in', {
      data: { tenantId, email: 'a300@example.test', password },
    });
    expect([401, 403]).toContain(disabled.status());

    await update(true, null);
    const roleless = await request.post('/identity-api/local/v1/persona-credentials/sign-in', {
      data: { tenantId, email: 'a300@example.test', password },
    });
    if (roleless.status() === 200) {
      const session = await roleless.json() as { authenticationContextId: string };
      const advisor = await request.get('/access-api/v1/advisor/context', {
        headers: {
          'X-Monergy-Tenant': tenantId,
          'X-Monergy-Session': session.authenticationContextId,
        },
      });
      expect(advisor.status()).toBe(403);
    } else {
      expect([401, 403]).toContain(roleless.status());
    }
  } finally {
    await update(true, original.businessRoleId);
  }

  const restored = await personaSession(request, tenantId, 'a300@example.test');
  const advisor = await request.get('/access-api/v1/advisor/context', {
    headers: {
      'X-Monergy-Tenant': tenantId,
      'X-Monergy-Session': restored.authenticationContextId,
    },
  });
  expect(advisor.status()).toBe(200);
});
