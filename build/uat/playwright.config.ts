import { resolve } from 'node:path';
import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: '.',
  testMatch: 'workspace.uat.spec.ts',
  workers: 1,
  timeout: 120000,
  expect: { timeout: 15000 },
  outputDir: resolve('.artifacts/uat-test-results'),
  reporter: [['list'], ['json', { outputFile: resolve('.artifacts/uat-browser-results.json') }]],
  use: {
    baseURL: 'http://127.0.0.1:4173',
    browserName: 'chromium',
    headless: true,
    viewport: { width: 1440, height: 1000 },
    actionTimeout: 15000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
  },
});
