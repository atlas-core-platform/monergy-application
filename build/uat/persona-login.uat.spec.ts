import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import type { APIRequestContext, Page } from '@playwright/test';

test.describe.configure({ mode: 'serial' });

const tenantId = process.env.MONERGY_PERSONA_UAT_TENANT ?? 'T001';
const foreignTenant = tenantId === 'T001' ? 'T002' : 'T001';
const password = process.env.MONERGY_PERSONA_UAT_PASSWORD ?? '';
const testerSecret = process.env.MONERGY_PERSONA_TESTER_SECRET ?? '';
const people = [
  { actor: 'A900', email: 'a900@example.test', path: '/access' },
  { actor: 'A100', email: 'a100@example.test', path: '/' },
  { actor: 'A200', email: 'a200@example.test', path: '/' },
  { actor: 'A300', email: 'a300@example.test', path: '/advisor' },
] as const;

test.beforeAll(() => {
  if (password.length < 15 || !testerSecret)
    throw new Error('Protected Persona UAT password and tester secret are required.');
});

async function activate(page: Page, person: typeof people[number]) {
  const issued = await page.request.post(
    '/identity-api/local/v1/persona-credentials/tester/welcome-link',
    {
      headers: { 'X-Monergy-Tester-Activation': testerSecret },
      data: {
        tenantId,
        email: person.email,
        returnPath: person.path,
        requestId: randomUUID().replaceAll('-', ''),
      },
    },
  );
  expect(issued.status(), `${person.actor} welcome link`).toBe(200);
  const result = await issued.json() as { welcomePath: string; expiresAt: string };
  const link = new URL(result.welcomePath, 'http://local.test');
  const ticket = new URLSearchParams(link.hash.slice(1)).get('ticket');
  expect(ticket).toMatch(/^[0-9a-f]{64}$/);
  await page.goto(result.welcomePath);
  await expect(page).toHaveURL(/\/welcome$/);
  await expect(page.getByText('your administrator cannot see it or sign in for you')).toBeVisible();
  await page.getByLabel('Password (15–128 characters)').fill(password);
  await page.getByLabel('Confirm password').fill(password);
  const confirmation = page.waitForResponse((response) =>
    new URL(response.url()).pathname.endsWith('/tester/activation/confirm'));
  await page.getByRole('button', { name: 'Set password' }).click();
  const activated = await confirmation;
  expect(activated.status(), `${person.actor} activation`).toBe(200);
  await expect(page.getByText('Your password is set.')).toBeVisible();
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Continue to sign in' }))
    .toHaveAttribute('href', person.path);
  const replay = await page.request.post(
    '/identity-api/local/v1/persona-credentials/tester/activation/confirm',
    { data: { tenantId, ticket, password } },
  );
  expect(replay.status(), `${person.actor} ticket replay`).toBe(401);
}

async function signIn(page: Page, path: string, email: string) {
  await page.goto(path);
  await expect(page.getByRole('heading', { name: 'Sign in to Monergy' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Activate account' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Forgot password' })).toHaveCount(0);
  await page.getByLabel('Organization').fill(tenantId);
  await page.getByLabel('Email address').fill(email);
  await page.getByLabel('Password').fill(password);
  const signedIn = page.waitForResponse((response) =>
    new URL(response.url()).pathname.endsWith('/persona-credentials/sign-in'));
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  return await signedIn;
}

async function passwordSession(request: APIRequestContext, email: string) {
  const response = await request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId, email, password },
  });
  expect(response.status()).toBe(200);
  return await response.json() as {
    tenantId: string; actorId: string; authenticationContextId: string; subjectVersion: number;
  };
}

test('Development profile exposes tester activation without an email server', async ({ request }) => {
  const persona = await request.get('/identity-api/local/v1/persona-credentials');
  expect(await persona.json()).toEqual({ enabled: true, activation: 'tester-link' });
  const email = await request.get('/identity-api/local/v1/customer-email-access');
  expect(await email.json()).toEqual({ enabled: false });
  const denied = await request.post('/identity-api/local/v1/persona-credentials/tester/welcome-link', {
    data: {
      tenantId, email: 'a300@example.test', returnPath: '/advisor',
      requestId: randomUUID().replaceAll('-', ''),
    },
  });
  expect(denied.status()).toBe(403);
  const incorrect = await request.post('/identity-api/local/v1/persona-credentials/tester/welcome-link', {
    headers: { 'X-Monergy-Tester-Activation': 'incorrect-tester-authorization' },
    data: { tenantId, email: 'a300@example.test', returnPath: '/advisor' },
  });
  expect(incorrect.status()).toBe(403);
  const arbitrary = await request.post('/identity-api/local/v1/persona-credentials/tester/welcome-link', {
    headers: { 'X-Monergy-Tester-Activation': testerSecret },
    data: {
      tenantId, email: 'not-provisioned@example.test', returnPath: '/advisor',
      requestId: randomUUID().replaceAll('-', ''),
    },
  });
  expect(arbitrary.status()).toBe(409);
});

