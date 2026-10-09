import { createRequire } from 'node:module';
import { expect, test } from '@playwright/test';

const require = createRequire(import.meta.url);

test('Midnight entry, compact directory and mobile portals retain accessible controls and contrast', async ({
  page,
}, testInfo) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 1440, height: 1000 });
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.route('**/*-api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    await route.fulfill({
      json: path.endsWith('/tenant-sessions')
        ? {
            tenantId: 'T001',
            actorId: 'A900',
            authenticationContextId: 'midnight-visual-fixture',
            subjectVersion: 1,
            expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
          }
        : path.endsWith('/context')
          ? { tenantId: 'T001', actorId: 'A900', authority: 'TenantAdmin', policyVersion: 7 }
          : {
              policyVersion: 7,
              nextCursor: null,
              items: path.endsWith('/members')
                ? [
                    { actorId: 'A100', normalizedEmail: 'active@example.test', status: 'Active' },
                    {
                      actorId: 'A200',
                      normalizedEmail: 'disabled@example.test',
                      status: 'Disabled',
                    },
                  ]
                : [],
            },
    });
  });
  await page.goto('/access/users');
  await page.getByLabel('Local access key').waitFor();
  await page.addScriptTag({ path: require.resolve('axe-core/axe.min.js') });
  const capture = async (name: string) => {
    await page.evaluate(() => document.fonts.ready);
    const violations = await page.evaluate(async () => {
      const axe = (window as unknown as { axe: typeof import('axe-core') }).axe;
      return (
        await axe.run(document, {
          runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] },
        })
      ).violations.map(({ id, nodes }) => ({ id, targets: nodes.map((node) => node.target) }));
    });
    expect(violations, name).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await testInfo.attach(name, {
      body: await page.screenshot({ fullPage: true, animations: 'disabled' }),
      contentType: 'image/png',
    });
  };
  await capture('midnight-public-entry');
  await page.getByLabel('Local access key').fill('fixture-only');
  await page.getByRole('button', { name: 'Connect workspace', exact: true }).click();
  await page.getByText('active@example.test', { exact: true }).waitFor();
  await capture('midnight-users');
  await page.getByRole('combobox', { name: 'Filter loaded users by status' }).click();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(page.getByText('active@example.test', { exact: true })).toHaveCount(0);
  await expect(page.getByText('disabled@example.test', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Collapse sidebar' }).click();
  await page.getByRole('navigation').getByRole('link', { name: 'Users', exact: true }).focus();
  await expect(page.getByRole('tooltip', { name: 'Users', exact: true })).toBeVisible();
  await expect(page.locator('.mw-sidebar')).toHaveCSS('width', '64px');
  await expect(page.locator('.mw-sidebar')).toHaveCSS('transition-property', 'none');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Open navigation' }).click();
  await capture('midnight-mobile-navigation');
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Create user', exact: true }).click();
  await page.getByLabel('Email address').waitFor();
  await capture('midnight-mobile-create');
  expect(errors).toEqual([]);
});
