import { resolve } from 'node:path';
import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: 'pre-azure-human-auth.uat.spec.ts',
  workers: 1,
  timeout: 180000,
  expect: { timeout: 20000 },
  outputDir: resolve('.artifacts/pre-azure-human-auth-test-results'),
  reporter: [['list'], ['json', { outputFile: resolve('.artifacts/pre-azure-human-auth-browser-results.json') }]],
  use: {
    baseURL: process.env.MONERGY_PREAZURE_URL ?? 'http://127.0.0.1:4322',
    browserName: 'chromium',
    headless: true,
    viewport: { width: 1440, height: 1000 },
    actionTimeout: 20000,
    launchOptions: { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ?? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
  },
});
