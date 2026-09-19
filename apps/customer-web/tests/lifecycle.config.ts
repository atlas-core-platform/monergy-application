import { defineConfig } from 'vitest/config';

import base from '../vite.config';

// Diagnostic reproduction is separate from the unchanged seven-test suite.
export default defineConfig({
  ...base,
  test: { ...base.test, include: ['./tests/FormLifecycle.diagnostic.tsx'] },
});
