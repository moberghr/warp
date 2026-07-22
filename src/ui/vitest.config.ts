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
    coverage: {
      provider: 'v8',
      reporter: ['text-summary', 'text'],
      // Gate coverage on the non-view logic only. React pages/components, thin axios/react-query
      // wrappers, demo fixtures, and generated types are integration/rendering concerns that the unit
      // suite intentionally does not assert on (#187) — including them would make the threshold noise.
      include: ['src/lib/**', 'src/stores/**', 'src/hooks/**', 'src/utils/**', 'src/config.ts'],
      thresholds: { statements: 85, branches: 80, functions: 85, lines: 85 },
    },
  },
});
