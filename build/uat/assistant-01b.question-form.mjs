import { expect } from '@playwright/test';

/**
 * Ant's decorative loading icon may remain in the accessibility name during its
 * exit motion after the actual request state is idle. Require the real editable
 * form and enabled submit control instead of treating that icon as button copy.
 * @param {import('@playwright/test').Page} page
 * @param {string} question
 */
export async function prepareAssistantQuestion(page, question) {
  const input = page.locator('#assistant-question');
  await expect(input).toBeEditable();
  await input.fill(question);
  const form = page.locator('form').filter({ has: input });
  await expect(form).toHaveCount(1);
  const submit = form.locator('button[type="submit"]');
  await expect(submit).toHaveCount(1);
  await expect(submit).toBeVisible();
  await expect(submit).toBeEnabled();
  // Ant rejects clicks while its actual loading state is set, independently of
  // the native disabled attribute. A lingering decorative icon alone is safe.
  await expect(submit).not.toHaveClass(/(?:^|\s)ant-btn-loading(?:\s|$)/);
  await expect(submit).toHaveText('Ask');
  return submit;
}
