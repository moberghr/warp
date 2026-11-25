import { readFileSync } from "node:fs";
import { test } from "@playwright/test";
import { IDS as DEMO_IDS } from "../src/demo/data";

// The showcase set: the handful of pages the README and the docs homepage lead with.
//
// screenshots.spec.ts captures every page at 1920px, full height, for the reference docs, where a
// reader zooms in. Those are too wide to read in a README column or a homepage figure, so this set
// is captured at a laptop-sized 1440x900 viewport and twice the pixel density: the text stays
// legible at half width and sharp on a high-density display.
const SHOWCASE_DIR = "../../website/static/img/showcase";
const README_DIR = "../../website/static/img/readme";

const traceIdForUrl = DEMO_IDS.traceId.replace(/-/g, "");

test.use({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });

const pages = [
    { name: "dashboard", path: "/" },
    { name: "job-running", path: `/detail/${DEMO_IDS.processingJob}` },
    { name: "job-failed", path: `/detail/${DEMO_IDS.failedJob}` },
    { name: "trace", path: `/trace/${traceIdForUrl}` },
    { name: "issues", path: "/issues" },
    { name: "recurring", path: "/recurring" },
];

for (const pg of pages) {
    for (const theme of ["light", "dark"] as const) {
        test(`showcase ${pg.name} ${theme}`, async ({ page }) => {
            await page.addInitScript((t: string) => {
                localStorage.setItem("warp:theme", t);
            }, theme);

            await page.goto(`/warp${pg.path}?demo`);
            await page.locator("h1").first().waitFor({ timeout: 10000 });
            await page
                .locator("canvas")
                .first()
                .waitFor({ state: "visible", timeout: 1500 })
                .catch(() => {});
            await page.waitForTimeout(500);

            await page.screenshot({
                path: `${SHOWCASE_DIR}/${pg.name}-${theme}.png`,
            });
        });
    }
}

// The README's lead image: the light dashboard capture inside a browser frame, on a transparent
// background so it sits on GitHub's light and dark page colours alike. The README is plain Markdown
// (nuget.org shows it too, and does not render HTML), so it cannot pick an image per theme or style
// one: the frame and its shadow have to be part of the picture. Runs after the captures above (one
// file, one worker, in order) because it reads the PNG they wrote.
test('readme frame', async ({ page }) => {
  const shot = readFileSync(`${SHOWCASE_DIR}/dashboard-light.png`).toString('base64');
  const line = '#d9d9e0';

  await page.setContent(`
    <body style="margin:0;background:transparent">
      <div id="frame" style="padding:28px 40px 56px;width:1200px;box-sizing:content-box">
        <div style="border:1px solid ${line};border-radius:12px;overflow:hidden;
                    box-shadow:0 24px 48px -16px rgba(20,10,60,0.28)">
          <div style="display:flex;align-items:center;gap:8px;height:40px;padding:0 16px;
                      background:#f3f3f5;border-bottom:1px solid ${line}">
            <i style="width:11px;height:11px;border-radius:50%;background:${line}"></i>
            <i style="width:11px;height:11px;border-radius:50%;background:${line}"></i>
            <i style="width:11px;height:11px;border-radius:50%;background:${line}"></i>
            <span style="margin-left:16px;flex:0 0 360px;height:24px;border-radius:6px;background:#fff;
                         border:1px solid ${line};color:#6b6b76;font:12px/24px system-ui,sans-serif;
                         padding:0 12px;box-sizing:border-box">localhost:5000/warp</span>
          </div>
          <img src="data:image/png;base64,${shot}" style="display:block;width:100%" />
        </div>
      </div>
    </body>`);
  await page.locator('img').evaluate((img: HTMLImageElement) => img.decode());

  await page.locator('#frame').screenshot({
    path: `${README_DIR}/dashboard.png`,
    omitBackground: true,
  });
});
