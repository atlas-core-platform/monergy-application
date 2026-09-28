import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/contracts': {
        target: 'http://127.0.0.1:5188',
        changeOrigin: true,
      },
    },
  },
  define: {
    __BUILD_REVISION__: JSON.stringify(process.env.GITHUB_SHA ?? 'LOCAL_UNCOMMITTED'),
  },
  test: {
    environment: 'jsdom',
    include: ['./tests/**/*.test.{ts,tsx}'],
    setupFiles: ['./tests/setup.ts'],
    testTimeout: 15_000,
  },
});
