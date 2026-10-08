import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: '.',
  testMatch: 'workspace.uat.spec.ts',
  workers: 1,
  timeout: 60000,
  reporter: [['list'], ['json', { outputFile: '.artifacts/uat-browser-results.json' }]],
  use: {
    baseURL: 'http://127.0.0.1:4173',
    browserName: 'chromium',
    headless: true,
    viewport: { width: 1440, height: 1000 },
    trace: 'off',
    screenshot: 'off',
    video: 'off',
  },
});
