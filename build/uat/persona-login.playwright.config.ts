import { resolve } from 'node:path';
import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: 'persona-login.uat.spec.ts',
  workers: 1,
  timeout: 120000,
  expect: { timeout: 15000 },
  outputDir: resolve('.artifacts/persona-login-test-results'),
  reporter: [['list'], ['json', { outputFile: resolve('.artifacts/persona-login-browser-results.json') }]],
  use: {
    baseURL: process.env.MONERGY_PERSONA_UAT_URL ?? 'http://127.0.0.1:4193',
    browserName: 'chromium',
    headless: true,
    viewport: { width: 1440, height: 1000 },
    actionTimeout: 15000,
    trace: 'off',
    screenshot: 'only-on-failure',
    video: 'off',
  },
});
