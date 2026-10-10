// 用应用自带的 Playwright 驱动（bin/.../.playwright）驱动本机 Chrome，把三个固定机位截成 PNG。
// 先在仓库根目录起静态服务：python3 -I -m http.server 8765 --bind 127.0.0.1
// 用法（用驱动自带的 node）：
//   bin/Debug/net10.0/.playwright/node/darwin-arm64/node Artwork/Polis/sample/shoot.mjs <输出目录> [端口] [额外查询串，如 "&grade=0"]
// 不用 `chrome --headless --screenshot`：新版无头模式在 macOS 上拿不到显示链路，截图会一直等一帧等不到。
// SELFTEST=glow 时不截图，改跑运动自检：镜头逐帧转动，光晕与雅典娜当帧真实投影的最大偏差超过 0.5 像素就以非零码退出。
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { mkdirSync } from 'node:fs';

const require = createRequire(import.meta.url);
const driver = process.env.PLAYWRIGHT_CORE
  ?? fileURLToPath(new URL('../../../bin/Debug/net10.0/.playwright/package/', import.meta.url));
const { chromium } = require(driver);

const [out, port = '8765', extra = ''] = process.argv.slice(2);
if (!out) { console.error('用法：node shoot.mjs <输出目录> [端口] [额外查询串]'); process.exit(2); }
mkdirSync(out, { recursive: true });

const browser = await chromium.launch({ channel: 'chrome', headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') console.log(`[console.${m.type()}] ${m.text()}`); });
page.on('pageerror', (e) => console.log(`[pageerror] ${e.message}`));
// python 的静态服务偶尔会重置连接（ERR_CONNECTION_RESET），页面缺一个模块就永远到不了就绪状态；
// 所以加载失败时重试，而不是干等到超时。
async function load(url, ready) {
  for (let attempt = 1; ; attempt++) {
    try {
      await page.goto(url);
      await page.waitForFunction(ready, null, { timeout: 30_000 });
      return;
    } catch (e) {
      if (attempt >= 3) throw e;
      console.log(`[retry ${attempt}] ${url}: ${e.message.split('\n')[0]}`);
    }
  }
}
const shots = (process.env.SHOTS ?? 'game,mid,close').split(',');   // 只拍某几个机位：SHOTS=game
if (process.env.SELFTEST === 'glow') {
  let worst = 0;
  for (const shot of shots) {
    await load(`http://127.0.0.1:${port}/Artwork/Polis/sample/index.html?shot=${shot}&selftest=glow`, () => document.title.startsWith('glow-max-error-px:'));
    const px = Number((await page.title()).split(':')[1]);
    worst = Math.max(worst, px);
    console.log(`${shot}: 光晕最大偏差 ${px.toFixed(2)} px`);
  }
  await browser.close();
  process.exit(worst <= 0.5 ? 0 : 1);
}
for (const shot of shots) {
  const started = Date.now();
  await load(`http://127.0.0.1:${port}/Artwork/Polis/sample/index.html?shot=${shot}${extra}`, () => document.title === 'ready');
  const name = `${shot}${process.env.SUFFIX ?? ''}`;
  await page.screenshot({ path: `${out}/${name}.png` });
  console.log(`${out}/${name}.png  (${((Date.now() - started) / 1000).toFixed(1)}s)`);
}
await browser.close();
