import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  timeout: 30000,
  expect: { timeout: 10000 },
  fullyParallel: true,
  workers: process.env.CI ? 2 : undefined,
  use: {
    baseURL: 'http://localhost:5179',
    // 1920×1080. Bigger than the prior 1280×800 because the section sidebars
    // (Jobs / Batches / Messages add a 256px aside) plus the action columns on
    // list pages were getting horizontally cropped at 1280.
    viewport: { width: 1920, height: 1080 },
    actionTimeout: 10000,
  },
  webServer: {
    command: 'npx vite --mode demo --port 5179',
    url: 'http://localhost:5179',
    reuseExistingServer: !process.env.CI,
    timeout: 30000,
  },
});
