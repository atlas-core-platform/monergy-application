import { createHash } from 'node:crypto';
import { expect, test } from '@playwright/test';

test('D11 persisted reporting uses real services, reopens and exports exact content', async ({
  page,
}) => {
  await page.goto('/reports');
  await expect(page.getByText('SERVICE-OWNED SOURCES · LOCAL ONLY')).toBeVisible();
  await expect(page.getByText('MWP-03-D11 · SYNTHETIC')).toBeVisible();

  await page.getByRole('button', { name: /Generate basic report|Refresh basic report/ }).click();
  await expect(page.getByText('Financial snapshot')).toBeVisible();
  await expect(page.getByText('Financial Profile Service').first()).toBeVisible();
  await expect(page.getByText('Financial Rules Service')).toBeVisible();
  await page.getByText(/Engineering fixture engineering\.sum · Financial Rules Service/).click();
  await expect(page.getByText(/^[a-f0-9]{32}$/).last()).toBeVisible();
  await expect(page.getByText('Audit delivery: DELIVERED')).toBeVisible();

  const reportIdCell = page
    .getByRole('rowheader', { name: 'Report ID' })
    .locator('xpath=following-sibling::td[1]');
  const exportHashCell = page
    .getByRole('rowheader', { name: 'Export SHA-256' })
    .locator('xpath=following-sibling::td[1]');
  const reportId = await reportIdCell.textContent();
  const expectedHash = await exportHashCell.textContent();
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export basic report' }).click();
  const download = await downloadPromise;
  const path = await download.path();
  if (!path) throw new Error('D11 report download path is unavailable.');
  const bytes = await import('node:fs/promises').then((fs) => fs.readFile(path));
  expect(createHash('sha256').update(bytes).digest('hex').toUpperCase()).toBe(expectedHash);

  await page.reload();
  await expect(reportIdCell).toHaveText(reportId ?? '');
  await page.getByRole('button', { name: 'Reopen persisted report' }).click();
  await expect(exportHashCell).toHaveText(expectedHash ?? '');
});
