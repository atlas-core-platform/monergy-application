import { readFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';
const profile = JSON.parse(readFileSync('.artifacts/uat/ci/profile.json', 'utf8')) as {
  identities: { tenantId: string; actorId: string; token: string }[];
};
test('real tenant administration, sessions, canonical audit and service readiness', async ({
  page,
}) => {
  await page.goto('/access');
  await page
    .getByLabel('Local access key')
    .fill(
      profile.identities.find(
        (identity) => identity.tenantId === 'T001' && identity.actorId === 'A900',
      )?.token ?? '',
    );
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.getByRole('heading', { name: 'People & access', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Import people', exact: true }).click();
  await page.getByText('Paste CSV instead').click();
  await page
    .getByRole('textbox', { name: 'CSV contents' })
    .fill('email,role_code,group_codes\nuat-onboard@example.test,uat-boundary-tester,');
  await page.getByRole('button', { name: 'Validate entire file' }).click();
  await page.getByRole('button', { name: 'Confirm onboarding' }).click();
  await page.getByRole('button', { name: 'Onboard people', exact: true }).click();
  await expect(page.getByText('Your people are ready.')).toBeVisible();
  await page.getByLabel('Close', { exact: true }).click();
  await expect(page.getByText('uat-onboard@example.test', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: /Sessions & security/ }).click();
  await expect(page.getByRole('button', { name: 'Revoke all for A900' }).first()).toBeVisible();
  await page.getByLabel('Close', { exact: true }).click();
  await page.getByRole('button', { name: /Access activity/ }).click();
  await expect(page.getByText('member.sessions-revoked', { exact: true }).first()).toBeVisible();
  await page.getByLabel('Close', { exact: true }).click();
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
  expect(
    await page.evaluate(() =>
      Object.keys(localStorage)
        .concat(Object.keys(sessionStorage))
        .filter((key) => /session|token|auth/i.test(key)),
    ),
  ).toEqual([]);
});
