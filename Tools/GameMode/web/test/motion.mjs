// 运动自检（设计稿 12.7）：镜头按脚本逐帧移动（绕转、推拉、平移，同时跟着雅典娜走一段路、瞬移一次），
// 比较光晕、旁白气泡、建筑名牌这一帧用的屏幕位置与当帧真实投影的最大偏差。验收线 0.5 像素（设备像素）。
//   bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/motion.mjs [chromium,webkit] [--nosync]
// --nosync 故意跳过"渲染前对齐矩阵"，用来确认这项检查确实抓得住打样里那种漂移（预期远大于 0.5 像素）。
import { startServer } from './serve.mjs';
import { launch, openPage } from './playwright.mjs';

const browsers = (process.argv[2] && !process.argv[2].startsWith('--') ? process.argv[2] : 'chromium,webkit').split(',');
const nosync = process.argv.includes('--nosync');
const LIMIT = 0.5;
const server = await startServer();
let worst = 0;
try {
  for (const name of browsers) {
    const browser = await launch(name);
    try {
      const { page, problems } = await openPage(browser, `${server.origin}/polis/index.html?fixture=../fixtures/synthetic.json&selftest=motion${nosync ? '&nosync=1' : ''}`);
      await page.waitForFunction(() => document.title.startsWith('motion-max-error-px:') || document.title === 'error', null, { timeout: 180_000 });
      const r = await page.evaluate(() => ({ title: document.title, result: window.__polis.selftest, errors: window.__polis.errors }));
      if (!r.result) throw new Error(`selftest did not run: ${r.title} ${r.errors.join(' | ')}`);
      const { errors, frames, windows, maxErrorPx } = r.result;
      worst = Math.max(worst, maxErrorPx);
      console.log(`${name} ${browser.version()}: ${frames} 帧（窗口 ${windows.map(([a, b]) => `${Math.round(a)}–${Math.round(b)} ms`).join('、')}），`
        + `最大偏差 光晕 ${errors.glow.toFixed(3)} px · 旁白气泡 ${errors.bubble.toFixed(3)} px · 建筑名牌 ${errors.labels.toFixed(3)} px`
        + `${nosync ? '（未对齐矩阵）' : ''}${problems.length ? `\n  ${problems.join('\n  ')}` : ''}`);
    } finally {
      await browser.close();
    }
  }
} finally {
  await server.close();
}
const pass = nosync ? worst > LIMIT : worst <= LIMIT;
console.log(nosync
  ? `不对齐矩阵时最大偏差 ${worst.toFixed(3)} px（${pass ? '自检抓得住漂移' : '自检没抓到漂移——检查方法有问题'}）`
  : `最大偏差 ${worst.toFixed(3)} px，验收线 ${LIMIT} px：${pass ? 'PASSED' : 'FAILED'}`);
process.exit(pass ? 0 : 1);
