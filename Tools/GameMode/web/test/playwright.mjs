// 应用自带的 Playwright 驱动（bin/Debug/net10.0/.playwright/package）。不装 npm 包、不下载 Chromium：
// Chromium 一侧用本机 Chrome（channel: 'chrome'），WebKit 用 Playwright 自带的 WebKit（~/Library/Caches/ms-playwright）。
// 不用 `chrome --headless --screenshot`：新版无头模式在 macOS 上拿不到显示链路，截图会一直等一帧等不到。
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const driver = process.env.PLAYWRIGHT_CORE
  ?? fileURLToPath(new URL('../../../../bin/Debug/net10.0/.playwright/package/', import.meta.url));

export const { chromium, webkit } = require(driver);

export async function launch(name) {
  if (name === 'chromium') return chromium.launch({ channel: process.env.CHROMIUM_CHANNEL ?? 'chrome', headless: true });
  if (name === 'webkit') return webkit.launch({ headless: true });
  throw new Error(`unknown browser ${name}`);
}

/** 打开页面并记下控制台错误与页面异常。 */
export async function openPage(browser, url, viewport = { width: 1600, height: 1000 }) {
  const page = await browser.newPage({ viewport });
  const problems = [];
  page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
  page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
  page.on('requestfailed', (r) => problems.push(`[requestfailed] ${r.url()} ${r.failure()?.errorText}`));
  await page.goto(url);
  return { page, problems };
}
