import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '../../apps/customer-web/e2e',
  testMatch: 'd11-persisted-reporting.spec.ts',
  fullyParallel: false,
  retries: 0,
  outputDir: '../../.artifacts/d11/playwright/test-results',
  reporter: [['list'], ['json', { outputFile: '../../.artifacts/d11/playwright/results.json' }]],
  use: {
    baseURL: 'http://127.0.0.1:5173',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
