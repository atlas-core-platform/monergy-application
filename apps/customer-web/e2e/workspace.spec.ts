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
  await expect(page.locator('.workspace-art-chip').first()).toHaveCSS('animation-name', 'none');
  await page.getByRole('button', { name: 'Collapse sidebar' }).click();
  await expect(page.getByRole('button', { name: 'Expand sidebar' })).toHaveAttribute(
    'aria-expanded',
    'false',
  );
  const compactAccess = page
    .getByRole('navigation', { name: 'Workspace navigation' })
    .getByRole('link', { name: 'Access management' });
  await expect(compactAccess).toBeVisible();
  await expect(compactAccess.locator('svg')).toBeVisible();
  await page.getByRole('button', { name: 'Jump to a workspace', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Find a workspace' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(
    page.getByRole('button', { name: 'Jump to a workspace', exact: true }),
  ).toBeFocused();
  await page.keyboard.press('Control+k');
  await expect(page.getByRole('dialog', { name: 'Jump to a workspace' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Find a workspace' })).toBeFocused();
  await page.getByRole('textbox', { name: 'Find a workspace' }).fill('access');
  await page.getByRole('dialog').getByRole('button', { name: 'Access management' }).click();
  await expect(page).toHaveURL(/\/access$/);
  await expect(page.getByRole('button', { name: 'Connect workspace' })).toBeVisible();
  const guide = page.getByRole('button', { name: 'Access management guide' });
  await guide.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('dialog', { name: 'Your access management guide' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(guide).toBeFocused();
  await expect(page.locator('#workspace-content')).toHaveCSS('animation-name', 'none');
});

test('mobile navigation and access form remain within the viewport', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Collapse sidebar' }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Open navigation' }).click();
  await expect(
    page.getByRole('dialog').locator('.mw-nav-label').filter({ hasText: 'Access management' }),
  ).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: 'Open navigation' })).toBeFocused();
  await page.getByRole('button', { name: 'Open navigation' }).click();
  await page.getByRole('dialog').getByRole('link', { name: 'Access management' }).click();
  await expect(page.getByRole('heading', { name: 'Access starts here.' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.screenshot({
    path: '.artifacts/ui/access-mobile.png',
    fullPage: true,
    animations: 'disabled',
  });
});

test('all sidebar destinations remain reachable on a short desktop viewport', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 480 });
  await page.goto('/');
  const navigation = page.getByRole('navigation', { name: 'Workspace navigation' });
  const foundation = navigation.getByRole('link', { name: 'Engineering foundation' });
  await foundation.focus();
  await expect(foundation).toBeInViewport();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/foundation$/);
  await expect(navigation.getByRole('link', { name: 'Engineering foundation' })).toHaveAttribute(
    'aria-current',
    'page',
  );
  await expect(page.getByRole('button', { name: 'Collapse sidebar' })).toBeInViewport();
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
  await expect(page.getByRole('heading', { name: 'People. Permissions. Clarity.' })).toBeVisible();
  await page.getByRole('navigation').getByRole('link', { name: 'Users', exact: true }).click();
  await page.getByRole('button', { name: 'Import users' }).click();
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

test('bundled typography reaches drawers and review shows the exact role before saving', async ({
  page,
}) => {
  const writes: unknown[] = [];
  await page.route('**/identity-api/**', async (route) => route.fulfill({ json: session }));
  await page.route('**/access-api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/capabilities')) {
      await route.fulfill({ json: { capabilities: [] } });
      return;
    }
    if (route.request().method() === 'POST') writes.push(route.request().postDataJSON());
    await route.fulfill({
      json: {
        policyVersion: 7,
        items: path.endsWith('/permissions')
          ? [{ id: 'P1', label: 'Read financial information', code: 'financial-read' }]
          : path.endsWith('/members')
            ? [member]
            : [],
        nextCursor: null,
      },
    });
  });
  await page.goto('/access');
  await page.evaluate(() => document.fonts.load('500 15px Inter'));
  expect(
    await page.evaluate(() =>
      [...document.fonts].some((font) => font.family === 'Inter' && font.status === 'loaded'),
    ),
  ).toBe(true);
  expect(
    await page.evaluate(() =>
      performance
        .getEntriesByType('resource')
        .some((entry) => entry.name.includes('inter-latin') && entry.name.endsWith('.woff2')),
    ),
  ).toBe(true);
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await page.getByRole('navigation').getByRole('link', { name: 'Roles', exact: true }).click();
  await page.getByRole('button', { name: 'Create role', exact: true }).click();
  const editor = page.getByRole('dialog', { name: 'Create role', exact: true });
  await editor.getByLabel('Name', { exact: true }).fill('Financial analyst');
  await editor.getByLabel('Code', { exact: true }).fill('financial-analyst');
  await editor.getByRole('combobox', { name: 'Permissions' }).click();
  await page.getByTitle('Read financial information', { exact: true }).click();
  await editor.getByRole('button', { name: 'Review change' }).click();
  const review = page.getByRole('dialog', { name: 'Review new role' });
  await expect(review.getByText('Financial analyst', { exact: true })).toBeVisible();
  await expect(review.getByText('financial-analyst', { exact: true })).toBeVisible();
  await expect(
    review
      .getByRole('region', { name: 'Review changes' })
      .getByText('Read financial information', { exact: true }),
  ).toBeVisible();
  await expect(review.locator('.access-review-after').first()).toHaveCSS('font-size', '14px');
  expect(writes).toEqual([]);
  await page.screenshot({ path: '.artifacts/ui/role-review.png', animations: 'disabled' });
  await review.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(editor.getByLabel('Name', { exact: true })).toHaveValue('Financial analyst');
  expect(writes).toEqual([]);
  await editor.getByRole('button', { name: 'Review change' }).click();
  await review.getByRole('button', { name: 'Create role', exact: true }).click();
  await expect(review).not.toBeVisible();
  expect(writes).toEqual([
    {
      expectedPolicyVersion: 7,
      code: 'financial-analyst',
      label: 'Financial analyst',
      permissionIds: ['P1'],
    },
  ]);
});

