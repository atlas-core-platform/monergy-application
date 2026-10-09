import { readFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';
const profile = JSON.parse(readFileSync('.artifacts/uat/ci/profile.json', 'utf8')) as {
  identities: { tenantId: string; actorId: string; token: string }[];
};
test('local UAT direct routes and invalid credentials never expose the protected shell', async ({
  page,
}) => {
  for (const route of [
    '/',
    '/search',
    '/reports',
    '/foundation',
    '/vs02',
    '/operations',
    '/access/users',
  ]) {
    await page.goto(route);
    await expect(page.getByLabel('Local access key')).toBeVisible();
    await expect(page.locator('.mw-shell')).toHaveCount(0);
    await expect(page.getByRole('navigation')).toHaveCount(0);
  }
  await page.getByLabel('Local access key').fill('invalid-uat-test-key');
  await page.getByRole('button', { name: 'Connect workspace', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Unable to connect');
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  await page.getByLabel('Tenant', { exact: true }).fill('T002');
  await page
    .getByLabel('Local access key')
    .fill(
      profile.identities.find(
        (identity) => identity.tenantId === 'T001' && identity.actorId === 'A900',
      )?.token ?? '',
    );
  await page.getByRole('button', { name: 'Connect workspace', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Connect workspace', exact: true })).toBeEnabled();
  await expect(page.getByRole('alert')).toContainText('Unable to connect');
  await expect(page.locator('.mw-shell')).toHaveCount(0);
});
test('real tenant administration, sessions, canonical audit and service readiness', async ({
  page,
}) => {
  await page.goto('/access');
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await page.keyboard.press('Control+k');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page
    .getByLabel('Local access key')
    .fill(
      profile.identities.find(
        (identity) => identity.tenantId === 'T001' && identity.actorId === 'A900',
      )?.token ?? '',
    );
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(
    page.getByRole('heading', { name: 'People. Permissions. Clarity.', exact: true }),
  ).toBeVisible();
  const navigation = page.getByRole('navigation', { name: 'Access Management navigation' });
  await expect(navigation.getByRole('link', { name: 'System Expert' })).toHaveCount(0);
  await navigation.getByRole('link', { name: 'Users', exact: true }).click();
  await page.getByRole('button', { name: 'Create user', exact: true }).click();
  await page.getByLabel('Email address').fill('uat-single@example.test');
  await page.getByRole('combobox', { name: 'Business role' }).click();
  await page.getByTitle('Disposable UAT tester', { exact: true }).click();
  await page.getByRole('button', { name: 'Review user', exact: true }).click();
  await expect(
    page.getByRole('region', { name: 'Review onboarding' }).getByText('uat-single@example.test'),
  ).toBeVisible();
  await page
    .getByRole('dialog', { name: 'Review new user', exact: true })
    .getByRole('button', { name: 'Create user', exact: true })
    .click();
  await expect(page.getByText('User created.')).toBeVisible();
  await page.getByLabel('Close', { exact: true }).click();
  await expect(page.getByText('uat-single@example.test', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Import users', exact: true }).click();
  await page.getByText('Paste CSV instead').click();
  await page
    .getByRole('textbox', { name: 'CSV contents' })
    .fill('email,role_code,group_codes\nuat-onboard@example.test,uat-boundary-tester,');
  await page.getByRole('button', { name: 'Validate entire file' }).click();
  await page.getByRole('button', { name: 'Confirm onboarding' }).click();
  await page.getByRole('button', { name: 'Onboard people', exact: true }).click();
  await expect(page.getByText('User access activated.')).toBeVisible();
  await page.getByLabel('Close', { exact: true }).click();
  await expect(page.getByText('uat-onboard@example.test', { exact: true })).toBeVisible();
  await navigation.getByRole('link', { name: 'Security & Sessions', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Revoke all for A900' }).first()).toBeVisible();
  await navigation.getByRole('link', { name: 'Access Activity', exact: true }).click();
  await expect(page.getByText('All user sessions revoked', { exact: true }).first()).toBeVisible();
  await page.goto('/operations');
  await page
    .getByLabel('Local access key')
    .fill(
      profile.identities.find(
        (identity) => identity.tenantId === 'T001' && identity.actorId === 'A900',
      )?.token ?? '',
    );
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await page.getByRole('button', { name: /Connected services/ }).click();
  await expect(page.getByText('Running', { exact: true })).toHaveCount(13);
  await expect(page.getByText('Service scaffold; no live consent journey')).toBeVisible();
  await page.getByLabel('Close', { exact: true }).click();
  await page.getByRole('button', { name: /Release readiness/ }).click();
  await expect(page.getByText('Local UAT · Production acceptance pending')).toBeVisible();
  await page.getByLabel('Close', { exact: true }).click();
  await page.screenshot({ path: '.artifacts/uat-workspace.png', fullPage: true });
  // Browser refresh cannot recover a bearer token from browser storage.
  await page.reload();
  await expect(page.getByRole('button', { name: 'Connect workspace' })).toBeVisible();
  await expect(page.locator('.mw-shell')).toHaveCount(0);
  expect(
    await page.evaluate(() =>
      Object.keys(localStorage)
        .concat(Object.keys(sessionStorage))
        .filter((key) => /session|token|auth/i.test(key)),
    ),
  ).toEqual([]);
});
