import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

const session = {
  tenantId: 'T001',
  actorId: 'A900',
  authenticationContextId: 'entry-fixture-session',
  subjectVersion: 1,
  expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
};
const context = { tenantId: 'T001', actorId: 'A900', authority: 'TenantAdmin', policyVersion: 7 };
const member = {
  actorId: 'A100',
  normalizedEmail: 'entry@example.test',
  status: 'Active',
  tenantAdmin: false,
  businessRoleId: null,
};
const list = { policyVersion: 7, items: [], nextCursor: null };
async function fixtures(page: Page, override?: (route: Route) => Promise<boolean>) {
  await page.route('**/*-api/**', async (route) => {
    if (await override?.(route)) return;
    const path = new URL(route.request().url()).pathname;
    await route.fulfill({
      json: path.endsWith('/tenant-sessions')
        ? session
        : path.endsWith('/revoke')
          ? true
          : path.endsWith('/context')
            ? context
            : path.endsWith('/members')
              ? { ...list, items: [member] }
              : list,
    });
  });
}
async function connect(page: Page) {
  await page.getByLabel('Local access key').fill('fixture-only-not-a-credential');
  await page.getByRole('button', { name: 'Connect workspace', exact: true }).click();
}
async function publicOnly(page: Page) {
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await expect(page.locator('.access-record-button')).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
}

test('every Access direct route and operations stays public before credentials and after reload', async ({
  page,
}) => {
  let reads = 0;
  await fixtures(page, async (route) => {
    if (route.request().url().includes('/administration/')) reads += 1;
    return false;
  });
  for (const path of [
    '/access',
    '/access/users',
    '/access/roles',
    '/access/permissions',
    '/access/groups',
    '/access/resources',
    '/access/activity',
    '/access/sessions',
    '/access/users/history',
    '/operations',
  ]) {
    await page.goto(path);
    await expect(page.getByLabel('Local access key')).toBeVisible();
    await publicOnly(page);
    await page.keyboard.press('Control+k');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  }
  expect(reads).toBe(0);
  await page.goto('/access/users');
  await connect(page);
  await expect(page.getByText(member.normalizedEmail, { exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByLabel('Local access key')).toBeVisible();
  await publicOnly(page);
  expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0]);
});

for (const scenario of [
  'invalid-key',
  'non-admin',
  'wrong-tenant',
  'wrong-actor',
  'expired',
  'malformed',
  'owner-unavailable',
]) {
  test(`rejects ${scenario} without mounting the shell or reading protected records`, async ({
    page,
  }) => {
    let reads = 0;
    await fixtures(page, async (route) => {
      const path = new URL(route.request().url()).pathname;
      if (path.includes('/administration/') && !path.endsWith('/context')) reads += 1;
      if (path.endsWith('/tenant-sessions')) {
        if (scenario === 'invalid-key') {
          await route.fulfill({ status: 401, json: { error: 'AUTHENTICATION_REQUIRED' } });
          return true;
        }
        if (scenario === 'wrong-tenant') {
          await route.fulfill({ json: { ...session, tenantId: 'T002' } });
          return true;
        }
        if (scenario === 'expired') {
          await route.fulfill({ json: { ...session, expiresAt: '2000-01-01T00:00:00Z' } });
          return true;
        }
        if (scenario === 'malformed') {
          await route.fulfill({ json: { ...session, expiresAt: 'not-a-date' } });
          return true;
        }
      }
      if (path.endsWith('/context')) {
        await route.fulfill({
          status: scenario === 'non-admin' ? 403 : scenario === 'owner-unavailable' ? 503 : 200,
          json:
            scenario === 'wrong-actor'
              ? { ...context, actorId: 'A901' }
              : { error: 'TENANT_ADMIN_REQUIRED' },
        });
        return true;
      }
      return false;
    });
    await page.goto('/access/users');
    await connect(page);
    await expect(page.getByRole('alert')).toBeVisible();
    await expect(page.getByLabel('Local access key')).toHaveValue('');
    await publicOnly(page);
    expect(reads).toBe(0);
  });
}

test('administrator verification must finish before shell creation; cancellation cannot be reversed by a late response', async ({
  page,
}) => {
  let release: (() => void) | undefined;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  let reached = false;
  await fixtures(page, async (route) => {
    if (!route.request().url().endsWith('/context')) return false;
    reached = true;
    await pending;
    await route.fulfill({ json: context }).catch(() => undefined);
    return true;
  });
  await page.goto('/access/users');
  await connect(page);
  await expect.poll(() => reached).toBe(true);
  await expect(page.getByRole('status')).toContainText('Verifying tenant administrator');
  await publicOnly(page);
  await page.getByRole('button', { name: 'Cancel connection' }).click();
  release?.();
  await expect(page.getByRole('button', { name: 'Connect workspace', exact: true })).toBeEnabled();
  await publicOnly(page);
});

