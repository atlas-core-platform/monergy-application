import { resolve } from 'node:path';
import { defineConfig } from '@playwright/test';
import { privateOutputDirectory } from './assistant-01b.guards.mjs';

// Deliberately not selected by the ordinary browser suite. Starting Gate D is a
// separate, explicitly authorized operation after the frozen source gates.
if (!process.argv.includes('--list') && process.env.MONERGY_ASSISTANT_GATE_D_AUTHORIZED !== 'true')
  throw new Error('Gate D authorization and a protected fixture are required.');

export default defineConfig({
  testDir: '.', testMatch: 'assistant-01b.uat.spec.ts', workers: 1, retries: 0,
  timeout: 120000, expect: { timeout: 15000 },
  outputDir: privateOutputDirectory({ listing: process.argv.includes('--list'), repository: resolve('.'),
    fixturePath: process.env.MONERGY_ASSISTANT_UAT_FIXTURE, outputPath: process.env.MONERGY_ASSISTANT_RAW_ARTIFACTS }),
  reporter: [[resolve('build/uat/assistant-01b.reporter.ts')]],
  use: {
    browserName: 'chromium', headless: process.env.MONERGY_ASSISTANT_VISIBLE !== 'true',
    viewport: { width: 1440, height: 1000 }, actionTimeout: 15000,
    trace: 'off', screenshot: 'off', video: 'off',
    launchOptions: { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ??
      'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' },
  },
});
