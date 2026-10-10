import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

async function fixture(page: Page, initiallyActive = false) {
  let active = initiallyActive;
  let policyVersion = 7;
  let assignments: { id: string; actorId: string; customerId: string }[] = [];
  const writes: { path: string; body: unknown }[] = [];
  await page.route('**/*-api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const method = route.request().method();
    if (path === '/identity-api/local/v1/tenant-sessions') {
      await route.fulfill({
        json: {
          tenantId: 'T001',
          actorId: 'A900',
          authenticationContextId: 'onboarding-fixture',
          subjectVersion: 1,
          expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
        },
      });
      return;
    }
    if (path.endsWith('/context')) {
      await route.fulfill({
        json: { tenantId: 'T001', actorId: 'A900', authority: 'TenantAdmin', policyVersion },
      });
      return;
    }
    if (path === '/uat-api/v1/setup') {
      if (method === 'POST') {
        writes.push({ path, body: route.request().postDataJSON() });
        active = true;
      }
      await route.fulfill({
        json: {
          enabled: true,
          tenantId: 'T001',
          actorId: 'A900',
          policyVersion,
          organizationName: 'Aster Financial Services',
          countryCode: 'IN',
          timeZone: 'Asia/Kolkata',
          initialAdministratorEmail: 'admin@aster.example',
          state: active ? 'Active' : 'ReadyForAdmin',
          receiptReference: active ? 'setup-fixture-receipt' : null,
          customerRelationships: true,
        },
      });
      return;
    }
    if (path.includes('/resource-directory/')) {
      await route.fulfill({
        json: {
          resourceType: 'customer',
          items: [
            {
              resourceId: 'customer-private-reference',
              displayName: 'Priya Shah',
              secondaryLabel: 'Personal customer',
            },
          ],
        },
      });
      return;
    }
    if (path.includes('/customer-relationships')) {
      if (method === 'POST') {
        writes.push({ path, body: route.request().postDataJSON() });
        assignments = [
          {
            id: 'relationship-private-reference',
            actorId: 'advisor-private-reference',
            customerId: 'customer-private-reference',
          },
        ];
        policyVersion++;
      }
      if (method === 'DELETE') {
        writes.push({ path, body: route.request().postDataJSON() });
        assignments = [];
        policyVersion++;
      }
      await route.fulfill({ json: { policyVersion, items: assignments, nextCursor: null } });
      return;
    }
    const items = path.endsWith('/members')
      ? [
          {
            actorId: 'advisor-private-reference',
            normalizedEmail: 'maya@aster.example',
            status: 'Active',
            businessRoleId: 'advisor-role',
            tenantAdmin: false,
          },
        ]
      : path.endsWith('/roles')
        ? [
            {
              id: 'advisor-role',
              code: 'advisor',
              label: 'Financial Advisor',
              permissionIds: ['profile', 'search'],
            },
          ]
        : [];
    await route.fulfill({ json: { policyVersion, items, nextCursor: null, capabilities: [] } });
  });
  return writes;
}
async function connect(page: Page, path = '/access') {
  await page.goto(path);
  await page.getByLabel('Local access key').fill('fixture-only');
  await page.getByRole('button', { name: 'Connect workspace', exact: true }).click();
}

test('first login reviews organization and access before mounting navigation', async ({ page }) => {
  const writes = await fixture(page);
  await connect(page);
  await expect(page.getByRole('heading', { name: 'A clear start for your team.' })).toBeVisible();
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Complete setup' })).toBeDisabled();
  await page.screenshot({ path: '.artifacts/onboarding-setup.png', fullPage: true });
  await page.getByLabel('I have reviewed the organization details').check();
  await page.getByLabel('I understand how roles and customer access work').check();
  expect(writes).toHaveLength(0);
  await page.getByRole('button', { name: 'Complete setup' }).click();
  await expect(
    page.getByRole('navigation', { name: 'Access Management navigation' }),
  ).toBeVisible();
  expect(writes).toHaveLength(1);
  expect(writes[0]?.body).toMatchObject({
    expectedPolicyVersion: 7,
    organizationReviewed: true,
    accessReviewed: true,
  });
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(page.getByRole('navigation')).toHaveCount(0);
});

test('named customer assignment reviews its effect and supports immediate removal', async ({
  page,
}) => {
  const writes = await fixture(page, true);
  await connect(page, '/access/resources');
  await expect(
    page.getByRole('heading', { name: 'Assigned customers', exact: true }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Assign customer', exact: true }).click();
  await page.getByRole('combobox', { name: 'Advisor', exact: true }).click();
  await page.getByTitle('maya@aster.example', { exact: true }).click();
  await page.getByRole('combobox', { name: 'Customer', exact: true }).click();
  await page.getByTitle('Priya Shah · Personal customer', { exact: true }).click();
  await page.getByRole('button', { name: 'Review assignment', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Review customer assignment', exact: true });
  await expect(dialog).toContainText('Priya Shah');
  await expect(dialog).toContainText('maya@aster.example');
  await expect(dialog).toContainText('does not grant new capabilities');
  await expect(dialog).not.toContainText('customer-private-reference');
  expect(writes).toHaveLength(0);
  await page.screenshot({ path: '.artifacts/onboarding-relationship-review.png', fullPage: true });
  await dialog.getByRole('button', { name: 'Assign customer', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(writes[0]?.body).toEqual({
    expectedPolicyVersion: 7,
    actorId: 'advisor-private-reference',
    customerId: 'customer-private-reference',
  });
  await page
    .getByRole('button', { name: 'Review removal for Priya Shah and maya@aster.example' })
    .click();
  await page.getByRole('button', { name: 'Remove assignment', exact: true }).click();
  await expect(
    page.getByText('Customer assignment removed. This relationship no longer authorizes access.'),
  ).toBeVisible();
  expect(writes).toHaveLength(2);
});

test('compact setup reflows at mobile width and supports reduced motion', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await fixture(page);
  await connect(page);
  await expect(page.getByRole('heading', { name: 'A clear start for your team.' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.getByLabel('I have reviewed the organization details').focus();
  await page.keyboard.press('Space');
  await expect(page.getByLabel('I have reviewed the organization details')).toBeChecked();
});