test('access routes keep the session and exclude platform navigation across deep links and history', async ({
  page,
}) => {
  let signIns = 0;
  await page.route('**/identity-api/**', async (route) => {
    signIns += 1;
    await route.fulfill({ json: session });
  });
  await page.route('**/access-api/**', async (route) => {
    await route.fulfill({ json: { policyVersion: 7, items: [], nextCursor: null } });
  });
  await page.goto('/access/roles');
  const navigation = page.getByRole('navigation', { name: 'Access Management navigation' });
  for (const name of [
    'Evidence',
    'Search',
    'Reports',
    'System Expert',
    'Engineering foundation',
    'Local UAT operations',
  ]) {
    await expect(navigation.getByRole('link', { name, exact: true })).toHaveCount(0);
  }
  await expect(navigation.getByRole('link', { name: 'Roles', exact: true })).toHaveAttribute(
    'aria-current',
    'page',
  );
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.getByRole('heading', { name: 'Roles', exact: true })).toBeVisible();
  await navigation.getByRole('link', { name: 'Users', exact: true }).click();
  await page.getByRole('link', { name: 'Onboarding history', exact: true }).click();
  await expect(page).toHaveURL(/\/access\/users\/history$/);
  await expect(navigation.getByRole('link', { name: 'Users', exact: true })).toHaveAttribute(
    'aria-current',
    'page',
  );
  await page.goBack();
  await expect(page.getByRole('heading', { name: 'Users', exact: true })).toBeVisible();
  await page.goBack();
  await expect(page.getByRole('heading', { name: 'Roles', exact: true })).toBeVisible();
  await page.keyboard.press('Control+k');
  const palette = page.getByRole('dialog', { name: 'Jump to an access page' });
  await expect(palette).toBeVisible();
  await expect(palette.getByRole('button', { name: 'System Expert', exact: true })).toHaveCount(0);
  await palette.getByRole('button', { name: 'Permissions', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Permissions', exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Access Management home' }).click();
  await expect(page).toHaveURL(/\/access$/);
  await expect(page.getByRole('heading', { name: 'People. Permissions. Clarity.' })).toBeVisible();
  expect(signIns).toBe(1);
  await page.goto('/access/not-a-page');
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.getByText('This access management page does not exist.')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Monergy frontend foundation' })).toHaveCount(0);
});

test('single-user review confirms inside the drawer while the directory action remains behind it', async ({
  page,
}) => {
  const writes: { path: string; body: unknown }[] = [];
  await page.route('**/identity-api/**', async (route) => route.fulfill({ json: session }));
  await page.route('**/access-api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const result = {
      id: 'one-user',
      policyVersion: 8,
      rowCount: 1,
      identities: [{ rowNumber: 2, normalizedEmail: 'new@example.test', actorId: 'A101' }],
    };
    if (route.request().method() === 'POST')
      writes.push({ path, body: route.request().postDataJSON() });
    if (path.endsWith('/preview'))
      await route.fulfill({
        json: { valid: true, policyVersion: 7, contentHash: 'test-hash', rowCount: 1, errors: [] },
      });
    else if (path.endsWith('/commit'))
      await route.fulfill({ json: { ...result, status: 'PendingIdentity' } });
    else if (path.endsWith('/process'))
      await route.fulfill({ json: { ...result, status: 'Activated', policyVersion: 9 } });
    else await route.fulfill({ json: { policyVersion: 7, items: [], nextCursor: null } });
  });
  await page.goto('/access/users');
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await page.getByRole('button', { name: 'Create user', exact: true }).click();
  const form = page.getByRole('dialog', { name: 'Create user', exact: true });
  await form.getByLabel('Email address').fill('new@example.test');
  await form.getByRole('button', { name: 'Review user', exact: true }).click();
  const review = page.getByRole('dialog', { name: 'Review new user', exact: true });
  await expect(review.getByText('new@example.test', { exact: true })).toBeVisible();
  expect(writes.filter((write) => write.path.endsWith('/commit'))).toHaveLength(0);
  await review.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(form.getByLabel('Email address')).toHaveValue('new@example.test');
  await form.getByRole('button', { name: 'Review user', exact: true }).click();
  await review.getByRole('button', { name: 'Create user', exact: true }).click();
  await expect(
    page.getByRole('dialog').getByRole('heading', { name: 'User created.', exact: true }),
  ).toBeVisible();
  const commit = writes.find((write) => write.path.endsWith('/commit'));
  expect(commit?.body).toEqual({
    expectedPolicyVersion: 7,
    csv: 'email,role_code,group_codes\n"new@example.test","",""\n',
    contentHash: 'test-hash',
  });
  expect(writes.find((write) => write.path.endsWith('/process'))?.body).toEqual({
    expectedPolicyVersion: 8,
  });
  await expect(page.locator('.access-panel .ant-spin-spinning')).toHaveCount(0);
});
