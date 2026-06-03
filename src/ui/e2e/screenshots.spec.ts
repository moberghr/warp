import { test } from '@playwright/test';
import { IDS as DEMO_IDS } from '../src/demo/data';

const traceIdForUrl = DEMO_IDS.traceId.replace(/-/g, '');

const SCREENSHOTS_DIR = '../../website/static/img/screenshots';

// fullPage default is true for every entry — list pages, detail pages, dashboards.
// 1920px viewport + fullPage means horizontal cropping is gone and vertical scroll
// content (job logs, exception stacks, batch child tables) is captured end-to-end.
const pages = [
  { name: '01-dashboard', path: '/' },
  { name: '02-jobs-failed', path: '/jobs/failed' },
  { name: '03-job-detail-trace', path: `/detail/${DEMO_IDS.completedJobWithTrace}` },
  { name: '04-jobs-completed', path: '/jobs/completed' },
  { name: '05-messages', path: '/messages/enqueued' },
  { name: '06-batches', path: '/batches/processing' },
  { name: '07-recurring', path: '/recurring' },
  { name: '08-servers', path: '/servers' },
  { name: '09-job-detail-failed', path: `/detail/${DEMO_IDS.failedJob}` },
  { name: '16-job-detail-retry-extension', path: `/detail/${DEMO_IDS.failedJob}` },
  { name: '10-batch-detail', path: `/detail/${DEMO_IDS.batch1}` },
  { name: '22-message-detail', path: `/detail/${DEMO_IDS.message1}` },
  { name: '11-login', path: '/' },
  { name: '12-trace', path: `/trace/${traceIdForUrl}` },
  { name: '13-worker-detail', path: `/workers/${DEMO_IDS.worker1}` },
  { name: '14-recurring-detail', path: '/recurring/1' },
  { name: '15-server-detail', path: `/servers/${DEMO_IDS.server1}` },
  { name: '17-counters', path: '/counters' },
  { name: '18-concurrency-limits', path: '/concurrency' },
  { name: '19-services-list', path: '/services' },
  { name: '20-services-detail-singleton', path: '/services/JobStatsLoggerService' },
  { name: '21-services-detail-perserver', path: '/services/TickCounterService' },
];

for (const pg of pages) {
  for (const theme of ['light', 'dark'] as const) {
    const suffix = theme === 'dark' ? '-dark' : '';

    test(`${pg.name}${suffix}`, async ({ page }) => {
      // Set theme before navigation
      await page.addInitScript((t: string) => {
        localStorage.setItem('warp:theme', t);
      }, theme);

      // Build the URL with demo param (and login param for login page)
      const isLogin = pg.name === '11-login';
      if (isLogin) {
        // Set hasBuiltInLogin so the 401 flow triggers the login page
        await page.addInitScript(() => {
          (window as unknown as Record<string, unknown>).hasBuiltInLogin = true;
        });
      }
      const demoParam = isLogin ? '?demo&login' : '?demo';
      const url = `/warp${pg.path}${demoParam}`;

      await page.goto(url);

      if (isLogin) {
        // Wait for login form to appear (may take ~1-2s due to 401 flow)
        await page.locator('text=Sign in').waitFor({ timeout: 10000 });
      } else {
        // Wait for page content to load
        await page.locator('h1').first().waitFor({ timeout: 10000 });
        // Wait for charts to render if present
        await page.locator('canvas').first().waitFor({ state: 'visible', timeout: 1500 }).catch(() => {});
      }

      // Brief settle for animations
      await page.waitForTimeout(500);

      await page.screenshot({
        path: `${SCREENSHOTS_DIR}/${pg.name}${suffix}.png`,
        fullPage: true,
      });
    });
  }
}
