import { expect, test } from '@playwright/test';

test('toolchain shell starts and its dialog is keyboard operable', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Monergy frontend foundation' })).toBeVisible();
  await expect(page.getByText('NOT STARTED')).toBeVisible();

  const trigger = page.getByRole('button', { name: 'View toolchain evidence' });
  await trigger.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('dialog', { name: 'D02 frontend evidence' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'D02 frontend evidence' })).toBeHidden();
  await expect(trigger).toBeFocused();
});
