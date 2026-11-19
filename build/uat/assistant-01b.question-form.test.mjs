import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from '@playwright/test';
import { prepareAssistantQuestion } from './assistant-01b.question-form.mjs';

let browser;
before(async () => {
  browser = await chromium.launch({ headless: true,
    executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ?? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' });
});
after(async () => { await browser?.close(); });

function fixture(disabled) {
  return `<form><label for="assistant-question">Your question</label>
    <textarea id="assistant-question"></textarea>
    <button type="submit" ${disabled ? 'disabled' : ''}><span role="img" aria-label="loading"><svg width="1" height="1" aria-hidden="true"></svg></span> <span>Ask</span></button>
    <output id="submitted">0</output></form>
    <script>document.querySelector('form').addEventListener('submit',event=>{
      event.preventDefault();document.querySelector('#submitted').textContent='1';
    });</script>`;
}

test('editable question form submits despite lingering decorative loading accessible text', async () => {
  const page = await browser.newPage();
  try {
    await page.setContent(fixture(false));
    // Reproduces the actual physical selector mismatch without any owner mock.
    assert.equal(await page.getByRole('button', { name: 'Ask', exact: true }).count(), 0);
    assert.equal(await page.getByRole('button', { name: 'loading Ask', exact: true }).count(), 1);
    const submit = await prepareAssistantQuestion(page, 'A synthetic general product question.');
    await submit.click();
    assert.equal(await page.locator('#submitted').textContent(), '1');
    assert.equal(await page.locator('#assistant-question').inputValue(), 'A synthetic general product question.');
  } finally { await page.close(); }
});

test('an actually disabled submit control is rejected without submitting', async () => {
  const page = await browser.newPage();
  try {
    await page.setContent(fixture(true));
    await assert.rejects(prepareAssistantQuestion(page, 'A synthetic general product question.'), /toBeEnabled/);
    assert.equal(await page.locator('#submitted').textContent(), '0');
    assert.equal(await page.locator('button[type="submit"]').isDisabled(), true);
  } finally { await page.close(); }
});

test('an actual Ant loading state cannot be mistaken for a decorative exit icon', async () => {
  const page = await browser.newPage();
  try {
    await page.setContent(fixture(false));
    await page.locator('button[type="submit"]').evaluate(button => { button.classList.add('ant-btn-loading'); });
    await assert.rejects(prepareAssistantQuestion(page, 'A synthetic general product question.'), /toHaveClass/);
    assert.equal(await page.locator('#submitted').textContent(), '0');
  } finally { await page.close(); }
});
