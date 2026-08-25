import { test } from '@playwright/test';
import { IDS as DEMO_IDS } from '../src/demo/data';
import { DEMO_ENDPOINT_IDS } from '../src/demo/data/endpoints';

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
  { name: '08-applications', path: '/applications' },
  { name: '09-job-detail-failed', path: `/detail/${DEMO_IDS.failedJob}` },
  { name: '16-job-detail-retry-extension', path: `/detail/${DEMO_IDS.failedJob}` },
  { name: '10-batch-detail', path: `/detail/${DEMO_IDS.batch1}` },
  { name: '22-message-detail', path: `/detail/${DEMO_IDS.message1}` },
  { name: '11-login', path: '/' },
  { name: '12-trace', path: `/trace/${traceIdForUrl}` },
  { name: '13-worker-detail', path: `/workers/${DEMO_IDS.worker1}` },
  // '/recurring/{id}' where id is the URL-safe base64 of the definition's NAME (mirrors UrlSafeId).
  // RGFpbHkgUmVwb3J0 === base64('Daily Report') — the first demo definition.
  { name: '14-recurring-detail', path: '/recurring/RGFpbHkgUmVwb3J0' },
  { name: '15-server-detail', path: `/servers/${DEMO_IDS.server1}` },
  { name: '17-counters', path: '/counters' },
  // Endpoint observability. These four were hand-captured until the demo router grew /endpoints
  // routes, which is why they alone kept showing the pre-grouping nav. The endpoint id is the
  // URL-safe base64 of "{METHOD} {template}" (mirrors EndpointRouteId).
  { name: '23-endpoints-list', path: '/endpoints' },
  { name: '24-endpoint-detail', path: `/endpoints/${DEMO_ENDPOINT_IDS.ordersCreate}` },
  { name: '25-endpoint-call-drawer', path: `/endpoints/${DEMO_ENDPOINT_IDS.ordersCreate}/calls/${DEMO_ENDPOINT_IDS.originCall}` },
  // The job the call above spawned — its Origin card links back to that same call.
  { name: '26-job-detail-origin', path: `/detail/${DEMO_IDS.completedJobWithTrace}` },
  { name: '26-client', path: '/client' },
  { name: '27-client-event', path: '/client/events/evt-typeerror' },
  { name: '29-client-session', path: '/client/sessions/sess-8f3a2b1c' },
  { name: '31-issues', path: '/issues' },
  { name: '32-issue-detail', path: '/issues/job-nullref-processorder' },
  { name: '33-adapters', path: '/adapters' },
  { name: '34-webhooks', path: '/webhooks' },
  { name: '35-slo', path: '/slo' },
  { name: '18-concurrency-limits', path: '/concurrency' },
  { name: '19-services-list', path: '/services' },
  { name: '20-services-detail-singleton', path: '/services/JobStatsLoggerService' },
  { name: '21-services-detail-perserver', path: '/services/TickCounterService' },
  // '/applications/{id}' where id is the URL-safe base64 of the app name (mirrors UrlSafeId.Encode).
  // Y2hlY2tvdXQtd29ya2Vy === base64('checkout-worker') — the demo app with server instances + job activity.
  { name: '27-application-detail', path: '/applications/Y2hlY2tvdXQtd29ya2Vy' },
  // '/jobs/by-type/{type}' for a demo job type present in /jobs/metrics so the execution-metrics header renders.
  { name: '28-jobs-by-type-metrics', path: '/jobs/by-type/Acme.Orders.ProcessOrderRequest' },
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
