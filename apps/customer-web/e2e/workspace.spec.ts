import { expect, test } from '@playwright/test';

const member = {
  actorId: 'A100',
  normalizedEmail: 'browser-test@example.test',
  status: 'Active',
  tenantAdmin: false,
  businessRoleId: null,
};
const session = {
  tenantId: 'T001',
  actorId: 'A900',
  authenticationContextId: 'browser-test-session',
  subjectVersion: 1,
  expiresAt: '2027-01-01T00:00:00Z',
};

test('workspace navigation, command palette and guide support keyboard and reduced motion', async ({
  page,
}) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'A clearer view of everything.' })).toBeVisible();
  await expect(page).toHaveTitle('Monergy Workspace');
  await page.getByRole('button', { name: 'Collapse sidebar' }).click();
  await expect(page.getByRole('button', { name: 'Expand sidebar' })).toHaveAttribute(
    'aria-expanded',
    'false',
  );
  await page.keyboard.press('Control+k');
  await expect(page.getByRole('dialog', { name: 'Jump to a workspace' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Find a workspace' })).toBeFocused();
  await page.getByRole('textbox', { name: 'Find a workspace' }).fill('access');
  await page.getByRole('dialog').getByRole('button', { name: 'Access management' }).click();
  await expect(page).toHaveURL(/\/access$/);
  await expect(page.getByRole('button', { name: 'Connect workspace' })).toBeVisible();
  const guide = page.getByRole('button', { name: 'Workspace guide' });
  await guide.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('dialog', { name: 'A connected way to work' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(guide).toBeFocused();
  await expect(page.locator('#workspace-content')).toHaveCSS('animation-name', 'none');
});

test('mobile navigation and access form remain within the viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await page.getByRole('button', { name: 'Open navigation' }).click();
  await page.getByRole('dialog').getByRole('link', { name: 'Access management' }).click();
  await expect(
    page.getByRole('heading', { name: 'Good work starts with the right access.' }),
  ).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.screenshot({
    path: '.artifacts/ui/access-mobile.png',
    fullPage: true,
    animations: 'disabled',
  });
});

test('CSV drag and drop validates the whole file and sign-out revokes the session', async ({
  page,
}) => {
  const requests: { path: string; body: unknown }[] = [];
  await page.route('**/identity-api/**', async (route) => {
    requests.push({
      path: new URL(route.request().url()).pathname,
      body: route.request().postDataJSON(),
    });
    await route.fulfill({ json: route.request().url().endsWith('/revoke') ? true : session });
  });
  await page.route('**/access-api/**', async (route) => {
    const url = route.request().url();
    if (url.endsWith('/preview')) {
      requests.push({ path: new URL(url).pathname, body: route.request().postDataJSON() });
      await route.fulfill({
        json: {
          valid: false,
          policyVersion: 1,
          contentHash: 'test',
          rowCount: 1,
          errors: [{ rowNumber: 2, field: 'email', code: 'INVALID_EMAIL' }],
        },
      });
    } else await route.fulfill({ json: { policyVersion: 1, items: [member], nextCursor: null } });
  });
  await page.goto('/access');
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.getByRole('heading', { name: 'People & access' })).toBeVisible();
  await page.getByRole('button', { name: 'Import people' }).click();
  const transfer = await page.evaluateHandle(() => {
    const data = new DataTransfer();
    data.items.add(
      new File(['email,role_code,group_codes\ninvalid,,'], 'people.csv', { type: 'text/csv' }),
    );
    return data;
  });
  await page.locator('.access-drop-zone').dispatchEvent('drop', { dataTransfer: transfer });
  await expect(page.getByRole('heading', { name: 'people.csv' })).toBeVisible();
  await page.getByRole('button', { name: 'Validate entire file' }).click();
  await expect(page.getByText('1 issues to resolve')).toBeVisible();
  expect(requests.filter((request) => request.path.endsWith('/preview'))[0]?.body).toEqual({
    csv: 'email,role_code,group_codes\ninvalid,,',
  });
  expect(requests.some((request) => request.path.endsWith('/commit'))).toBe(false);
  await page.screenshot({
    path: '.artifacts/ui/import-drawer.png',
    fullPage: true,
    animations: 'disabled',
  });
  await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('button', { name: 'Connect workspace' })).toBeVisible();
  expect(requests.find((request) => request.path.endsWith('/revoke'))?.body).toEqual({
    tenantId: 'T001',
    authenticationContextId: 'browser-test-session',
  });
  expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0]);
});
