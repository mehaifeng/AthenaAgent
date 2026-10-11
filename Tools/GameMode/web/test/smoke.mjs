// 冒烟测试（设计稿 12.7）：Chromium 和 WebKit（大致对应 WebView2 和 WKWebView）各跑一遍。
//   回放：加载合成夹具后页面报告"就绪"、没有控制台错误、画面非空，并且动画循环在走。
//   实时：页面里先注入一个假的宿主（invokeCSharpAction，与 NativeWebView 注入的同名函数），按应用的协议推城邦、
//         事件、成果、雾、阅读器，检查页面发回的意图——就绪、收下、重新定位都发了，"批准"从没发过；
//         不可见时停止渲染；卷轴里的 HTML 只是文字。Chromium 另外关掉 WebGL 跑一遍：页面必须回报 failed（webgl）。
//   M2：积累（橄榄林、居民、委托板、神谕、海图、藏品的摆放）的每一条意图都发得出去、形状对；传令官的行可点；
//   资产清单：一件换成外部 glTF（测试现写的 GLB）真的换上了，另一件模型不存在（404）退回程序化的，页面没有报错。
//   bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/smoke.mjs [chromium,webkit]
import { readFileSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { startServer } from './serve.mjs';
import { launch, openPage } from './playwright.mjs';

const browsers = (process.argv[2] ?? 'chromium,webkit').split(',');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));
const city = { ...fixture, replay: null };
const assetsDir = mkdtempSync(path.join(tmpdir(), 'polis-assets-'));
writeFileSync(path.join(assetsDir, 'house.glb'), boxGlb(6, 4.5, 6));
const server = await startServer(0, [['/polis-assets/', assetsDir]]);
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
      failed = !(await accumulationCheck(name, browser)) || failed;
    } finally {
      await browser.close();
    }
    if (name === 'chromium') failed = !(await noWebglCheck()) || failed;
  }
} finally {
  await server.close();
  rmSync(assetsDir, { recursive: true, force: true });
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

/**
 * M2 的积累系统：C# 推来橄榄林、居民、委托板、神谕、海图、带摆放的藏品；页面的每一种操作都发出封闭集合里的那条意图，
 * 形状对、带着 C# 给的 Id。资产清单里一件换成了外部 GLB（真的换上），一件的模型不存在（404 → 退回程序化，只记一笔警告）。
 */
async function accumulationCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, accumulation' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => {
      // 资产清单故意指向一个不存在的模型：浏览器会把那次 404 记成一条控制台错误，这一条是预期的（页面退回程序化的那一件）
      if (m.type() === 'error' && !/404|missing\.glb/i.test(m.text())) problems.push(`[console.error] ${m.text()}`);
    });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    const sentSince = async (n) => (await page.evaluate(() => window.__hostInbox)).slice(n);
    const inboxLength = () => page.evaluate(() => window.__hostInbox.length);

    await send({ type: 'init', tables: {}, assets: { 'building.house': { model: 'house.glb', scale: 1 }, 'prop.amphora': { model: 'missing.glb' } } });
    await send({ type: 'city', key: 'ws-1', city });
    await send({ type: 'grove', trees: Array.from({ length: 20 }, (_, i) => ({ conversationId: `c-${i}`, title: `委托 ${i}`, index: i, stage: i % 4, turns: 1 + i, interrupted: false, scheduled: false })) });
    await send({ type: 'residents', residents: [{ kind: 'scribe', met: false, name: '抄写员', intro: '她能替你起草报告。', tryLabel: '请雅典娜试试' }, { kind: 'smith', met: true, name: '铁匠', intro: '锻炉前跑命令。', tryLabel: '请雅典娜试试' }] });
    await send({ type: 'board', suggestions: [{ id: 'tidyAgora:', line: '广场上散着 20 个文件' }, { id: 'setOracle:', line: '在神庙设一个每日神谕' }] });
    await send({ type: 'oracle', oracle: { exists: false, title: '每日神谕', body: '还没有设立' } });
    await send({ type: 'islands', islands: [
      { id: 'sanctuary', name: '', order: 0, sanctuary: true, running: false, waiting: false, commissions: 2, current: false, missing: false },
      { id: 'ws-1', name: '我的工作区', order: 1, sanctuary: false, running: true, waiting: false, commissions: 20, current: true, missing: false },
      { id: 'ws-2', name: '家庭账本', order: 2, sanctuary: false, running: false, waiting: true, commissions: 3, current: false, missing: false },
    ] });
    await send({ type: 'items', items: [
      { id: 'kept', kind: 'scroll', title: '《周报》', state: 'accepted', createdAt: '2026-10-11T09:00:00Z' },
      { id: 'shown', kind: 'painting', title: '《海边》', state: 'accepted', placement: { venue: 'paintedStoa', slot: 2 }, createdAt: '2026-10-11T09:01:00Z' },
      { id: 'gone', kind: 'ledger', title: '《预算》', state: 'accepted', lost: true, placement: { venue: 'vault', slot: 0 }, createdAt: '2026-10-11T09:02:00Z' },
    ] });
    await send({ type: 'report', title: '你离开期间', lines: [{ text: '每日神谕送来了一卷', conversationId: 'c-3', itemId: null }, { text: '「照片」里多了 2 个文件' }] });
    await page.waitForFunction(() => window.__polis.grove?.trees === 20 && (window.__polis.residents ?? []).length === 2, null, { timeout: 20_000 });
    await page.waitForFunction(() => (window.__polis.assets?.external ?? []).length + (window.__polis.assets?.fallbacks ?? []).length === 2, null, { timeout: 30_000 });
    await page.waitForTimeout(600);
    const shape = await page.evaluate(() => {
      const g = window.__polisGame;
      const houses = [...g.world.buildings.values()].filter((b) => b.kind === 'house');
      return {
        assets: window.__polis.assets,
        externalHouse: houses.length > 0 && houses.every((b) => b.group.userData.external === true),
        venues: [...g.world.venues.keys()],
        displayed: g.world.venues.get('paintedStoa').slots[2].item,
        lostSlot: g.world.venues.get('vault').slots[0].item,
        terraces: window.__polis.grove.terraces,
        board: window.__polis.board,
        oracle: window.__polis.oracle,
        islands: window.__polis.islands,
        pixels: window.__polis.pixelStats(),
      };
    });

    // 每一种操作发出它那一条意图
    const intents = {};
    let n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; const t = g.picks.trees[4]; g.click(t.x, t.y); });
    intents.tree = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; g.openPanel('board'); g.onButton('suggestion:0'); });
    intents.board = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; g.openPanel('oracle'); g.onButton('oracle-request'); });
    intents.oracle = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; g.openResident('scribe'); g.onButton('resident-try'); });
    intents.resident = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; g.toggleChart(true); });
    await page.waitForTimeout(200);
    const chartShown = await page.evaluate(() => window.__polis.chartOpen === true && window.__polisGame.regions.some((r) => r.id === 'island:ws-2'));
    await page.evaluate(() => window.__polisGame.onButton('island:ws-2'));
    intents.island = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; g.openCard('kept'); g.onButton('card-place'); g.onButton('placing-default'); });
    intents.placeDefault = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; g.startPlacing('kept'); });
    await page.waitForTimeout(200);
    await page.evaluate(() => { const g = window.__polisGame; const s = g.picks.slots.find((x) => x.venue === 'archive' && !x.item && x.slot === 5); g.click(s.x, s.y); });
    intents.placeSlot = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; g.openCard('shown'); g.onButton('card-unplace'); g.openCard('gone'); g.onButton('card-remove'); });
    intents.unplaceRemove = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => window.__polisGame.onButton('herald-line:0'));
    intents.herald = await sentSince(n);
    const inbox = await page.evaluate(() => window.__hostInbox.map((m) => m.type));
    const of = (list, type) => list.find((m) => m.type === type);
    const checks = {
      grove: shape.terraces === 2,
      residentsSpawned: true,
      venues: ['paintedStoa', 'archive', 'vault'].every((v) => shape.venues.includes(v)),
      displayedOnPedestal: shape.displayed === 'shown' && shape.lostSlot === 'gone',
      externalModelUsed: shape.assets.external.includes('building.house') && shape.externalHouse,
      missingModelFallsBack: shape.assets.fallbacks.some((f) => f.startsWith('prop.amphora')),
      treeSelects: of(intents.tree, 'select-conversation')?.conversationId === 'c-4',
      boardTakes: of(intents.board, 'take-suggestion')?.suggestionId === 'tidyAgora:',
      oracleRequests: !!of(intents.oracle, 'request-oracle') && Object.keys(of(intents.oracle, 'request-oracle')).every((k) => k === 'v' || k === 'type'),
      residentMeetsAndTries: of(intents.resident, 'meet-resident')?.residentId === 'scribe' && of(intents.resident, 'try-resident')?.residentId === 'scribe',
      chartShownAndSelects: chartShown && of(intents.island, 'select-island')?.islandId === 'ws-2',
      placesDefault: of(intents.placeDefault, 'place-item')?.venue === 'archive' && of(intents.placeDefault, 'place-item')?.slot === undefined,
      placesOnSlot: of(intents.placeSlot, 'place-item')?.venue === 'archive' && of(intents.placeSlot, 'place-item')?.slot === 5,
      unplacesAndRemoves: of(intents.unplaceRemove, 'unplace-item')?.itemId === 'shown' && of(intents.unplaceRemove, 'remove-item')?.itemId === 'gone',
      heraldLineSelects: of(intents.herald, 'select-conversation')?.conversationId === 'c-3',
      nonEmptyFrame: shape.pixels.std > 8 && shape.pixels.distinct >= 16,
      neverApproves: !inbox.some((t) => /approv|allow|grant/i.test(t)),
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, assets: shape.assets, inbox, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
    await page.close();
    console.log(JSON.stringify(result, null, 2));
    return Object.values(checks).every(Boolean);
  } catch (e) {
    result.error = e.message;
    console.log(JSON.stringify(result, null, 2));
    return false;
  }
}

