import { readFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';
const profile = JSON.parse(readFileSync('.artifacts/uat/ci/profile.json', 'utf8')) as {
  identities: { tenantId: string; actorId: string; token: string }[];
  onboarding?: boolean;
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
  if (profile.onboarding) {
    await expect(page.getByRole('heading', { name: 'A clear start for your team.' })).toBeVisible();
    await expect(page.locator('.mw-shell')).toHaveCount(0);
    await page.getByLabel('I have reviewed the organization details').check();
    await page.getByLabel('I understand how roles and customer access work').check();
    await page.getByRole('button', { name: 'Complete setup' }).click();
  }
  await expect(page.getByRole('heading', { name: 'Access overview', exact: true })).toBeVisible();
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
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const users = page.getByRole('region', { name: 'Users', exact: true });
  await expect(users.getByText('uat-single@example.test', { exact: true })).toBeVisible();
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
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(users.getByText('uat-onboard@example.test', { exact: true })).toBeVisible();
  if (profile.onboarding) {
    await navigation.getByRole('link', { name: 'Resource Access', exact: true }).click();
    await page.getByRole('button', { name: 'Assign customer', exact: true }).click();
    await page.getByRole('combobox', { name: 'Advisor', exact: true }).click();
    await page.getByTitle('a300@example.test', { exact: true }).click();
    await page.getByRole('combobox', { name: 'Customer', exact: true }).click();
    await page.getByTitle('Reference Customer · Primary UAT customer', { exact: true }).click();
    await page.getByRole('button', { name: 'Review assignment', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Review customer access' })).toContainText(
      'Reference Customer',
    );
    await page
      .getByRole('dialog')
      .getByRole('button', { name: 'Assign customer', exact: true })
      .click();
    await expect(
      page.getByText(
        'Customer assigned. Access still requires role permissions and current customer Consent.',
      ),
    ).toBeVisible();
    await page
      .getByRole('button', { name: 'Review removal for Reference Customer and a300@example.test' })
      .click();
    await page.getByRole('button', { name: 'Remove assignment', exact: true }).click();
    await expect(
      page.getByText('Customer assignment removed. This relationship no longer authorizes access.'),
    ).toBeVisible();
  }
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
  await expect(
    page.getByText(
      profile.onboarding
        ? 'Durable local customer Consent, expiry and revocation'
        : 'Service scaffold; no live consent journey',
    ),
  ).toBeVisible();
  await page.getByLabel('Close', { exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.getByRole('button', { name: /Release readiness/ }).click();
  await expect(page.getByText('Local UAT · Production acceptance pending')).toBeVisible();
  await page.getByLabel('Close', { exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
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
