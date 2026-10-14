import { expect } from '@playwright/test';
import type { Page } from '@playwright/test';

interface ProductSessionFixtureOptions {
  actorId?: string;
  customerId?: string;
  customerName?: string;
  customerDetail?: string;
  capabilities?: string[];
}

export async function completeProductWorkspaceConnection(page: Page) {
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await page.getByLabel('Local access key').fill('browser-product-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.locator('.mw-shell')).toBeVisible();
}

export async function connectProductWorkspace(
  page: Page,
  path = '/',
  options: ProductSessionFixtureOptions = {},
) {
  const actorId = options.actorId ?? 'A100';
  const session = {
    tenantId: 'T001',
    actorId,
    authenticationContextId: 'browser-product-session',
    subjectVersion: 1,
    expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
  };
  const context = {
    tenantId: 'T001',
    actorId,
    customerId: options.customerId ?? 'reference-customer',
    customerName: options.customerName ?? 'Alex Morgan',
    customerDetail: options.customerDetail ?? 'Personal workspace',
    capabilities: options.capabilities ?? [
      'evidence.document.upload',
      'financial-profile.profile.read',
      'search.query.execute',
      'reporting.report.generate',
    ],
    environment: 'Local UAT',
    dataDurability: 'Runtime only',
    productionAccepted: false,
  };
  await page.route('**/identity-api/**', async (route) => {
    await route.fulfill({ json: route.request().url().endsWith('/revoke') ? true : session });
  });
  await page.route('**/product-api/v1/context', async (route) => {
    await route.fulfill({ json: context });
  });
  await page.goto(path);
  await completeProductWorkspaceConnection(page);
}
