import { resolve } from 'node:path';
import { defineConfig } from '@playwright/test';

// The running Docker UAT workspace is built with VITE_MONERGY_LOCAL_UAT=true.
// Network fixtures isolate UI assertions; verify-onboarding.py tests real owners.
export default defineConfig({
  testDir: '../../apps/customer-web/e2e',
  testMatch: ['access-entry.spec.ts', 'midnight-refinement.spec.ts', 'onboarding.spec.ts'],
  workers: 2,
  timeout: 45000,
  reporter: [
    ['list'],
    ['json', { outputFile: resolve('.artifacts/onboarding-browser-results.json') }],
  ],
  use: {
    baseURL: 'http://127.0.0.1:4173',
    browserName: 'chromium',
    headless: true,
    viewport: { width: 1440, height: 1000 },
    trace: 'off',
    video: 'off',
  },
});
