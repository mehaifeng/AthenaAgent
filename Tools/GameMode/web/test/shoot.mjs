// 截图：按给定的回放时刻各截一张（?shot=1&t=…），存进被忽略的 Tools/GameMode/.local/shots/。
//   node Tools/GameMode/web/test/shoot.mjs [--fixture ../.local/real-fixture.json] [--times 12000,30000] [--out <目录>] [--browser chromium|webkit] [--extra "&ao=0"]
// 不给 --times 时按回放里的事件自动挑五个不同的时刻：第一段走路、读、锻炉 / 港口、写、交付。
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { startServer } from './serve.mjs';
import { launch, openPage } from './playwright.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), []));
const fixture = args.fixture ?? '../fixtures/synthetic.json';
const out = args.out ?? fileURLToPath(new URL('../../.local/shots/', import.meta.url));
const browserName = args.browser ?? 'chromium';
mkdirSync(out, { recursive: true });

const server = await startServer();
const browser = await launch(browserName);
try {
  let times = args.times ? args.times.split(',').map(Number) : null;
  if (!times) {
    const { page, problems } = await openPage(browser, `${server.origin}/polis/index.html?fixture=${encodeURIComponent(fixture)}&shot=1&hud=1`);
    await page.waitForFunction(() => window.__polis?.ready || document.title === 'error', null, { timeout: 60_000 });
    if (problems.length) console.log(problems.join('\n'));
    times = await page.evaluate(() => {
      const p = window.__polis;
      p.seek(p.fixture.durationMs + 4000);
      const steps = p.steps();
      const pick = (pred, f = 0.5) => { const s = steps.find(pred); return s ? Math.round(s.start + ((s.end ?? s.start + 2000) - s.start) * f) : null; };
      const chosen = [
        pick((s) => s.kind === 'walk' && s.end - s.start > 900, 0.6),
        pick((s) => s.kind === 'act' && s.at?.startsWith('b:'), 0.5),
        pick((s) => s.kind === 'act' && (s.at === 'forge' || s.at === 'harbor' || s.at === 'library'), 0.5),
        pick((s) => s.kind === 'meditate' && (s.end ?? 0) - s.start > 2500, 0.5),
        pick((s) => s.kind === 'deliver', 0.8),
      ].filter((t) => t != null);
      return [...new Set(chosen)];
    });
    await page.close();
  }
  for (const [k, t] of times.entries()) {
    const started = Date.now();
    const url = `${server.origin}/polis/index.html?fixture=${encodeURIComponent(fixture)}&shot=1&t=${t}${args.extra ?? ''}`;
    const { page, problems } = await openPage(browser, url);
    await page.waitForFunction(() => window.__polis?.ready || document.title === 'error', null, { timeout: 60_000 });
    const file = `${out}/${String(k + 1).padStart(2, '0')}-t${t}${args.suffix ?? ''}.png`;
    await page.screenshot({ path: file });
    const step = await page.evaluate(() => window.__polis.step);
    console.log(`${file}  step=${step}  (${((Date.now() - started) / 1000).toFixed(1)}s)${problems.length ? '\n  ' + problems.join('\n  ') : ''}`);
    await page.close();
  }
} finally {
  await browser.close();
  await server.close();
}