test('idle expiry removes the shell and open portal without waiting for another API request', async ({
  page,
}) => {
  await page.clock.install();
  const expiresAt = new Date((await page.evaluate(() => Date.now())) + 15_000).toISOString();
  await fixtures(page, async (route) => {
    if (!route.request().url().endsWith('/tenant-sessions')) return false;
    await route.fulfill({ json: { ...session, expiresAt } });
    return true;
  });
  await page.goto('/access/users');
  await connect(page);
  await page.getByRole('button', { name: 'Create user', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.clock.fastForward(15_001);
  await expect(page.getByRole('alert')).toContainText('session has ended');
  await publicOnly(page);
});

test('sign-out clears content before revocation completes and history cannot restore it', async ({
  page,
}) => {
  let release: (() => void) | undefined;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  await fixtures(page, async (route) => {
    if (!route.request().url().endsWith('/revoke')) return false;
    await pending;
    await route.fulfill({ status: 503, json: { error: 'OWNER_UNAVAILABLE' } });
    return true;
  });
  await page.goto('/access/users');
  await connect(page);
  await page.getByRole('navigation').getByRole('link', { name: 'Roles', exact: true }).click();
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await publicOnly(page);
  await page.goBack();
  await publicOnly(page);
  release?.();
  await expect(page.getByRole('alert')).toContainText('could not confirm session revocation');
});

for (const code of [401, 403, 503]) {
  test(`a protected ${code} response clears tenant records and open navigation portals`, async ({
    page,
  }) => {
    let fail = false;
    await fixtures(page, async (route) => {
      if (!fail || !route.request().url().includes('/members')) return false;
      await route.fulfill({
        status: code,
        json: {
          error:
            code === 401
              ? 'SESSION_NOT_CURRENT'
              : code === 403
                ? 'TENANT_ADMIN_REQUIRED'
                : 'OWNER_UNAVAILABLE',
        },
      });
      return true;
    });
    await page.goto('/access/users');
    await connect(page);
    await expect(page.getByText(member.normalizedEmail, { exact: true })).toBeVisible();
    fail = true;
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await publicOnly(page);
    await expect(page.getByText(member.normalizedEmail, { exact: true })).toHaveCount(0);
    if (code === 503) {
      fail = false;
      await page.getByRole('button', { name: 'Reconnect workspace' }).click();
      await expect(page.getByText(member.normalizedEmail, { exact: true })).toBeVisible();
    }
  });
}

test('offline and tab resume require owner revalidation', async ({ page }) => {
  let checks = 0;
  await fixtures(page, async (route) => {
    if (route.request().url().endsWith('/context')) checks += 1;
    return false;
  });
  await page.goto('/access/users');
  await connect(page);
  await expect(page.getByText(member.normalizedEmail, { exact: true })).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new Event('offline')));
  await publicOnly(page);
  await page.getByRole('button', { name: 'Reconnect workspace' }).click();
  await expect(page.getByText(member.normalizedEmail, { exact: true })).toBeVisible();
  expect(checks).toBe(2);
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await expect.poll(() => checks).toBe(3);
  await expect(page.getByText(member.normalizedEmail, { exact: true })).toBeVisible();
});

test('an uncertain onboarding commit is recovered by its original ID after reconnection, never replayed', async ({
  page,
}) => {
  let commits = 0;
  let operationId = '';
  await fixtures(page, async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/preview')) {
      await route.fulfill({
        json: { valid: true, policyVersion: 7, contentHash: 'fixture', rowCount: 1, errors: [] },
      });
      return true;
    }
    if (path.endsWith('/commit')) {
      commits += 1;
      operationId = path.split('/').at(-2) ?? '';
      await route.abort('failed');
      return true;
    }
    if (operationId && path.endsWith('/imports/' + operationId)) {
      await route.fulfill({
        json: {
          id: operationId,
          status: 'PendingIdentity',
          policyVersion: 8,
          rowCount: 1,
          contentHash: 'fixture',
          errorCode: null,
          identities: [],
        },
      });
      return true;
    }
    return false;
  });
  await page.goto('/access/users');
  await connect(page);
  await page.getByRole('button', { name: 'Create user', exact: true }).click();
  await page.getByLabel('Email address').fill('recovery@example.test');
  await page.getByRole('button', { name: 'Review user', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Create user', exact: true }).click();
  await publicOnly(page);
  await page.getByRole('button', { name: 'Reconnect workspace' }).click();
  await page.getByRole('button', { name: 'Review request', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('PendingIdentity');
  expect(commits).toBe(1);
  expect(operationId).not.toBe('');
});
