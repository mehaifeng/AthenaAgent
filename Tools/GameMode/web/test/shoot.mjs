// 截图：夹具城邦各截一张（?shot=1），存进被忽略的 Tools/GameMode/.local/shots/。
//   node Tools/GameMode/web/test/shoot.mjs [--fixture ../.local/real-fixture.json] [--variants "&demo=1;&demo=1&follow=athena"]
//                                           [--out <目录>] [--browser chromium|webkit] [--size 1600x1000]
// --variants 用分号隔开，每一项是追加在地址后面的参数（缺省三张：空城、带积累数据、夜里带积累数据）。
// 2026-10-13 起没有回放可截：原来按回放里的事件挑时刻（走路、读、锻炉、交付），回放连同直播一起拿掉了。
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { startServer } from './serve.mjs';
import { launch, openPage } from './playwright.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), []));
const fixture = args.fixture ?? '../fixtures/synthetic.json';
const out = args.out ?? fileURLToPath(new URL('../../.local/shots/', import.meta.url));
const browserName = args.browser ?? 'chromium';
const [width, height] = (args.size ?? '1600x1000').split('x').map(Number);
const variants = (args.variants ?? '&hud=0;&demo=1&hud=0;&demo=1&hud=0&theme=dark').split(';');
mkdirSync(out, { recursive: true });

const server = await startServer();
const browser = await launch(browserName);
try {
  for (const [k, extra] of variants.entries()) {
    const started = Date.now();
    const url = `${server.origin}/polis/index.html?fixture=${encodeURIComponent(fixture)}&shot=1${extra}`;
    const { page, problems } = await openPage(browser, url, { width, height });
    await page.waitForFunction(() => window.__polis?.ready || document.title === 'error', null, { timeout: 60_000 });
    const file = `${out}/${String(k + 1).padStart(2, '0')}${args.suffix ?? ''}.png`;
    await page.screenshot({ path: file });
    console.log(`${file}  ${extra}  (${((Date.now() - started) / 1000).toFixed(1)}s)${problems.length ? '\n  ' + problems.join('\n  ') : ''}`);
    await page.close();
  }
} finally {
  await browser.close();
  await server.close();
}
