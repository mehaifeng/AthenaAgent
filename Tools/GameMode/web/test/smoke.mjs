// 冒烟测试（设计稿 12.7）：Chromium 和 WebKit（大致对应 WebView2 和 WKWebView）各跑一遍，
// 加载合成夹具后页面报告"就绪"、没有控制台错误、画面非空，并且动画循环在走。
//   bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/smoke.mjs [chromium,webkit]
import { startServer } from './serve.mjs';
import { launch, openPage } from './playwright.mjs';

const browsers = (process.argv[2] ?? 'chromium,webkit').split(',');
const server = await startServer();
let failed = false;
try {
  for (const name of browsers) {
    const started = Date.now();
    const browser = await launch(name);
    const result = { browser: `${name} ${browser.version()}` };
    try {
      const { page, problems } = await openPage(browser, `${server.origin}/web/index.html?fixture=fixtures/synthetic.json&speed=4`);
      await page.waitForFunction(() => window.__polis?.ready === true || document.title === 'error', null, { timeout: 90_000 });
      const first = await page.evaluate(() => ({ title: document.title, errors: window.__polis.errors, now: window.__polis.now, fixture: window.__polis.fixture }));
      await page.waitForTimeout(3000);
      const later = await page.evaluate(() => ({ now: window.__polis.now, step: window.__polis.step, errors: window.__polis.errors, pixels: window.__polis.pixelStats() }));
      const checks = {
        ready: first.title === 'ready',
        noPageErrors: later.errors.length === 0,
        noConsoleErrors: problems.length === 0,
        // 画面调成了黑白，按 5 位量化最多 32 种灰阶；全空的一帧标准差≈0、只有一两种颜色
        nonEmptyFrame: later.pixels.std > 8 && later.pixels.distinct >= 16,
        animating: later.now > first.now,
        syntheticFixture: first.fixture?.synthetic === true && first.fixture.buildings >= 8,
      };
      Object.assign(result, {
        checks,
        pixels: { mean: +later.pixels.mean.toFixed(1), std: +later.pixels.std.toFixed(1), distinct: later.pixels.distinct },
        replayMs: Math.round(later.now),
        step: later.step,
        problems: [...problems, ...later.errors],
        seconds: +((Date.now() - started) / 1000).toFixed(1),
      });
      if (!Object.values(checks).every(Boolean)) failed = true;
    } catch (e) {
      result.error = e.message;
      failed = true;
    } finally {
      await browser.close();
    }
    console.log(JSON.stringify(result, null, 2));
  }
} finally {
  await server.close();
}
console.log(failed ? 'SMOKE FAILED' : 'SMOKE PASSED');
process.exit(failed ? 1 : 0);
