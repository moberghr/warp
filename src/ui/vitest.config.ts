import { defineConfig } from 'vitest/config';
import path from 'node:path';

// UI unit tests (#187). happy-dom is enough for the pure-logic / store / hook tests we assert on; we do
// not render to a real canvas (Chart.js component tests are intentionally out of scope). The `@` alias
// mirrors tsconfig so tests import modules the same way the app does.
export default defineConfig({
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  test: {
    environment: 'happy-dom',
    globals: true,
    // Playwright screenshot specs under e2e/ are a separate runner.
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
  },
});