/** 一个最小的 GLB（glTF 2.0 二进制）：一只 w × h × d 的盒子，底面在 y = 0。测试现写现用，不下载任何素材。 */
function boxGlb(w, h, d) {
  const x = w / 2, z = d / 2;
  const positions = new Float32Array([-x, 0, -z, x, 0, -z, x, h, -z, -x, h, -z, -x, 0, z, x, 0, z, x, h, z, -x, h, z]);
  const indices = new Uint16Array([0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3]);
  const bin = Buffer.concat([Buffer.from(positions.buffer), Buffer.from(indices.buffer)]);
  const json = {
    asset: { version: '2.0', generator: 'athena smoke test' },
    scene: 0, scenes: [{ nodes: [0] }], nodes: [{ mesh: 0 }],
    meshes: [{ primitives: [{ attributes: { POSITION: 0 }, indices: 1 }] }],
    buffers: [{ byteLength: bin.length }],
    bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: positions.byteLength }, { buffer: 0, byteOffset: positions.byteLength, byteLength: indices.byteLength }],
    accessors: [
      { bufferView: 0, componentType: 5126, count: 8, type: 'VEC3', min: [-x, 0, -z], max: [x, h, z] },
      { bufferView: 1, componentType: 5123, count: indices.length, type: 'SCALAR' },
    ],
  };
  const pad = (buf, fill) => Buffer.concat([buf, Buffer.alloc((4 - (buf.length % 4)) % 4, fill)]);
  const jsonChunk = pad(Buffer.from(JSON.stringify(json)), 0x20);
  const binChunk = pad(bin, 0);
  const header = Buffer.alloc(12);
  header.writeUInt32LE(0x46546c67, 0); header.writeUInt32LE(2, 4); header.writeUInt32LE(12 + 8 + jsonChunk.length + 8 + binChunk.length, 8);
  const chunk = (buf, type) => { const h = Buffer.alloc(8); h.writeUInt32LE(buf.length, 0); h.writeUInt32LE(type, 4); return Buffer.concat([h, buf]); };
  return Buffer.concat([header, chunk(jsonChunk, 0x4e4f534a), chunk(binChunk, 0x004e4942)]);
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