test('invited users choose their own password from a stripped, single-use welcome link', async ({ page }) => {
  for (const person of people) await activate(page, person);
});

test('A900 signs in normally and reaches Organization Settings only', async ({ page }) => {
  expect((await signIn(page, '/access', 'a900@example.test')).status()).toBe(200);
  const setup = page.getByRole('heading', { name: 'Welcome to Monergy — Set up your organization' });
  const shell = page.locator('.mw-shell');
  await expect(setup.or(shell)).toBeVisible();
  if (await setup.isVisible()) {
    await expect(setup).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
    return;
  }
  await expect(shell).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Access overview', exact: true })).toBeVisible();
});

test('A100 and A200 sign in normally and reach their customer workspaces', async ({ page }) => {
  for (const email of ['a100@example.test', 'a200@example.test']) {
    expect((await signIn(page, '/', email)).status()).toBe(200);
    await expect(page.locator('.mw-shell')).toBeVisible();
    await page.getByRole('button', { name: 'Sign out' }).click();
  }
});

test('A300 signs in normally and reaches the Financial Advisor workspace', async ({ page }) => {
  expect((await signIn(page, '/advisor', 'a300@example.test')).status()).toBe(200);
  await expect(page.locator('.mw-shell')).toBeVisible();
  await expect(page.getByText('Financial Advisor', { exact: true }).first()).toBeVisible();
});

test('wrong password, wrong tenant, and wrong persona routes deny without a protected shell', async ({ page, request }) => {
  const wrongPassword = await request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId, email: 'a300@example.test', password: 'incorrect password value' },
  });
  expect(wrongPassword.status()).toBe(401);
  const wrongTenant = await request.post('/identity-api/local/v1/persona-credentials/sign-in', {
    data: { tenantId: foreignTenant, email: 'a300@example.test', password },
  });
  expect(wrongTenant.status()).toBe(401);

  expect((await signIn(page, '/access', 'a300@example.test')).status()).toBe(200);
  await expect(page.getByRole('alert').filter({ hasText: 'Organization Administrator access is required' })).toBeVisible();
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  expect((await signIn(page, '/', 'a300@example.test')).status()).toBe(200);
  await expect(page.getByRole('alert').filter({ hasText: 'customer workspace and product role are required' })).toBeVisible();
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  expect((await signIn(page, '/advisor', 'a100@example.test')).status()).toBe(200);
  await expect(page.getByRole('alert').filter({ hasText: 'active Financial Advisor' })).toBeVisible();
  await expect(page.locator('.mw-shell')).toHaveCount(0);
});

test('revoked sessions cannot be replayed', async ({ request }) => {
  const session = await passwordSession(request, 'a300@example.test');
  const revoked = await request.post('/identity-api/local/v1/tenant-sessions/revoke', {
    data: { tenantId, authenticationContextId: session.authenticationContextId },
  });
  expect(revoked.status()).toBe(200);
  const replay = await request.get('/access-api/v1/advisor/context', {
    headers: {
      'X-Monergy-Tenant': tenantId,
      'X-Monergy-Session': session.authenticationContextId,
    },
  });
  expect(replay.status()).toBe(401);
});

test('disabled membership and removed Advisor role deny, and restoration reestablishes current authority', async ({ request }) => {
  const admin = await passwordSession(request, 'a900@example.test');
  const headers = {
    'X-Monergy-Tenant': tenantId,
    'X-Monergy-Session': admin.authenticationContextId,
  };
  const readMember = async () => {
    const response = await request.get('/access-api/v1/administration/members?limit=100', { headers });
    expect(response.status()).toBe(200);
    const body = await response.json() as {
      policyVersion: number;
      items: { actorId: string; businessRoleId: string | null; active: boolean; tenantAdmin: boolean }[];
    };
    const member = body.items.find((value) => value.actorId === 'A300');
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
    } else expect([401, 403]).toContain(roleless.status());
  } finally {
    await update(true, original.businessRoleId);
  }
  const restored = await passwordSession(request, 'a300@example.test');
  const advisor = await request.get('/access-api/v1/advisor/context', {
    headers: {
      'X-Monergy-Tenant': tenantId,
      'X-Monergy-Session': restored.authenticationContextId,
    },
  });
  expect(advisor.status()).toBe(200);
});
