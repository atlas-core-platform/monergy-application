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
  const reads = { relationships: 0, members: 0, directory: 0 };
  page.on('request', (request) => {
    if (request.method() !== 'GET') return;
    const path = new URL(request.url()).pathname;
    if (path.endsWith('/customer-relationships')) reads.relationships++;
    if (path.endsWith('/members')) reads.members++;
    if (path.includes('/resource-directory/customer')) reads.directory++;
  });
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
  expect(reads).toEqual({ relationships: 2, members: 1, directory: 1 });
  expect(writes[0]?.body).toEqual({
    expectedPolicyVersion: 7,
    actorId: 'advisor-private-reference',
    customerId: 'customer-private-reference',
  });
  await page
    .getByRole('button', { name: 'Review removal for Priya Shah and maya@aster.example' })
    .click();
  const removalDialog = page.getByRole('dialog', {
    name: 'Review customer removal',
    exact: true,
  });
  await expect(removalDialog).toBeVisible();
  await removalDialog.getByRole('button', { name: 'Remove assignment', exact: true }).click();
  await expect(
    page.getByText('Customer assignment removed. This relationship no longer authorizes access.'),
  ).toBeVisible();
  expect(reads).toEqual({ relationships: 3, members: 1, directory: 1 });
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

test('customer entry stays isolated from administration and confirms reviewed evidence', async ({
  page,
}) => {
  const customerSession = {
    tenantId: 'T001',
    actorId: 'A100',
    authenticationContextId: 'customer-product-fixture',
    subjectVersion: 2,
    expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
  };
  const writes: unknown[] = [];
  const library: unknown[] = [];
  await page.route('**/*-api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/identity-api/local/v1/tenant-sessions') {
      await route.fulfill({ json: customerSession });
      return;
    }
    if (path === '/product-api/v1/context') {
      await route.fulfill({
        json: {
          tenantId: 'T001',
          actorId: 'A100',
          customerId: 'reference-customer',
          customerName: 'Reference Customer',
          customerDetail: 'Primary UAT customer',
          capabilities: [
            'evidence.document.upload',
            'evidence.document.read',
            'search.query.execute',
            'reporting.report.generate',
          ],
          environment: 'Local UAT',
          dataDurability: 'Runtime only',
          productionAccepted: false,
        },
      });
      return;
    }
    if (path === '/product-api/v1/evidence') {
      if (route.request().method() === 'GET') {
        await route.fulfill({ json: { items: library, dataDurability: 'Runtime only' } });
        return;
      }
      const body = route.request().postDataJSON() as { operationId: string };
      writes.push(body);
      const accepted = {
        documentId: `document-${body.operationId}`,
        documentVersionId: `document-version-${body.operationId}`,
        evidenceId: `evidence-${body.operationId}`,
        version: 1,
        sourceName: 'Customer upload',
        originalFileName: 'statement.txt',
        contentType: 'text/plain',
        contentLength: 17,
        contentSha256: 'b'.repeat(64),
        receivedAt: '2026-10-14T00:00:00Z',
        createdAt: '2026-10-14T00:00:00Z',
      };
      library.unshift(accepted);
      await route.fulfill({
        json: {
          operationId: body.operationId,
          ...accepted,
          acceptedAt: '2026-10-14T00:00:00Z',
          dataDurability: 'Runtime only',
        },
      });
      return;
    }
    await route.fulfill({ status: 404, json: { error: 'NOT_FOUND' } });
  });
  await page.goto('/');
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await page.getByLabel('Local access key').fill('customer-fixture');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.getByRole('heading', { name: 'Start with what you know.' })).toBeVisible();
  const navigation = page.getByRole('navigation', { name: 'Financial Workspace navigation' });
  await expect(navigation.getByRole('link', { name: 'Evidence' })).toBeVisible();
  for (const name of ['Access management', 'System Expert', 'Engineering foundation'])
    await expect(navigation.getByRole('link', { name, exact: true })).toHaveCount(0);
  await navigation.getByRole('link', { name: 'Evidence' }).click();
  await page.locator('input[type="file"]').setInputFiles({
    name: 'statement.txt',
    mimeType: 'text/plain',
    buffer: Buffer.from('customer evidence'),
  });
  const review = page.getByRole('dialog', { name: 'Review evidence upload' });
  await expect(review).toContainText('Reference Customer');
  await expect(review).toContainText('Current LOCAL/UAT runtime only');
  expect(writes).toHaveLength(0);
  await review.getByRole('button', { name: 'Upload evidence' }).click();
  const receipt = page.locator('article.product-receipt').filter({ hasText: 'statement.txt' });
  await expect(receipt.getByText('VERIFIED', { exact: true })).toBeVisible();
  await receipt.getByRole('button', { name: 'View provenance for statement.txt' }).click();
  const provenance = page.getByRole('dialog', { name: 'Evidence provenance' });
  await expect(provenance).toContainText('document-version-');
  await expect(provenance).toContainText('Raw document content is not exposed');
  expect(writes).toHaveLength(1);
  await page.screenshot({ path: '.artifacts/customer-evidence-intake.png', fullPage: true });
});
