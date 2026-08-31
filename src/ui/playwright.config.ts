import { defineConfig } from '@playwright/test';

// This run overwrites PNGs that are committed to the repo and published on the docs site, so the
// server it points at has to be THIS checkout — nothing else. Overridable because a second worktree
// (or a stale dev server from an old checkout) holding the default port is the common case:
//
//   WARP_SCREENSHOT_PORT=5189 npm run screenshots
const PORT = Number(process.env.WARP_SCREENSHOT_PORT ?? 5179);

export default defineConfig({
  testDir: './e2e',
  timeout: 30000,
  expect: { timeout: 10000 },
  fullyParallel: false,
  use: {
    baseURL: `http://localhost:${PORT}`,
    // 1920×1080. Bigger than the prior 1280×800 because the section sidebars
    // (Jobs / Batches / Messages add a 256px aside) plus the action columns on
    // list pages were getting horizontally cropped at 1280.
    viewport: { width: 1920, height: 1080 },
    actionTimeout: 10000,
  },
  webServer: {
    // --strictPort so Vite fails loudly instead of quietly moving to the next free port, which would
    // leave Playwright waiting out its timeout against a URL nothing is serving.
    command: `npx vite --mode demo --port ${PORT} --strictPort`,
    url: `http://localhost:${PORT}`,
    // NEVER reuse. This was `!process.env.CI`, which is false on CI but true locally — so a local run
    // silently adopted whatever already answered on the port and captured THAT code into this repo's
    // docs screenshots: another worktree's branch, or a stale server from an old checkout, with no
    // error and no visible clue that the images do not match the working tree. Starting a dedicated
    // server costs about a second; publishing another branch's UI as this one's documentation is not
    // recoverable by inspection, because the images look perfectly plausible.
    reuseExistingServer: false,
    // 90s, not 30s: never reusing means every local run now pays a cold Vite start, so this budget is
    // always exercised rather than skipped against a warm server. A cold start with dependency
    // optimisation overran 30s in testing, which surfaces as "Timed out waiting from
    // config.webServer" — a failure that looks like a broken config rather than a slow one.
    timeout: 90000,
  },
});
