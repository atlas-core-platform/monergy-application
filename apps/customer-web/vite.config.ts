import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/access-api/v1': {
        target: 'http://127.0.0.1:5088',
        rewrite: (path) => path.replace(/^\/access-api/, ''),
      },
      '/identity-api/local/v1/tenant-sessions': {
        target: 'http://127.0.0.1:5101',
        rewrite: (path) => path.replace(/^\/identity-api/, ''),
      },
      '/contracts/cid-051': {
        target: 'http://127.0.0.1:5189',
        changeOrigin: true,
      },
      '/contracts/cid-052': {
        target: 'http://127.0.0.1:5189',
        changeOrigin: true,
      },
      '/operations/local/reporting': {
        target: 'http://127.0.0.1:5189',
        changeOrigin: true,
      },
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
