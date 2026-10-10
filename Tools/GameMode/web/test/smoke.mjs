// 冒烟测试（设计稿 12.7）：Chromium 和 WebKit（大致对应 WebView2 和 WKWebView）各跑一遍。
//   回放：加载合成夹具后页面报告"就绪"、没有控制台错误、画面非空，并且动画循环在走。
//   实时：页面里先注入一个假的宿主（invokeCSharpAction，与 NativeWebView 注入的同名函数），按应用的协议推城邦、
//         事件、成果、雾、阅读器，检查页面发回的意图——就绪、收下、重新定位都发了，"批准"从没发过；
//         不可见时停止渲染；卷轴里的 HTML 只是文字。Chromium 另外关掉 WebGL 跑一遍：页面必须回报 failed（webgl）。
//   bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/smoke.mjs [chromium,webkit]
import { readFileSync } from 'node:fs';
import { startServer } from './serve.mjs';
import { launch, openPage } from './playwright.mjs';

const browsers = (process.argv[2] ?? 'chromium,webkit').split(',');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));
const city = { ...fixture, replay: null };
const server = await startServer();
let failed = false;

const HOST = () => {
  window.__hostInbox = [];
  window.invokeCSharpAction = (message) => window.__hostInbox.push(JSON.parse(message));
};

try {
  for (const name of browsers) {
    const browser = await launch(name);
    try {
      failed = !(await replayCheck(name, browser)) || failed;
      failed = !(await liveCheck(name, browser)) || failed;
    } finally {
      await browser.close();
    }
    if (name === 'chromium') failed = !(await noWebglCheck()) || failed;
  }
} finally {
  await server.close();
}
console.log(failed ? 'SMOKE FAILED' : 'SMOKE PASSED');
process.exit(failed ? 1 : 0);

async function replayCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'replay' };
  try {
    const { page, problems } = await openPage(browser, `${server.origin}/polis/index.html?fixture=../fixtures/synthetic.json&speed=4`);
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
    await page.close();
    console.log(JSON.stringify(result, null, 2));
    return Object.values(checks).every(Boolean);
  } catch (e) {
    result.error = e.message;
    console.log(JSON.stringify(result, null, 2));
    return false;
  }
}

