import { expect } from '@playwright/test';

/** @param {import('@playwright/test').Locator} button @param {string} label */
async function readyButton(button, label) {
  await expect(button).toHaveCount(1);
  await expect(button).toBeVisible();
  await expect(button).toBeEnabled();
  // Ant rejects clicks while its actual loading state is set, independently of
  // the native disabled attribute. A lingering decorative icon alone is safe.
  await expect(button).not.toHaveClass(/(?:^|\s)ant-btn-loading(?:\s|$)/);
  await expect(button).toHaveText(label);
  return button;
}

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
  return readyButton(form.locator('button[type="submit"]'), 'Ask');
}

/**
 * Retry retains the disabled payload controls while the completed request's
 * decorative icon exits. Scope is the actual named suggestion/review region.
 * @param {import('@playwright/test').Locator} scope
 * @param {'Retry suggestion'|'Retry review'} label
 */
export async function prepareAssistantRetry(scope, label) {
  await expect(scope).toHaveCount(1);
  return readyButton(scope.getByRole('button').filter({ hasText: label }), label);
}