async function liveCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);

    await send({ type: 'init', tables: { ui: { relocate: 'Relocate', accept: 'Keep' }, lines: { turn: 'On it' } } });
    await send({ type: 'depart', caption: '驶向我的工作区' });
    await send({ type: 'city', key: 'ws-1', city, player: { x: 1, z: 4 } });
    await send({ type: 'focus', last: { category: 'read', place: 'inside', building: '合同' } });
    await send({ type: 'events', events: [
      { type: 'turn', turn: 1, ageMs: 50 },
      { type: 'tool-start', id: 'w1', tool: 'write_system_file', category: 'write', place: 'inside', path: '合同/摘要.md', building: '合同', ageMs: 20 },
      { type: 'approval', waiting: true, ageMs: 10 },
    ] });
    await send({ type: 'items', items: [{ id: 'item-1', kind: 'scroll', title: '<img src=x onerror=alert(1)>', state: 'pending', path: '合同/摘要.md', createdAt: '2026-10-11T09:00:00Z' }] });
    await send({ type: 'notices', notices: [{ text: '「照片」里多了 2 个文件' }] });
    await send({ type: 'report', title: '你离开期间', lines: ['「账本」改名为「财务」'] });
    await page.waitForFunction(() => window.__polis.step === 'await', null, { timeout: 20_000 });
    const awaiting = await page.evaluate(() => ({ step: window.__polis.step, city: window.__polis.city, pixels: window.__polis.pixelStats() }));

    // 收下：打开卡片、点"收下"（按钮画在 canvas 上，这里直接走同一个处理函数）
    await page.evaluate(() => { window.__polisGame.openCard('item-1'); window.__polisGame.onButton('card-accept'); });
    // 雾与重新定位
    await send({ type: 'fog', missing: true, path: '/Users/demo/我的工作区' });
    await page.evaluate(() => window.__polisGame.onButton('relocate'));
    const fogged = await page.evaluate(() => window.__polis.fog);
    await send({ type: 'fog', missing: false });

    // 不可见：停止渲染；可见：恢复
    await send({ type: 'visibility', visible: false });
    await page.waitForTimeout(400);
    const pausedFrames = await page.evaluate(() => window.__polis.frames);
    await page.waitForTimeout(600);
    const stillPaused = await page.evaluate(() => ({ frames: window.__polis.frames, paused: window.__polis.paused }));
    await send({ type: 'visibility', visible: true });
    await page.waitForTimeout(500);
    const resumed = await page.evaluate(() => window.__polis.frames);

    // 卷轴阅读器：模型写的 HTML 只是文字
    await send({ type: 'scroll', itemId: 'item-1', title: '报告 <b>x</b>', markdown: '# 标题\n\n<img src=x onerror="window.__xss=1">\n\n- 一项' });
    const reader = await page.evaluate(() => ({
      shown: !document.getElementById('reader').hidden,
      images: document.querySelectorAll('#reader img, #reader b').length,
      text: document.getElementById('reader').textContent,
      xss: window.__xss ?? 0,
    }));

    await send({ type: 'events', events: [{ type: 'approval', waiting: false }, { type: 'tool-end', id: 'w1', ok: true }, { type: 'deliver', turn: 1, itemId: 'item-1' }] });
    await page.waitForTimeout(800);
    const landedFirst = await page.evaluate(() => window.__polis.voyage ?? null);

    // 航海过场的上限（9.2）：新城只来了顶层（partial），完整测绘迟迟不到——到上限先靠岸，不卡在海上
    await page.evaluate(() => document.getElementById('reader').hidden = true);
    await send({ type: 'depart', caption: '驶向另一座城' });
    await send({ type: 'city', key: 'ws-2', city: { ...city, buildings: city.buildings.slice(0, 2) }, partial: true });
    await page.waitForTimeout(2500);
    const stillSailing = await page.evaluate(() => window.__polis.voyage);
    await page.waitForFunction(() => window.__polis.voyage == null, null, { timeout: 9000 });
    const inbox = await page.evaluate(() => window.__hostInbox.map((m) => m.type));
    const checks = {
      readySent: inbox[0] === 'ready',
      cityLoaded: awaiting.city?.buildings === city.buildings.length && awaiting.city.key === 'ws-1',
      awaitsApproval: awaiting.step === 'await',
      nonEmptyFrame: awaiting.pixels.std > 8 && awaiting.pixels.distinct >= 16,
      acceptSent: inbox.includes('accept-delivery'),
      relocateSent: fogged === true && inbox.includes('relocate'),
      neverApproves: !inbox.some((t) => /approv|allow|grant/i.test(t)),
      pausesWhenHidden: stillPaused.paused === true && stillPaused.frames === pausedFrames,
      resumesWhenShown: resumed > stillPaused.frames,
      readerIsText: reader.shown && reader.images === 0 && reader.xss === 0 && reader.text.includes('<img src=x'),
      voyageLandsWhenReady: landedFirst == null,
      voyageWaitsForPartial: stillSailing === 'sailing',
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, inbox, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
    await page.close();
    console.log(JSON.stringify(result, null, 2));
    return Object.values(checks).every(Boolean);
  } catch (e) {
    result.error = e.message;
    console.log(JSON.stringify(result, null, 2));
    return false;
  }
}

/** 没有 WebGL 的环境（设计稿 12.5 第 3 处）：页面自检失败，必须回报 failed（webgl），不能静默。 */
async function noWebglCheck() {
  const { chromium } = await import('./playwright.mjs');
  const browser = await chromium.launch({ channel: process.env.CHROMIUM_CHANNEL ?? 'chrome', headless: true, args: ['--disable-webgl', '--disable-3d-apis'] });
  const result = { browser: `chromium ${browser.version()}`, mode: 'live, WebGL disabled' };
  try {
    const page = await browser.newPage();
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'failed' || m.type === 'ready'), null, { timeout: 60_000 });
    const sent = await page.evaluate(() => window.__hostInbox);
    const failure = sent.find((m) => m.type === 'failed');
    const checks = { reportsFailure: failure?.reason === 'webgl' && !sent.some((m) => m.type === 'ready'), detailIncluded: (failure?.detail ?? '').length > 0 };
    Object.assign(result, { checks, sent });
    console.log(JSON.stringify(result, null, 2));
    return Object.values(checks).every(Boolean);
  } catch (e) {
    result.error = e.message;
    console.log(JSON.stringify(result, null, 2));
    return false;
  } finally {
    await browser.close();
  }
}
