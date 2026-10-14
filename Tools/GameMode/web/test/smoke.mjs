// 冒烟测试（设计稿 12.7）：Chromium 和 WebKit（大致对应 WebView2 和 WKWebView）各跑一遍。
//   夹具：加载合成夹具后页面报告"就绪"、没有控制台错误、画面非空，并且动画循环在走。
//   实时：页面里先注入一个假的宿主（invokeCSharpAction，与 NativeWebView 注入的同名函数），按应用的协议推城邦、
//         成果、对话栏、雾、阅读器，检查页面发回的意图——就绪、收下、重新定位都发了，"批准"从没发过；
//         刚做完的成果直接出现在你脚下，装城时就在的排在广场南边；等审批只在对话栏上（红封印）；
//         工具 / 推理 / 审批事件（events）、"转向这份委托"（focus）页面已经不认（2026-10-13：直播拿掉了）；
//         不可见时停止渲染；卷轴里的 HTML 只是文字。Chromium 另外关掉 WebGL 跑一遍：页面必须回报 failed（webgl）。
//   M2：积累（橄榄林、居民、委托板、神谕、海图、藏品的摆放）的每一条意图都发得出去、形状对；传令官的行可点；
//   资产清单：一件换成外部 glTF（测试现写的 GLB，带内嵌贴图）真的换上了、贴图读进了材质，另一件模型不存在（404）退回程序化的，页面没有报错；
//   雅典娜换成一个 A-pose 人台、蒙在程序化骨架上（rig: procedural）——身体是蒙皮网格，手里的东西还在。
//   操作（2026-10-11）：视角缺省自由、V 轮换、左键拖动转镜头；WASD 走路且走不进房子；点雅典娜打开对话栏；对话栏收到 composer
//   才出现，Enter 托付（say 带会话 Id）、草稿同步（draft）、她在办事时"请她停下"（stop-turn）；在对话栏里按 W 是字不是脚步，
//   输入法选字时的 Enter 不托付；预填（composer open）展开并聚焦；你在远处开口，她过来（远了化作一道光）。
//   居民即会话（M-B）：街上最多 32 位、最近的不超过 4 位全套；走到一位跟前按 E 开他的卡片，卡片的按钮选中那场会话；碑上刻着老居民。
//   聊天（2026-10-13）：卡片上的"聊几句"把对话栏借给他，回车只发 chat；他的话流式进他头顶的气泡，栏回到"你在写"，Esc 告辞；页面从不自己开口。
//   辩论（M-E）：讲台前按 E 开讲台的卡片；议题与论点在借给辩论的对话栏里写，回车只发 debate-start / debate-say；判票时居民围过来，判完按票数挥手；"结束"与 Esc 发 debate-end。
//   户与民居（M-D）：居民先到、民居后到时城一变就从民居门前重新出门；民居的名牌写户名；新立的一户雅典娜说一句；户名变了名牌跟着换。
//   bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/smoke.mjs [chromium,webkit]
import { readFileSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { deflateSync, crc32 } from 'node:zlib';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { startServer } from './serve.mjs';
import { launch, openPage } from './playwright.mjs';
import { mannequinGlb } from './mannequin.mjs';

const browsers = (process.argv[2] ?? 'chromium,webkit').split(',');
// 迭代时只跑其中几组：SMOKE_ONLY=chatCheck,debateCheck（函数名，逗号分隔）。正式运行不设。
const only = process.env.SMOKE_ONLY ? new Set(process.env.SMOKE_ONLY.split(',').map((x) => x.trim())) : null;
const wanted = (check) => !only || only.has(check.name);
const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));
const city = fixture;
const assetsDir = mkdtempSync(path.join(tmpdir(), 'polis-assets-'));
writeFileSync(path.join(assetsDir, 'house.glb'), boxGlb(6, 4.5, 6));
writeFileSync(path.join(assetsDir, 'mannequin.glb'), mannequinGlb());
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
      for (const check of [fixtureCheck, deliveryCheck, accumulationCheck, citizensCheck, relationsCheck, chorusCheck, doorCheck, craftCheck, chatCheck, debateCheck, householdCheck, controlsCheck]) {
        if (wanted(check)) failed = !(await check(name, browser)) || failed;
      }
    } finally {
      await browser.close();
    }
    if (name === 'chromium' && wanted(noWebglCheck)) failed = !(await noWebglCheck()) || failed;
  }
} finally {
  await server.close();
  rmSync(assetsDir, { recursive: true, force: true });
}
console.log(failed ? 'SMOKE FAILED' : 'SMOKE PASSED');
process.exit(failed ? 1 : 0);

async function fixtureCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'fixture' };
  try {
    const { page, problems } = await openPage(browser, `${server.origin}/polis/index.html?fixture=../fixtures/synthetic.json&demo=1`);
    await page.waitForFunction(() => window.__polis?.ready === true || document.title === 'error', null, { timeout: 90_000 });
    const first = await page.evaluate(() => ({ title: document.title, errors: window.__polis.errors, now: window.__polis.now, frames: window.__polis.frames, fixture: window.__polis.fixture }));
    await page.waitForTimeout(3000);
    const later = await page.evaluate(() => ({ now: window.__polis.now, frames: window.__polis.frames, athena: window.__polis.athena, errors: window.__polis.errors, pixels: window.__polis.pixelStats() }));
    const checks = {
      ready: first.title === 'ready',
      noPageErrors: later.errors.length === 0,
      noConsoleErrors: problems.length === 0,
      // 全空的一帧标准差≈0、只有一两种颜色
      nonEmptyFrame: later.pixels.std > 8 && later.pixels.distinct >= 16,
      animating: later.now > first.now && later.frames > first.frames,
      athenaLivesHere: ['idle', 'walk', 'teleport'].includes(later.athena),
      syntheticFixture: first.fixture?.synthetic === true && first.fixture.buildings >= 8,
    };
    Object.assign(result, {
      checks,
      pixels: { mean: +later.pixels.mean.toFixed(1), std: +later.pixels.std.toFixed(1), distinct: later.pixels.distinct },
      clockMs: Math.round(later.now),
      athena: later.athena,
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

async function deliveryCheck(name, browser) {
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

    await send({ type: 'init', tables: { ui: { relocate: 'Relocate', accept: 'Keep' } } });
    await send({ type: 'depart', caption: '驶向我的工作区' });
    await send({ type: 'city', key: 'ws-1', city, player: { x: 1, z: 4 } });
    // 装城时就在的一件（重启、你离开期间交付的）：排在广场南边，不在你脚下
    await send({ type: 'items', items: [{ id: 'item-0', kind: 'scroll', title: '旧卷', state: 'pending', createdAt: '2026-10-10T09:00:00Z' }] });
    await send({ type: 'notices', notices: [{ text: '「照片」里多了 2 个文件' }] });
    await send({ type: 'report', title: '你离开期间', lines: ['「账本」改名为「财务」'] });
    // 直播拿掉之后（2026-10-13）页面不再认工具 / 推理 / 审批事件，也没有"转向这份委托"：送进去只记一笔"认不得"
    await send({ type: 'events', events: [{ type: 'tool-start', id: 'w1', tool: 'write_system_file' }] });
    await send({ type: 'focus', last: null });
    // 对话栏：这份委托在办事、在等你点头——栏上一枚红封印
    await send({ type: 'composer', conversationId: 'c-1', busy: true, queued: false, waiting: true, compressing: false, canSend: false, attachments: [] });
    await page.waitForFunction(() => (window.__polis.frames ?? 0) > 3 && (window.__polis.shownItems ?? []).some((i) => i.id === 'item-0'), null, { timeout: 20_000 });
    const loaded = await page.evaluate(() => ({
      city: window.__polis.city, pixels: window.__polis.pixelStats(), player: window.__polis.player,
      row: (window.__polis.shownItems ?? []).find((i) => i.id === 'item-0'),
      sealed: document.getElementById('talk').classList.contains('waiting'),
      placeholder: document.querySelector('#talk .talk-input').placeholder,
      errors: window.__polis.errors,
      athena: window.__polis.athena,
    }));
    await send({ type: 'composer', conversationId: 'c-1', busy: false, queued: false, waiting: false, compressing: false, canSend: true, attachments: [] });
    const unsealed = await page.evaluate(() => !document.getElementById('talk').classList.contains('waiting'));

    // 刚做完的一回合：C# 推来整份清单，里面多了一件待收的——雅典娜拿着它跑过来递给你（2026-10-13，用户）；
    // 你走开了她就跟着（从她此刻的位置改道），等你停下再递，卷轴落在你脚下。你先站到港口：离她远，她得跑过来
    await page.evaluate(() => { const g = window.__polisGame, h = g.layout.sitePoint('harbor'); g.placePlayer(h.x, h.z - 1.2, Math.PI); });
    await page.waitForTimeout(600);
    await send({ type: 'items', items: [
      { id: 'item-0', kind: 'scroll', title: '旧卷', state: 'pending', createdAt: '2026-10-10T09:00:00Z' },
      { id: 'item-1', kind: 'scroll', title: '<img src=x onerror=alert(1)>', state: 'pending', path: '合同/摘要.md', createdAt: '2026-10-11T09:00:00Z' },
    ] });
    await page.waitForTimeout(300);
    const handing = await page.evaluate(() => ({ handover: window.__polis.handover, shown: (window.__polis.shownItems ?? []).some((i) => i.id === 'item-1'), step: window.__polisGame.athenaSim.state().step }));
    const chaseTo = handing.step?.to ?? null;
    // 你走开几步（挑一个能站、周围也能站的地方——placePlayer 不管能不能站，真人走不进海里）
    await page.evaluate(() => {
      const g = window.__polisGame, p = g.player, w = g.layout.walkable;
      const ok = (x, z) => w({ x, z }) && [0, 1, 2, 3].every((k) => w({ x: x + Math.sin(k * Math.PI / 2) * 1.2, z: z + Math.cos(k * Math.PI / 2) * 1.2 }));
      for (const [dx, dz] of [[-4, -3], [4, -3], [0, -4], [-4, -6], [4, -6], [0, -7]]) if (ok(p.x + dx, p.z + dz)) { g.placePlayer(p.x + dx, p.z + dz, 0); return; }
    });
    await page.waitForTimeout(900);
    const followed = await page.evaluate(() => { const st = window.__polisGame.athenaSim.state().step; return { to: st.to ?? st.at, kind: st.kind, reason: st.reason ?? null }; });
    const handedOver = await page.waitForFunction(() => window.__polis.handed === 'item-1' && (window.__polis.shownItems ?? []).some((i) => i.id === 'item-1'), null, { timeout: 45_000 }).then(() => true, () => false);
    const delivered = await page.evaluate(() => ({ player: window.__polis.player, shown: window.__polis.shownItems.find((i) => i.id === 'item-1'), action: window.__polis.playerAction, athena: { x: window.__polisGame.world.athena.position.x, z: window.__polisGame.world.athena.position.z } }));

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

    await page.waitForTimeout(800);
    const landedFirst = await page.evaluate(() => window.__polis.voyage ?? null);
    const waitingText = await page.evaluate(() => window.__polisGame.ui.talkWaiting);

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
      existingItemWaitsOnTheRow: !!loaded.row && Math.hypot(loaded.row.x - loaded.player.x, loaded.row.z - loaded.player.z) > 2.5,
      freshItemHandedByAthena: handing.handover?.itemId === 'item-1' && !handing.shown && handing.step?.kind === 'run' && handing.step?.reason === 'chase'
        && followed.to !== chaseTo && followed.reason === 'chase'
        && handedOver && !!delivered.shown && Math.hypot(delivered.shown.x - delivered.player.x, delivered.shown.z - delivered.player.z) < 1.5
        && Math.hypot(delivered.athena.x - delivered.player.x, delivered.athena.z - delivered.player.z) < 2.4
        && Math.hypot(delivered.athena.x - delivered.player.x, delivered.athena.z - delivered.player.z) > 0.5,   // 站在你跟前，不是站进你身体里
      cityLoaded: loaded.city?.buildings === city.buildings.length && loaded.city.key === 'ws-1',
      approvalOnlyOnTheBar: loaded.sealed && loaded.placeholder === waitingText && unsealed,
      noLiveMirror: loaded.errors.some((e) => e.includes('unknown host message: events')) && loaded.errors.some((e) => e.includes('unknown host message: focus'))
        && ['idle', 'walk', 'teleport'].includes(loaded.athena),
      nonEmptyFrame: loaded.pixels.std > 8 && loaded.pixels.distinct >= 16,
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
    Object.assign(result, { checks, inbox, delivery: { row: loaded.row, handing, chaseTo, followed, handedOver, fresh: delivered }, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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

    await send({ type: 'init', tables: {}, assets: { 'building.house': { model: 'house.glb', scale: 1 }, 'prop.amphora': { model: 'missing.glb' }, 'figure.athena': { model: 'mannequin.glb', rig: 'procedural' } } });
    await send({ type: 'city', key: 'ws-1', city });
    await send({ type: 'grove', trees: Array.from({ length: 20 }, (_, i) => ({ conversationId: `c-${i}`, title: `委托 ${i}`, index: i, stage: i % 4, turns: 1 + i, interrupted: false, scheduled: false })) });
    await send({ type: 'residents', residents: [{ kind: 'scribe', met: false, name: '抄写员卡德摩斯', trade: '抄写员', greekName: '卡德摩斯', intro: '我在档案馆抄写文书。' }, { kind: 'smith', met: true, name: '铁匠赫菲斯提昂', trade: '铁匠', greekName: '赫菲斯提昂', intro: '锻炉这边是干粗活的地方。' }] });
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
    await page.waitForFunction(() => (window.__polis.assets?.external ?? []).length + (window.__polis.assets?.fallbacks ?? []).length === 3, null, { timeout: 30_000 });
    await page.waitForTimeout(600);
    const shape = await page.evaluate(() => {
      const g = window.__polisGame;
      const houses = [...g.world.buildings.values()].filter((b) => b.kind === 'house');
      return {
        assets: window.__polis.assets,
        externalHouse: houses.length > 0 && houses.every((b) => b.group.userData.external === true),
        // GLB 里内嵌的贴图真的读进了材质（CSP 拦掉 blob: 时模型照样换上，只是没有贴图）
        externalTextured: houses.length > 0 && houses.every((b) => {
          let textured = false;
          b.group.traverse((o) => { if (o.isMesh && (o.material?.map?.image?.width ?? 0) > 0) textured = true; });
          return textured;
        }),
        // 蒙在程序化骨架上的雅典娜（rig: procedural）：外部网格是蒙皮网格、十四根骨头，程序化的身体拆光了，手里的东西还在
        athenaRig: (() => {
          const f = g.world.athenaFigure, root = g.world.athena;
          let skinned = 0, procedural = 0;
          root.traverse((o) => { if (o.isSkinnedMesh) skinned++; else if (o.isMesh && ![...f.props.values()].some((p) => p === o || p.getObjectById(o.id))) procedural++; });
          return { rig: root.userData.rig ?? null, skinned, procedural, props: f.props.size, bones: f.skeleton?.bones.length ?? 0 };
        })(),
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
    // M3 第三轮：城里的东西不再是点的——走到第 5 棵树跟前、面朝它，按 E（真的按键，走 window 上的那一条监听）
    await page.evaluate(() => { const g = window.__polisGame; const t = g.world.groveTrees[4]; g.setFollow('player'); g.placePlayer(t.base.x + 1.0, t.base.z, -Math.PI / 2); });
    await page.waitForTimeout(150);
    const treeFocus = await page.evaluate(() => window.__polis.focus);
    await page.keyboard.press('KeyE');
    intents.tree = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; g.openPanel('board'); g.onButton('suggestion:0'); });
    intents.board = await sentSince(n);
    n = await inboxLength();
    await page.evaluate(() => { const g = window.__polisGame; g.openPanel('oracle'); g.onButton('oracle-request'); });
    intents.oracle = await sentSince(n);
    n = await inboxLength();
    // 职业居民（2026-10-13）：卡片上没有"请雅典娜试试"，有"聊几句"——对话栏借给他，回车发 chat（who: resident:scribe）
    await page.evaluate(() => window.__polisGame.openResident('scribe'));
    await page.waitForTimeout(250);   // 按钮区域是画完一帧才有的
    const residentCard = await page.evaluate(() => { const g = window.__polisGame; return { view: g.panelView(), regions: g.regions.map((r) => r.id) }; });
    await page.evaluate(() => window.__polisGame.onButton('resident-chat'));
    await page.waitForTimeout(150);
    const residentLent = await page.evaluate(() => ({ talk: window.__polisTalk.state, chatting: window.__polisGame.chatting, name: document.querySelector('#talk .talk-name').textContent, closeUp: window.__polis.closeUp }));
    await page.keyboard.type('你在忙什么？');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(150);
    await send({ type: 'talk', who: 'resident:scribe', n: 3, text: '在抄一份周报呢。', done: true, status: 'ok' });
    await page.waitForTimeout(150);
    const residentBubble = await page.evaluate(() => (window.__polisGame.used?.citizenBubbles ?? []).find((b) => b.id === 'resident:scribe')?.text ?? null);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(100);
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
    // 摆放：走到书库的第 6 个空底座跟前按 E
    await page.evaluate(() => { const g = window.__polisGame; const s = g.world.venues.get('archive').slots[5]; g.setFollow('player'); g.placePlayer(s.world.x, s.world.z + 0.9, Math.PI); });
    await page.waitForTimeout(150);
    await page.keyboard.press('KeyE');
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
      externalTextureLoaded: shape.externalTextured,
      externalFigureOnProceduralRig: shape.athenaRig.rig === 'procedural' && shape.athenaRig.skinned > 0 && shape.athenaRig.procedural === 0 && shape.athenaRig.props > 0 && shape.athenaRig.bones === 14,
      missingModelFallsBack: shape.assets.fallbacks.some((f) => f.startsWith('prop.amphora')),
      treeSelects: treeFocus?.type === 'tree' && of(intents.tree, 'select-conversation')?.conversationId === 'c-4',
      boardTakes: of(intents.board, 'take-suggestion')?.suggestionId === 'tidyAgora:',
      oracleRequests: !!of(intents.oracle, 'request-oracle') && Object.keys(of(intents.oracle, 'request-oracle')).every((k) => k === 'v' || k === 'type'),
      residentMeetsAndChats: of(intents.resident, 'meet-resident')?.residentId === 'scribe' && of(intents.resident, 'chat')?.who === 'resident:scribe'
        && !intents.resident.some((m) => m.type === 'try-resident' || m.type === 'say') && residentCard.view?.title === '抄写员卡德摩斯' && residentCard.regions.includes('resident-chat')
        && !residentCard.regions.includes('resident-try') && residentLent.talk.mode === 'chat' && residentLent.name === '抄写员卡德摩斯' && residentLent.closeUp === 'resident'
        && residentBubble === '在抄一份周报呢。',
      chartShownAndSelects: chartShown && of(intents.island, 'select-island')?.islandId === 'ws-2',
      placesDefault: of(intents.placeDefault, 'place-item')?.venue === 'archive' && of(intents.placeDefault, 'place-item')?.slot === undefined,
      placesOnSlot: of(intents.placeSlot, 'place-item')?.venue === 'archive' && of(intents.placeSlot, 'place-item')?.slot === 5,
      unplacesAndRemoves: of(intents.unplaceRemove, 'unplace-item')?.itemId === 'shown' && of(intents.unplaceRemove, 'remove-item')?.itemId === 'gone',
      heraldLineSelects: of(intents.herald, 'select-conversation')?.conversationId === 'c-3',
      nonEmptyFrame: shape.pixels.std > 8 && shape.pixels.distinct >= 16,
      neverApproves: !inbox.some((t) => /approv|allow|grant/i.test(t)),
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, assets: shape.assets, resident: { card: { title: residentCard.view?.title, regions: residentCard.regions }, lent: { mode: residentLent.talk.mode, name: residentLent.name, closeUp: residentLent.closeUp }, bubble: residentBubble, sent: intents.resident }, inbox, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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
 * 居民即会话（M-B）：C# 推 40 位上街（它本来只推 32 位，页面再守一道）→ 场景里最多 32 个 figure:citizen:*；最近的不超过 4 位全套；
 * 走到一位闲着的居民跟前按 E 开他的卡片（台词 + 人口一行），卡片上的按钮发 select-conversation；老居民的名字刻在碑上，碑也能按 E 看。
 */
/**
 * 人物关系（第三轮需求 1）：C# 推来的 relations 进城——左上角"人际"打开总览（谁怎么看你、按分数排、职业居民另成一组、
 * 点一个人看他合得来与合不来的人）；开着时方向键不走路，Esc 收起；打招呼按他怎么看你（厌恶的转过身去不说话，亲近的挥手说热络的话）。
 */
async function relationsCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, relations' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    await send({ type: 'init', tables: {} });
    await send({ type: 'city', key: 'ws-1', city });
    await send({ type: 'residents', residents: [{ kind: 'smith', met: true, name: '铁匠赫菲斯提昂' }, { kind: 'priestess', met: true, name: '女祭司忒弥斯托克勒亚' }] });
    const homes = ['b:网站', 'b:合同', 'b:账本', 'b:照片', 'b:下载', 'b:杂物', 'agora:0', 'harbor'];
    const street = homes.map((home, i) => ({
      id: `rel-${i}`, seed: (Math.imul(i + 11, 2654435761) >>> 0), female: i % 2 === 1, name: `第${i}位`, title: `委托 ${i}`,
      home, age: 'adult', outcome: 'completed', folders: [], turns: 2, trade: 'potter',
    }));
    const indoors = [{ id: 'in-1', seed: 777, female: true, name: '屋里的甲', home: 'b:杂物', age: 'young', trade: 'weaver' }];
    await send({ type: 'citizens', citizens: { street, indoors, elders: [], census: { total: 9, street: 8, indoors: 1, elders: 0 } } });
    await page.waitForFunction(() => {
      const g = window.__polisGame;
      return g.world.citizens.size === 8 && [...g.world.citizens.values()].every((c) => c.figure && c.plan);
    }, null, { timeout: 60_000 });
    const scores = [70, -80, 30, 5, -30, 10, 0, 45];
    const you = Object.fromEntries(street.map((c, i) => [c.id, { score: scores[i], band: scores[i] >= 60 ? 'close' : scores[i] >= 25 ? 'friendly' : scores[i] > -25 ? 'neutral' : scores[i] > -60 ? 'cool' : 'hostile', patience: 3, opens: true, comesOut: scores[i] >= 25, why: i === 0 ? ['done', 'chats'] : [], talk: 0.5 }]));
    you['in-1'] = { score: 20, band: 'neutral', patience: 3, opens: true, comesOut: false, why: [], talk: 0.5 };
    you['resident:smith'] = { score: 6, band: 'neutral', patience: 3, opens: true, comesOut: false, why: ['nature'], talk: 0.5 };
    you['resident:priestess'] = { score: 28, band: 'friendly', patience: 4, opens: true, comesOut: true, why: ['nature'], talk: 0.5 };
    await send({ type: 'relations', relations: { you, pairs: [['rel-0', 'rel-2', 50, 40], ['rel-0', 'rel-1', -70, -65]], ties: { 'rel-0': { friends: [['rel-2', 50]], rivals: [['rel-1', -70]] } } } });
    await page.waitForTimeout(200);
    const relStatus = await page.evaluate(() => window.__polis.relations);

    // —— 总览：工具条上的"人际"打开；按分数排、职业居民另成一组；点第一位看他合得来的人 ——
    const buttonLabel = await page.evaluate(() => document.getElementById('btn-relations').textContent);
    await page.click('#btn-relations');
    await page.waitForTimeout(150);
    const panel = await page.evaluate(() => {
      const root = document.getElementById('relations');
      const rows = [...root.querySelectorAll('.rel-row')].map((r) => ({ id: r.dataset.id, text: r.textContent }));
      return { open: !root.hidden, rows, headers: [...root.querySelectorAll('.rel-list h3')].map((h) => h.textContent) };
    });
    await page.click('#relations .rel-row[data-id="rel-0"]');
    await page.waitForTimeout(100);
    const detail = await page.evaluate(() => {
      const d = document.querySelector('#relations .rel-detail');
      return { name: d?.querySelector('h2')?.textContent ?? null, why: d?.querySelector('.rel-why')?.textContent ?? null, links: [...(d?.querySelectorAll('.rel-link') ?? [])].map((l) => l.textContent) };
    });
    // 开着时方向键不走路
    const before = await page.evaluate(() => window.__polisGame.player);
    await page.keyboard.down('KeyW');
    await page.waitForTimeout(300);
    await page.keyboard.up('KeyW');
    const after = await page.evaluate(() => window.__polisGame.player);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(100);
    const closed = await page.evaluate(() => document.getElementById('relations').hidden);

    // —— 打招呼按他怎么看你：挑一位此刻闲着的人，先让他厌恶你（转过身去、不说话），再挑一位让他亲近你（挥手、说热络的话） ——
    const greetCheck = async (score, band) => {
      // 等一位此刻闲着、还要待一阵、还没打过招呼的人（日程 150 秒一圈，闲着的时候多）
      const id = await page.waitForFunction(() => {
        const g = window.__polisGame;
        const c = [...g.world.citizens.values()].find((x) => x.at?.action === 'idle' && x.at.idleLeft > 5000 && x.figure.root.visible && !(window.__greeted ?? []).includes(x.id));
        return c?.id ?? null;
      }, null, { timeout: 40_000 }).then((h) => h.jsonValue(), () => null);
      if (!id) return { id: null };
      await send({ type: 'relations', relations: { you: { ...you, [id]: { ...you[id], score, band } }, pairs: [], ties: {} } });
      await page.evaluate((cid) => {
        (window.__greeted ??= []).push(cid);
        const g = window.__polisGame;
        // 别的人先挪远：只有他在你 2.5 米内（打招呼挑最近的那一位）
        const c = g.world.citizens.get(cid);
        const p = c.figure.root.position;
        g.setFollow('player');
        g.placePlayer(p.x + 1.4, p.z, -Math.PI / 2);
      }, id);
      await page.waitForFunction((cid) => window.__polis.greeting === cid, id, { timeout: 4000 }).catch(() => {});
      await page.waitForTimeout(400);
      return page.evaluate((cid) => {
        const g = window.__polisGame;
        const c = g.world.citizens.get(cid);
        const toPlayer = Math.atan2(g.player.x - c.figure.root.position.x, g.player.z - c.figure.root.position.z);
        const facing = Math.abs(((c.yawNow - toPlayer + Math.PI * 3) % (Math.PI * 2)) - Math.PI);
        return { id: cid, greeting: window.__polis.greeting, style: window.__polis.greetingStyle, bubble: (g.used?.citizenBubbles ?? []).find((b) => b.id === cid)?.text ?? null, facingErr: +facing.toFixed(2) };
      }, id);
    };
    const hostile = await greetCheck(-80, 'hostile');
    const close = await greetCheck(75, 'close');
    const checks = {
      relationsArrive: relStatus?.people === 11 && relStatus?.pairs === 2,
      buttonLabelled: buttonLabel === '人际',
      overviewOpens: panel.open && panel.rows.length === 11,
      sortedByScore: panel.rows[0]?.id === 'rel-0' && panel.rows[8]?.id === 'rel-1',
      residentsApart: panel.headers.length === 2 && panel.rows.slice(9).every((r) => r.id.startsWith('resident:')),
      indoorsListed: panel.rows.some((r) => r.id === 'in-1' && r.text.includes('在屋里')),
      detailShowsTies: detail.name === '第0位' && (detail.why ?? '').includes('因为') && detail.links.some((l) => l.startsWith('第2位')) && detail.links.some((l) => l.startsWith('第1位')),
      noWalkingWhileOpen: Math.hypot(after.x - before.x, after.z - before.z) < 0.01,
      escCloses: closed,
      hostileTurnsAway: hostile.id != null && hostile.greeting === hostile.id && hostile.style?.turnAway === true && hostile.bubble == null && hostile.facingErr > 2.0,
      closeWavesWarmly: close.id != null && close.greeting === close.id && close.style?.wave === true && close.style?.line === 'warm' && typeof close.bubble === 'string' && close.bubble.length > 0 && close.facingErr < 0.6,
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, relStatus, panel: { open: panel.open, rows: panel.rows.map((r) => r.id), headers: panel.headers }, detail, hostile, close, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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
 * 你自己说话（第三轮需求 6）：附近的几个人都有反应——按他们怎么看你转身挥手、转过来听、背过身去；最多三位依次开口（chorus，厌恶你的不开口，
 * 喜欢你的先）；第一位头顶马上一串点；C# 推来一位位的话和 chorus-end 之后，没轮到的人的点收起、听的人再留一会儿就散。
 */
async function chorusCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, chorus' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    const inbox = () => page.evaluate(() => window.__hostInbox);
    await send({ type: 'init', tables: {} });
    await send({ type: 'city', key: 'ws-1', city });
    const street = Array.from({ length: 6 }, (_, i) => ({
      id: `ch-${i}`, seed: (Math.imul(i + 31, 2654435761) >>> 0), female: i % 2 === 0, name: `第${i}位`, title: `委托 ${i}`,
      home: `agora:${i}`, age: 'adult', outcome: 'completed', folders: [], turns: 2, trade: 'baker',
    }));
    await send({ type: 'citizens', citizens: { street, indoors: [], elders: [], census: { total: 6, street: 6, indoors: 0, elders: 0 } } });
    await page.waitForFunction(() => {
      const g = window.__polisGame;
      return g.world.citizens.size === 6 && [...g.world.citizens.values()].every((c) => c.figure && c.plan);
    }, null, { timeout: 60_000 });
    // 城里自己的闲聊停掉（像没配模型那样：这座城里不再挑一对）——不然挑中的两位在闲聊，不算听见你说话的人
    await page.evaluate(() => {
      const g = window.__polisGame;
      g.ambientNow('ch-4', 'ch-5');
      g.endAmbient({ a: 'ch-4', b: 'ch-5', status: 'nomodel' });
    });
    // 等三位此刻闲着的人，你站到他们中间（广场一圈的站位彼此在几米内）
    const trio = await page.waitForFunction(() => {
      const g = window.__polisGame;
      const idle = [...g.world.citizens.values()].filter((c) => c.at?.action === 'idle' && c.at.idleLeft > 6000 && c.figure.root.visible);
      return idle.length >= 3 ? idle.slice(0, 3).map((c) => c.id) : null;
    }, null, { timeout: 60_000 }).then((h) => h.jsonValue(), () => null);
    if (!trio) throw new Error('等不到三位闲着的人');
    const [fondId, haterId, plainId] = trio;
    const you = Object.fromEntries(street.map((c) => [c.id, { score: 0, band: 'neutral', patience: 3, opens: true, comesOut: false, why: [], talk: 0.5 }]));
    you[fondId] = { ...you[fondId], score: 75, band: 'close', comesOut: true };
    you[haterId] = { ...you[haterId], score: -80, band: 'hostile', opens: false, talk: 1 };
    await send({ type: 'relations', relations: { you, pairs: [], ties: {} } });
    await page.evaluate((ids) => {
      const g = window.__polisGame;
      const ps = ids.map((id) => g.world.citizens.get(id).figure.root.position);
      const cx = ps.reduce((s, p) => s + p.x, 0) / ps.length, cz = ps.reduce((s, p) => s + p.z, 0) / ps.length;
      g.setFollow('player');
      g.placePlayer(cx, cz + 0.4, 0);
    }, trio);
    await page.waitForTimeout(300);
    const n = (await inbox()).length;
    const first = await page.evaluate(() => window.__polisGame.speak({ kind: 'say', text: '今天风真大' }));
    await page.waitForTimeout(250);
    const sent = (await inbox()).slice(n);
    const chorus = sent.find((m) => m.type === 'chorus');
    const after = await page.evaluate(() => {
      const g = window.__polisGame;
      return { spoke: window.__polis.spoke, listeners: Object.fromEntries([...g.listeners].map(([id, l]) => [id, l.kind])), sayings: Object.fromEntries([...g.sayings].map(([id, s]) => [id, { typing: s.typing, text: s.text }])) };
    });
    // 一位位地说（照 C# 推来的样子），再 chorus-end
    const who = chorus?.who ?? [];
    if (who[0]) await send({ type: 'talk', who: who[0], n: 1, text: '是啊，旗子都飘直了。', done: true, status: 'ok' });
    if (who[1]) {
      await send({ type: 'talk', who: who[1], n: 0, text: '', done: false });
      await page.waitForTimeout(80);
    }
    const secondDots = who[1] ? await page.evaluate((id) => window.__polisGame.sayings.get(id)?.typing ?? null, who[1]) : null;
    if (who[1]) await send({ type: 'talk', who: who[1], n: 2, text: '我也这么觉得。', done: true, status: 'ok' });
    if (who[2]) await send({ type: 'talk', who: who[2], n: 0, text: '', done: false });
    await send({ type: 'chorus-end', who, spoke: who.slice(0, 2), status: 'nomodel' });
    await page.waitForTimeout(150);
    const ended = await page.evaluate(() => {
      const g = window.__polisGame;
      const now = performance.now();
      return { typing: [...g.sayings].filter(([, s]) => s.typing).map(([id]) => id), longest: Math.max(0, ...[...g.listeners.values()].map((l) => l.until - now)), chorus: window.__polis.chorus };
    });
    const checks = {
      chorusSent: !!chorus && chorus.text === '今天风真大' && who.length >= 2 && who.length <= 3,
      noChatSent: !sent.some((m) => m.type === 'chat' || m.type === 'say'),
      haterSilent: !who.includes(haterId),
      fondFirst: who[0] === fondId && first === fondId,
      everyoneReacts: after.listeners[fondId] === 'wave' && after.listeners[haterId] === 'turnAway' && after.listeners[plainId] === 'listen',
      firstThinking: after.sayings[fondId]?.typing === true,
      nextGetsDots: who.length < 2 || secondDots === true,
      endClearsDots: ended.typing.length === 0,
      listenersDisperse: ended.longest <= 5200 && ended.chorus?.status === 'nomodel',
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, trio: { fondId, haterId, plainId }, chorus, after, ended, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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
 * 门与敲门（第三轮需求 2、3）：民居、作坊的门会开；住在里面的人到了时候在屋里（不画）、开门出来（门开着、人看得见）；你走不进门洞。
 * 门阶前按 E 敲门：你抬手、门上"咚、咚"、屋里的人发 knock；C# 回音之后门里应声、面板列出屋里的人（街上满了、在屋里的那位也在）；
 * 挑一位，"隔着门说话"把栏借给他（chat 带 door）、他说到头出门来谈（talk 的 door: out）——屋里那位临时上街、走出来、栏接着借给他；
 * "请他出来"发 call-out，C# 说出来就出来、卡片打开，说不出来门里回绝一句；敲得太勤门里一句"别敲了"；屋里没人"没有人应门"。
 */
async function doorCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, doors and knocking' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    const inbox = () => page.evaluate(() => window.__hostInbox);
    const sentSince = async (n) => (await inbox()).slice(n);
    await send({ type: 'init', tables: {} });
    await send({ type: 'city', key: 'ws-1', city });
    const street = [
      { id: 'house-a', home: 'b:杂物' }, { id: 'shop-a', home: 'b:网站' }, { id: 'agora-a', home: 'agora:1' },
    ].map((c, i) => ({ ...c, seed: (Math.imul(i + 51, 2654435761) >>> 0), female: i % 2 === 0, name: `住户${i}`, title: `委托 ${i}`, age: 'adult', outcome: 'completed', folders: [], turns: 2, trade: 'spinner' }));
    const indoors = [{ id: 'indoor-a', seed: 4242, female: true, name: '屋里的阿伽忒', home: 'b:杂物', age: 'young', trade: 'baker' }];
    await send({ type: 'citizens', citizens: { street, indoors, elders: [], census: { total: 4, street: 3, indoors: 1, elders: 0 } } });
    await page.waitForFunction(() => {
      const g = window.__polisGame;
      return g.world.citizens.size === 3 && [...g.world.citizens.values()].every((c) => c.figure && c.plan);
    }, null, { timeout: 60_000 });
    // 城里自己的闲聊停掉（夹具里没有 C# 回 ambient-end）
    await page.evaluate(() => { const g = window.__polisGame; g.ambientNow('house-a', 'agora-a'); g.endAmbient({ a: 'house-a', b: 'agora-a', status: 'nomodel' }); });
    const doors = await page.evaluate(() => {
      const g = window.__polisGame;
      return { ids: [...g.world.doors.keys()], house: g.world.citizens.get('house-a').plan.door?.id ?? null, shop: g.world.citizens.get('shop-a').plan.door?.id ?? null, agora: g.world.citizens.get('agora-a').plan.door ?? null };
    });

    // —— 门：拨他的日程到"在屋里"、"正在出门"，看人与门 ——
    const setPhase = (id, phase, hold = false) => page.evaluate(({ cid, ph, hd }) => {
      const c = window.__polisGame.world.citizens.get(cid);
      const now = performance.now();
      c.schedule.holdAt = hd ? now : null;
      c.schedule.shift = now - (ph - c.plan.offset);
    }, { cid: id, ph: phase, hd: hold });
    await setPhase('shop-a', 5000);
    await page.waitForTimeout(150);
    const inside = await page.evaluate(() => { const c = window.__polisGame.world.citizens.get('shop-a'); return { at: c.at.at, visible: c.figure.root.visible }; });
    await setPhase('shop-a', 15000 + 350 + 400);
    await page.waitForTimeout(100);
    const exiting = await page.evaluate((door) => { const g = window.__polisGame; const c = g.world.citizens.get('shop-a'); return { at: c.at.at, visible: c.figure.root.visible, open: g.world.doors.get(door).open }; }, doors.shop);
    // 你走不进门洞：站在门阶上朝门按住 W
    const blocked = await page.evaluate((door) => {
      const g = window.__polisGame;
      const p = g.world.doorPlan(door);
      g.setFollow('player');
      g.placePlayer(p.step.x, p.step.z, Math.atan2(p.x - p.step.x, p.z - p.step.z));
      return { door: { x: p.x, z: p.z } };
    }, doors.shop);
    await page.keyboard.down('KeyW');
    await page.waitForTimeout(700);
    await page.keyboard.up('KeyW');
    const afterPush = await page.evaluate(() => window.__polisGame.player);

    // —— 敲门：住户 house-a 此刻在屋里（日程停在屋里），屋里还有街上满了的阿伽忒 ——
    await setPhase('house-a', 4000, true);
    await page.waitForTimeout(120);
    const atDoor = await page.evaluate((door) => {
      const g = window.__polisGame;
      const p = g.world.doorPlan(door);
      g.placePlayer(p.step.x, p.step.z + 0.5, Math.atan2(p.x - p.step.x, p.z - (p.step.z + 0.5)));
      return g.insideOf(door);
    }, doors.house);
    await page.waitForTimeout(250);
    const focus = await page.evaluate(() => window.__polis.focus);
    let n = (await inbox()).length;
    await page.keyboard.press('KeyE');
    await page.waitForTimeout(120);
    const knocked = await page.evaluate(() => ({ said: window.__polis.doorSaid?.text ?? null, action: window.__polis.playerAction, knock: window.__polis.knock }));
    const knockSent = (await sentSince(n)).filter((m) => m.type === 'knock');
    await send({ type: 'knock-reply', who: knockSent[0]?.who ?? [], pestered: [], ignoring: [] });
    await page.waitForTimeout(1500);
    const asked = await page.evaluate(() => ({ panel: window.__polis.panel, view: window.__polisGame.panelView(), regions: (window.__polisGame.regions ?? []).map((r) => r.id), said: window.__polis.doorSaid?.text ?? null }));
    // 挑屋里的阿伽忒，隔着门说话
    await page.evaluate(() => window.__polisGame.onButton('door-pick:indoor-a'));
    await page.waitForTimeout(80);
    const picked = await page.evaluate(() => ({ view: window.__polisGame.panelView(), regions: (window.__polisGame.regions ?? []).map((r) => r.id) }));
    await page.evaluate(() => window.__polisGame.onButton('door-talk'));
    await page.waitForTimeout(150);
    const lent = await page.evaluate(() => ({ lent: window.__polisTalk.lentTo, status: document.querySelector('#talk .talk-status').textContent }));
    n = (await inbox()).length;
    await page.keyboard.type('你在家呀？');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(120);
    const doorChat = (await sentSince(n)).find((m) => m.type === 'chat');
    await send({ type: 'talk', who: 'indoor-a', n: 1, text: '在呢，你等等，我出来跟你说。', done: true, status: 'ok', door: 'out' });
    await page.waitForFunction(() => window.__polisGame.world.citizens.get('indoor-a')?.at?.at === 'visit', null, { timeout: 15_000 }).catch(() => {});
    const cameOut = await page.evaluate(() => {
      const g = window.__polisGame;
      const c = g.world.citizens.get('indoor-a');
      return { guest: !!c, at: c?.at?.at ?? null, visible: c?.figure?.root.visible ?? false, lent: window.__polisTalk.lentTo, mode: window.__polisTalk.mode, visits: [...g.visits.keys()] };
    });
    await page.keyboard.press('Escape');
    await page.waitForTimeout(100);

    // —— 请他出来：住户 house-a；C# 说出来——门开、他走到门阶上，卡片打开 ——
    await page.evaluate(() => window.__polisGame.knock(window.__polisGame.world.citizens.get('house-a').plan.door.id));
    await send({ type: 'knock-reply', who: ['house-a'], pestered: [], ignoring: [] });
    await page.waitForTimeout(1500);
    await page.evaluate(() => window.__polisGame.onButton('door-pick:house-a'));
    n = (await inbox()).length;
    await page.evaluate(() => window.__polisGame.onButton('door-call'));
    await page.waitForTimeout(80);
    const callSent = (await sentSince(n)).find((m) => m.type === 'call-out');
    await send({ type: 'call-out-reply', who: 'house-a', out: true });
    await page.waitForFunction(() => window.__polisGame.panelView()?.type === 'citizen', null, { timeout: 15_000 }).catch(() => {});
    const calledOut = await page.evaluate(() => {
      const g = window.__polisGame;
      const c = g.world.citizens.get('house-a');
      return { at: c.at.at, visible: c.figure.root.visible, panel: g.panelView()?.type ?? null, opened: window.__polis.citizenOpened };
    });
    await page.evaluate(() => window.__polisGame.onButton('panel-close'));

    // —— 敲得太勤：C# 回音里有他——门里一句"别敲了" ——
    await setPhase('shop-a', 4000, true);
    await page.waitForTimeout(100);
    await page.evaluate(() => window.__polisGame.knock(window.__polisGame.world.citizens.get('shop-a').plan.door.id));
    await send({ type: 'knock-reply', who: ['shop-a'], pestered: ['shop-a'], ignoring: [] });
    await page.waitForTimeout(1500);
    const pestered = await page.evaluate(() => ({ said: window.__polis.doorSaid?.text ?? null, state: window.__polis.knock?.state, panel: window.__polisGame.panelView()?.type ?? null }));
    // —— 屋里没人：作坊的住户出门了 ——
    await setPhase('shop-a', 60_000, true);
    await page.waitForTimeout(100);
    await page.evaluate(() => window.__polisGame.knock(window.__polisGame.world.citizens.get('shop-a').plan.door.id));
    await page.waitForTimeout(1500);
    const nobody = await page.evaluate(() => ({ said: window.__polis.doorSaid?.text ?? null, state: window.__polis.knock?.state }));
    const checks = {
      doorsBuilt: doors.ids.length >= 2 && !!doors.house && !!doors.shop && doors.agora == null,
      insideIsHidden: inside.at === 'inside' && !inside.visible,
      exitingOpensTheDoor: exiting.at === 'door' && exiting.visible && exiting.open > 0.9,
      youCannotEnter: Math.hypot(afterPush.x - blocked.door.x, afterPush.z - blocked.door.z) > 0.3 && afterPush.z > blocked.door.z,
      focusOnDoor: focus?.type === 'door' && focus.id === doors.house,
      knockAnimatesAndSends: knocked.said === '咚、咚' && knocked.action === 'work' && knockSent.length === 1
        && knockSent[0].who.includes('house-a') && knockSent[0].who.includes('indoor-a') && atDoor.length === 2,
      panelListsWhoIsIn: asked.panel === 'door' && asked.regions.includes('door-pick:house-a') && asked.regions.includes('door-pick:indoor-a') && /谁|来了/.test(asked.said ?? ''),
      pickedOffersChoices: picked.regions.includes('door-call') && picked.regions.includes('door-talk') && picked.view?.title === '屋里的阿伽忒',
      talkThroughDoor: lent.lent?.kind === 'chat' && lent.lent.id === 'indoor-a' && /隔着门/.test(lent.status) && doorChat?.door === true && doorChat.who === 'indoor-a',
      comesOutToTalk: cameOut.guest && cameOut.at === 'visit' && cameOut.visible && cameOut.lent?.id === 'indoor-a',
      callOutSent: callSent?.who === 'house-a',
      calledOutComes: calledOut.at === 'visit' && calledOut.visible && calledOut.panel === 'citizen' && calledOut.opened === 'house-a',
      pesteredSaysStop: pestered.state === 'pestered' && /别敲|又敲/.test(pestered.said ?? '') && pestered.panel !== 'door',
      emptyHouseIsSilent: nobody.state === 'nobody' && nobody.said === '（没有人应门）',
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, doors, inside, exiting, afterPush, focus, knocked, knockSent, asked: { panel: asked.panel, said: asked.said }, picked: picked.view?.title, lent, doorChat, cameOut, calledOut, pestered, nobody, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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
 * 手艺（第三轮需求 4）：家有门的居民在家门前的那几段时间干他那门活，要工位的工位摆在他面前（不挡路、按他的身高缩放），
 * 住在公共地方的织工在那儿纺线（卷线杆、纺锤拿在手里）；你走近，干活的人停下来跟你打招呼，说完接着干；人走了，工位收掉。
 */
async function craftCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, crafts' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    await send({ type: 'init', tables: {} });
    await send({ type: 'city', key: 'ws-1', city });
    const people = [
      { id: 'baker-a', home: 'b:杂物', trade: 'baker', seed: 15 },
      { id: 'weaver-a', home: 'agora:1', trade: 'spinner', seed: 4243 },
    ];
    const street = people.map((c, i) => ({ ...c, female: (c.seed & 1) === 1, name: `匠人${i}`, title: `委托 ${i}`, age: 'adult', outcome: 'completed', folders: [], turns: 2 }));
    const census = (n) => ({ total: n, street: n, indoors: 0, elders: 0 });
    await send({ type: 'citizens', citizens: { street, indoors: [], elders: [], census: census(street.length) } });
    await page.waitForFunction(() => {
      const g = window.__polisGame;
      return g.world.citizens.size === 2 && [...g.world.citizens.values()].every((c) => c.figure && c.plan);
    }, null, { timeout: 60_000 });
    // 夹具里没有 C# 回 ambient-end：城里自己的闲聊停掉
    await page.evaluate(() => { const g = window.__polisGame; g.ambientNow('baker-a', 'weaver-a'); g.endAmbient({ a: 'baker-a', b: 'weaver-a', status: 'nomodel' }); });
    // 两位都拨到周期第 20 秒：出门之后、在家门前（离进屋还早）
    const setPhase = (id, phase) => page.evaluate(({ cid, ph }) => {
      const c = window.__polisGame.world.citizens.get(cid);
      c.schedule.holdAt = null;
      c.schedule.shift = performance.now() - (ph - c.plan.offset);
    }, { cid: id, ph: phase });
    await setPhase('baker-a', 20_000);
    await setPhase('weaver-a', 20_000);
    // 你站在面包师前面 4 米（看得见他、又不近到让他跟你打招呼：画面外的人不摆姿势），再站到广场上看织工
    await page.evaluate(() => {
      const g = window.__polisGame;
      const b = g.world.citizens.get('baker-a');
      const p = b.plan.home.point, yaw = b.plan.craft.homeYaw;
      g.setFollow('player');
      g.placePlayer(p.x + Math.sin(yaw) * 4, p.z + Math.cos(yaw) * 4, yaw + Math.PI);
    });
    await page.waitForTimeout(900);
    const baker = await page.evaluate(() => {
      const g = window.__polisGame;
      const b = g.world.citizens.get('baker-a');
      const yawDiff = Math.abs(Math.atan2(Math.sin(b.figure.root.rotation.y - b.plan.craft.homeYaw), Math.cos(b.figure.root.rotation.y - b.plan.craft.homeYaw)));
      return { action: b.at.action, work: b.at.work, key: b.figure.state.key, yawDiff, door: !!b.plan.door, visible: b.figure.root.visible, worked: window.__polis.crafts?.working };
    });
    await page.evaluate(() => window.__polisGame.placePlayer(0, -6, 0));
    await page.waitForTimeout(700);
    const working = await page.evaluate(() => {
      const g = window.__polisGame;
      const b = g.world.citizens.get('baker-a'), w = g.world.citizens.get('weaver-a');
      const st = g.world.stations.get('baker-a');
      let stationObjects = 0;
      g.R.scene.traverse((o) => { if (/^station:/.test(o.name)) stationObjects++; });
      return {
        station: st ? { kind: st.kind, gap: Math.hypot(st.x - b.plan.home.point.x, st.z - b.plan.home.point.z), scale: st.object.scale.x, height: b.figure.root.userData.height, visible: st.object.visible, blocks: g.world.occupancy.blocked(st.x, st.z) } : null,
        weaver: { action: w.at.action, work: w.at.work, key: w.figure.state.key, distaff: w.figure.props.get('distaff')?.visible ?? null, spindle: w.figure.props.get('spindle')?.visible ?? null },
        stationObjects, crafts: window.__polis.crafts,
      };
    });
    // 你走到织工跟前（1.6 米）：他停下手里的活跟你打招呼，说完接着纺
    await page.evaluate(() => {
      const g = window.__polisGame;
      const w = g.world.citizens.get('weaver-a');
      const p = w.figure.root.position;
      g.placePlayer(p.x + 1.6, p.z, -Math.PI / 2);
    });
    await page.waitForFunction(() => window.__polis.greeting === 'weaver-a', null, { timeout: 5000 }).catch(() => {});
    await page.waitForTimeout(500);
    const greeting = await page.evaluate(() => ({ greeting: window.__polis.greeting, key: window.__polisGame.world.citizens.get('weaver-a').figure.state.key }));
    await page.evaluate(() => window.__polisGame.placePlayer(0, -6, 0));
    await page.waitForFunction(() => window.__polis.greeting == null, null, { timeout: 6000 }).catch(() => {});
    await page.waitForTimeout(500);
    const after = await page.evaluate(() => window.__polisGame.world.citizens.get('weaver-a').figure.state.key);
    // 他家的房子换了样子（城的这一版里那座建筑的体量变了）：他按新的墙、新的门重排日程，工位跟着重摆
    const replanned = await page.evaluate(async (doc) => {
      const g = window.__polisGame;
      const b = g.world.citizens.get('baker-a');
      b.plan.stale = true;
      const next = { ...doc, buildings: doc.buildings.map((x) => (x.key === '杂物' ? { ...x, sizeClass: x.sizeClass === 3 ? 2 : 3 } : x)) };
      window.polis.receive(JSON.stringify({ type: 'city', key: 'ws-1', city: next }));
      await new Promise((r) => setTimeout(r, 600));
      const c = g.world.citizens.get('baker-a');
      return { update: window.__polis.lastUpdate, replanned: !!c.plan && !c.plan.stale, station: g.world.stations.has('baker-a') ? g.world.stations.get('baker-a').object.parent != null : null };
    }, city);
    // 面包师走了（这场会话删了）：工位跟着收掉
    await send({ type: 'citizens', citizens: { street: street.filter((c) => c.id !== 'baker-a'), indoors: [], elders: [], census: census(1) } });
    await page.waitForTimeout(400);
    const gone = await page.evaluate(() => {
      const g = window.__polisGame;
      let stationObjects = 0;
      g.R.scene.traverse((o) => { if (/^station:/.test(o.name)) stationObjects++; });
      return { stations: g.world.stations.size, stationObjects };
    });
    const checks = {
      bakerKneadsAtHome: baker.action === 'work' && baker.work === 'knead' && baker.door && baker.visible && /^work:knead/.test(baker.key ?? '') && baker.worked >= 1,
      facesHisWork: baker.yawDiff < 0.05,
      troughInFrontOfHim: working.station?.kind === 'trough' && working.station.visible && Math.abs(working.station.gap - 0.42 * working.station.height / 1.75) < 1e-6
        && Math.abs(working.station.scale - working.station.height / 1.75) < 1e-9,
      stationNeverBlocks: working.station?.blocks === false,
      oneStationDrawn: working.stationObjects === 1 && working.crafts?.stations === 1,
      spinnerSpinsOnTheAgora: working.weaver.action === 'work' && working.weaver.work === 'spin' && working.weaver.distaff === true && working.weaver.spindle === true,
      stopsToGreetYou: greeting.greeting === 'weaver-a' && /^(talk|wave)/.test(greeting.key ?? ''),
      backToWorkAfter: /^work:spin/.test(after ?? ''),
      replannedWhenHisHouseChanges: replanned.update === 'patched' && replanned.replanned,
      stationGoesWithHim: gone.stations === 0 && gone.stationObjects === 0,
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, baker, working, greeting, after, replanned, gone, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
    await page.close();
    console.log(JSON.stringify(result, null, 2));
    return Object.values(checks).every(Boolean);
  } catch (e) {
    result.error = e.message;
    console.log(JSON.stringify(result, null, 2));
    return false;
  }
}

async function citizensCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, citizens' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    const inbox = () => page.evaluate(() => window.__hostInbox);
    const sentSince = async (n) => (await inbox()).slice(n);
    await send({ type: 'init', tables: {} });
    await send({ type: 'city', key: 'ws-1', city });
    // 第四轮：城里的人从建筑来——每人住在一座楼里，带职业、脾气、住处；每八位里一位夜里也出门
    const homes = city.buildings.slice(0, 8).map((b) => b.key);
    const street = Array.from({ length: 40 }, (_, i) => ({
      id: `n:test0000:${String(i).padStart(12, '0')}`, seed: (Math.imul(i + 7, 2654435761) >>> 0), female: i % 2 === 1, name: `第${i}位`,
      home: `b:${homes[i % homes.length]}`, homeName: homes[i % homes.length], age: ['young', 'adult', 'old'][i % 3],
      trade: ['potter', 'baker', 'idle', 'merchant'][i % 4], tradeName: ['陶工', '面包师', '无业的人', '商贩'][i % 4], temper: '爽朗', stall: false, cottage: false, nightOwl: i % 8 === 0,
    }));
    const indoors = [{ id: 'n:test0000:indoor000001', seed: 4242, female: true, name: '屋里的阿伽忒', home: 'b:杂物', homeName: '杂物', age: 'young', trade: 'baker', tradeName: '面包师', temper: '腼腆' }];
    await send({ type: 'citizens', citizens: { street, indoors, census: { total: 41, street: 32, indoors: 9 } } });
    await page.waitForFunction(() => {
      const g = window.__polisGame;
      return g.world.citizens.size > 0 && [...g.world.citizens.values()].every((c) => c.figure && c.plan);
    }, null, { timeout: 60_000 });
    await page.waitForTimeout(400);
    const census = await page.evaluate(() => {
      const g = window.__polisGame;
      let figures = 0;
      g.R.scene.traverse((o) => { if (/^figure:citizen:/.test(o.name)) figures++; });
      return { figures, size: g.world.citizens.size, tiers: window.__polis.citizenTiers, status: window.__polis.citizens };
    });

    // 走到一位此刻闲着、还要待一阵的居民跟前（1 米），面朝他，按 E：卡片上是他的职业、年纪、性子、住处
    const target = await page.evaluate(() => {
      const g = window.__polisGame;
      const c = [...g.world.citizens.values()].find((x) => x.at?.action === 'idle' && x.at.idleLeft > 8000 && x.figure.root.visible);
      if (!c) return null;
      const p = c.figure.root.position;
      g.setFollow('player');
      g.placePlayer(p.x + 1.0, p.z, -Math.PI / 2);
      return c.id;
    });
    await page.waitForTimeout(250);
    const focus = await page.evaluate(() => window.__polis.focus);
    await page.keyboard.press('KeyE');
    await page.waitForTimeout(200);
    const card = await page.evaluate(() => ({ opened: window.__polis.citizenOpened, panel: window.__polis.panel, view: window.__polisGame.panelView?.() ?? null }));
    const cardButtons = await page.evaluate(() => (window.__polisGame.regions ?? []).map((r) => r.id));
    const targetData = street.find((c) => c.id === target);
    await page.evaluate(() => window.__polisGame.onButton('panel-close'));
    await page.evaluate(() => window.__polisGame.placePlayer(0, -6, 0));

    // —— 夜里（第四轮需求 4）：打开页面时就是夜里——不是夜游的人一开始就在屋里；天亮（直接到位）照常 ——
    const hidden = () => page.evaluate(() => {
      const g = window.__polisGame;
      const all = [...g.world.citizens.values()];
      return {
        // 没门、又哪儿都去不了的人整夜站在家门前（citizens 规则 8），不算在"回了屋"的人里
        hidden: all.filter((c) => c.at?.hidden).map((c) => c.id), nonOwls: all.filter((c) => !c.data.nightOwl && (c.plan?.door || c.plan?.dest)).length,
        nonOwlsHidden: all.filter((c) => !c.data.nightOwl && (c.plan?.door || c.plan?.dest) && c.at?.hidden).length,
        owlsHidden: all.filter((c) => c.data.nightOwl && c.at?.hidden).map((c) => c.id), total: all.length,
      };
    });
    const dayHidden = await hidden();
    await send({ type: 'theme', night: true, instant: true });
    await page.waitForTimeout(300);
    const nightLoad = await hidden();
    await send({ type: 'theme', night: false, instant: true });
    await page.waitForTimeout(300);
    const dayAgain = await hidden();
    // 入夜（过渡）：那一刻谁也不消失；各自走完手上这一圈再回屋（把每人的日程往后拨 310 秒——没门的家最多要两个周期）
    await send({ type: 'theme', night: true });
    await page.waitForTimeout(300);
    const dusk = await hidden();
    await page.evaluate(() => { for (const c of window.__polisGame.world.citizens.values()) { c.schedule.holdAt = null; c.schedule.shift = -310_000; } });
    await page.waitForTimeout(300);
    const lateNight = await hidden();
    await page.evaluate(() => { for (const c of window.__polisGame.world.citizens.values()) c.schedule.shift = 0; });
    await send({ type: 'theme', night: false, instant: true });
    await page.waitForTimeout(300);

    // —— 客人顶一位（第四轮需求 2）：街上满了（32 位），敲门请屋里的阿伽忒出来——画面外的一位回屋，街上不超过 32 ——
    const doorId = await page.evaluate(() => window.__polisGame.world.doorFor('b:杂物', 4242)?.id ?? null);
    await page.evaluate((id) => {
      const g = window.__polisGame;
      const p = g.world.doorPlan(id);
      g.placePlayer(p.step.x, p.step.z + 0.5, Math.atan2(p.x - p.step.x, p.z - (p.step.z + 0.5)));
      g.knock(id);
    }, doorId);
    await send({ type: 'knock-reply', who: ['n:test0000:indoor000001'], pestered: [], ignoring: [] });
    await page.waitForTimeout(1500);
    await page.evaluate(() => window.__polisGame.onButton('door-pick:n:test0000:indoor000001'));
    let n = (await inbox()).length;
    await page.evaluate(() => window.__polisGame.onButton('door-call'));
    await page.waitForTimeout(80);
    const callSent = (await sentSince(n)).find((m) => m.type === 'call-out');
    await send({ type: 'call-out-reply', who: 'n:test0000:indoor000001', out: true });
    await page.waitForFunction(() => window.__polisGame.visits.get('n:test0000:indoor000001')?.phase === 'out', null, { timeout: 15_000 }).catch(() => {});
    await page.waitForTimeout(200);
    const bench = await page.evaluate(() => {
      const g = window.__polisGame;
      const benched = window.__polis.benched ?? {};
      const sent = benched['n:test0000:indoor000001'];
      const c = sent ? g.world.citizens.get(sent) : null;
      const onStreet = [...g.world.citizens.values()].filter((x) => !x.at?.hidden || x.id === 'n:test0000:indoor000001').length;
      return { sent, hidden: !!c?.at?.hidden, size: g.world.citizens.size, onStreetMax: [...g.world.citizens.values()].filter((x) => x.id !== sent).length };
    });
    await page.evaluate(() => window.__polisGame.onButton('panel-close'));
    // 你走远了：客人回屋，被顶回去的那位回到日程上
    await page.evaluate(() => window.__polisGame.placePlayer(0, 30, 0));
    await page.waitForFunction(() => !window.__polisGame.world.citizens.has('n:test0000:indoor000001'), null, { timeout: 25_000 }).catch(() => {});
    const benchAfter = await page.evaluate(() => ({ benched: Object.keys(window.__polis.benched ?? {}).length, size: window.__polisGame.world.citizens.size,
      visit: [...(window.__polisGame.visits ?? new Map())].map(([id, v]) => `${id}:${v.phase}`), player: window.__polisGame.player, panel: window.__polis.panel }));

    // —— 雅典娜和城里的人说话（第四轮需求 8）：ambient-chat 带着 athena；她的话在她头顶，她转向对方、说话时打手势 ——
    n = (await inbox()).length;
    const partner = await page.evaluate(() => {
      const g = window.__polisGame;
      const a = g.world.athena.position;
      const c = [...g.world.citizens.values()].filter((x) => !x.at?.hidden && x.figure.root.visible)
        .sort((x, y) => Math.hypot(x.figure.root.position.x - a.x, x.figure.root.position.z - a.z) - Math.hypot(y.figure.root.position.x - a.x, y.figure.root.position.z - a.z))[0];
      g.placePlayer(a.x + 6, a.z + 6, 0);
      return g.ambientNow('athena', c.id) ? c.id : null;
    });
    const athenaSent = (await sentSince(n)).find((m) => m.type === 'ambient-chat');
    await send({ type: 'talk', who: 'athena', n: 1, text: '今天集市上热闹吗？', done: false });
    // 她正走着的那一段照样走完（每次移动都要完成），停下来就转向他说话
    await page.waitForFunction(() => window.__polis.athenaPose === 'talk', null, { timeout: 15_000 }).catch(() => {});
    const athenaTalking = await page.evaluate(() => {
      const g = window.__polisGame;
      return { pose: window.__polis.athenaPose, bubble: (g.used?.citizenBubbles ?? []).find((b) => b.id === 'athena')?.text ?? null, chat: window.__polis.chat };
    });
    await send({ type: 'talk', who: 'athena', n: 1, text: '今天集市上热闹吗？', done: true, status: 'ok' });
    await send({ type: 'ambient-end', a: 'athena', b: partner, status: 'ok' });
    await page.waitForTimeout(150);
    const athenaDone = await page.evaluate(() => window.__polis.chat);

    // 名单变了：拆掉不在名单里的，人数跟着变
    await send({ type: 'citizens', citizens: { street: street.slice(0, 5), indoors: [], census: { total: 5, street: 5, indoors: 0 } } });
    await page.waitForTimeout(300);
    const shrunk = await page.evaluate(() => {
      const g = window.__polisGame;
      let figures = 0;
      g.R.scene.traverse((o) => { if (/^figure:citizen:/.test(o.name)) figures++; });
      return { size: g.world.citizens.size, figures };
    });
    const owlsOnStreet = street.slice(0, 32).filter((c) => c.nightOwl).length;
    const checks = {
      streetCapped: census.size === 32 && census.figures === 32,
      fewFullyPosed: census.tiers != null && census.tiers.A <= 4,
      focusOnCitizen: target != null && focus?.type === 'citizen' && focus.id === target,
      cardOpens: card.opened === target && card.panel === 'citizen',
      cardSaysWhoHeIs: (card.view?.lines ?? []).some((l) => l.includes(targetData?.tradeName ?? '?') && l.includes('性子')) && (card.view?.lines ?? []).some((l) => l.includes(`「${targetData?.homeName}」`)),
      cardShowsCensus: (card.view?.lines ?? []).some((l) => /41/.test(l)),
      cardHasNoConversationButtons: cardButtons.includes('citizen-chat') && !cardButtons.includes('citizen-transcript') && !cardButtons.includes('citizen-open'),
      // 夜游的人照着白天的日程（也许正好在屋里那一段）；其余的人都在屋里
      nightOnLoadOnlyOwlsOut: nightLoad.nonOwlsHidden === nightLoad.nonOwls && nightLoad.owlsHidden.every((id) => dayHidden.hidden.includes(id)) && owlsOnStreet === 4,
      dayComesBack: dayAgain.hidden.length === dayHidden.hidden.length,
      duskIsNoVanishing: dusk.hidden.length <= dayHidden.hidden.length + 3,
      afterAPeriodOnlyOwlsOut: lateNight.nonOwlsHidden === lateNight.nonOwls && lateNight.owlsHidden.length < owlsOnStreet,
      guestSendsSomeoneIn: callSent?.who === 'n:test0000:indoor000001' && !!bench.sent && bench.hidden && bench.size === 33 && bench.onStreetMax === 32,
      benchReleasedWhenGuestLeaves: benchAfter.benched === 0 && benchAfter.size === 32,
      listShrinks: shrunk.size === 5 && shrunk.figures === 5,
      athenaChatsWithTownsfolk: partner != null && athenaSent?.a === 'athena' && athenaSent.b === partner && athenaSent.states?.[partner] != null
        && athenaTalking.pose === 'talk' && athenaTalking.bubble === '今天集市上热闹吗？' && athenaTalking.chat?.a === 'athena' && athenaDone == null,
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, census, target, focus, card, cardButtons, athena: { partner, sent: athenaSent, talking: athenaTalking }, nights: { dayHidden: dayHidden.hidden.length, nightLoad: nightLoad.hidden.length, dayAgain: dayAgain.hidden.length, dusk: dusk.hidden.length, lateNight: lateNight.hidden.length, owls: owlsOnStreet }, bench, benchAfter, shrunk, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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
 * 和一位居民聊天（2026-10-13，原来 M-C 的问话）：卡片上的"聊几句"把对话栏借给他——你写给雅典娜的半句话收着；回车只发 chat（不是 say、
 * 也不同步草稿），你的话在你头顶，他头顶一串循环的点；他站定、镜头留在你们两个身上、雅典娜不被叫过来。C# 流式推他的话：半句话
 * 边到边换，说完的定下来（晚到的半句不覆盖），栏回到"你在写"、接着说第二句；Esc 告辞，栏还给雅典娜、半句话还在；页面从不自己开口。
 */
async function chatCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, chatting with a citizen' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    const inbox = () => page.evaluate(() => window.__hostInbox);
    await send({ type: 'init', tables: {} });
    await send({ type: 'city', key: 'ws-1', city });
    const street = Array.from({ length: 6 }, (_, i) => ({
      id: `conv-${i}`, seed: (Math.imul(i + 11, 2654435761) >>> 0), female: i % 2 === 1, name: `整理合同的第${i}位`, title: `委托 ${i}`,
      home: ['agora:0', 'agora:2', 'agora:4', 'market', 'harbor', 'library'][i], age: 'adult', outcome: 'completed', folders: [], turns: 2,
    }));
    await send({ type: 'citizens', citizens: { street, elders: [], census: { total: 6, street: 6, indoors: 0, elders: 0 } } });
    await send({ type: 'composer', conversationId: 'c-1', busy: false, queued: false, waiting: false, compressing: false, canSend: true, attachments: [], draft: '写给雅典娜的半句话' });
    await page.waitForFunction(() => { const g = window.__polisGame; return g.world.citizens.size === 6 && [...g.world.citizens.values()].every((c) => c.figure && c.plan); }, null, { timeout: 60_000 });
    await page.waitForTimeout(300);
    const summonedBefore = await page.evaluate(() => window.__polis.summoned ?? 0);

    // 走到一位闲着、还要待一阵的居民跟前按 E，卡片上有"聊几句"
    const target = await page.evaluate(() => {
      const g = window.__polisGame;
      const c = [...g.world.citizens.values()].find((x) => x.at?.action === 'idle' && x.at.idleLeft > 15000 && x.figure.root.visible);
      if (!c) return null;
      const p = c.figure.root.position;
      g.setFollow('player');
      g.placePlayer(p.x + 1.0, p.z, -Math.PI / 2);
      return { id: c.id, name: c.data.name };
    });
    await page.waitForTimeout(250);
    await page.keyboard.press('KeyE');
    await page.waitForTimeout(150);
    const card = await page.evaluate(() => ({ view: window.__polisGame.panelView(), regions: window.__polisGame.regions.map((r) => r.id) }));
    await page.evaluate(() => window.__polisGame.onButton('citizen-chat'));
    await page.waitForTimeout(200);
    const lent = await page.evaluate(() => ({
      talk: window.__polisTalk.state, value: window.__polisTalk.value, chatting: window.__polisGame.chatting,
      focused: document.activeElement?.classList.contains('talk-input'), name: document.querySelector('#talk .talk-name').textContent,
      placeholder: document.querySelector('#talk .talk-input').placeholder, closeUp: window.__polis.closeUp,
    }));
    const n = (await inbox()).length;
    await page.keyboard.type('今天风大吗？');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(250);
    const sentNow = (await inbox()).slice(n);
    const bubblesNow = () => page.evaluate(() => (window.__polisGame.used?.citizenBubbles ?? []).map((b) => ({ id: b.id, text: b.text, typing: !!b.typing, alpha: b.alpha })));
    // 头顶的话的状态（不管这一帧画没画：闲聊挑的是画面里的人，测试里随手指定的两位可能在画面外，不摆也不画）
    const sayingsNow = () => page.evaluate(() => [...window.__polisGame.sayings].map(([id, v]) => ({ id, text: v.text, typing: !!v.typing, done: v.doneAt != null })));
    const mineNow = () => page.evaluate(() => window.__polisGame.mine?.text ?? null);
    const waiting = await page.evaluate(() => ({ talk: window.__polisTalk.state, chatting: window.__polisGame.chatting, disabled: document.querySelector('#talk .talk-input').disabled, closeUp: window.__polis.closeUp, summoned: window.__polis.summoned ?? 0 }));
    const thinkingBubbles = await bubblesNow();
    const mineAfterSend = await mineNow();

    // C# 流式推他的话：半句话边到边换，说完的那一版定下来，栏回到"你在写"（可以接着说）；说完之后才到的半句话不覆盖它
    await send({ type: 'talk', who: target?.id, text: '风是不小，', done: false });
    await page.waitForTimeout(150);
    const partial = await bubblesNow();
    const finalLine = '风是不小，船都早早靠了岸。你也出来走走？';
    await send({ type: 'talk', who: target?.id, text: finalLine, done: true, status: 'ok' });
    await send({ type: 'talk', who: target?.id, text: '风是', done: false });
    await page.waitForTimeout(250);
    const replied = await page.evaluate(() => ({
      talk: window.__polisTalk.state, chatting: window.__polisGame.chatting, focused: document.activeElement?.classList.contains('talk-input'),
      disabled: document.querySelector('#talk .talk-input').disabled, panel: window.__polisGame.panelView(), closeUp: window.__polis.closeUp,
    }));
    const repliedBubbles = await bubblesNow();
    // 接着说第二句
    const n1 = (await inbox()).length;
    await page.keyboard.type('那我去港口看看');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(200);
    const second = (await inbox()).slice(n1);

    // Esc 告辞：什么也不发，对话栏还回来（写给雅典娜的半句话还在）
    await send({ type: 'talk', who: target?.id, text: '去吧。', done: true, status: 'ok' });
    await page.waitForTimeout(200);
    const n2 = (await inbox()).length;
    await page.keyboard.type('算了');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    const cancelled = await page.evaluate(() => ({ talk: window.__polisTalk.state, value: window.__polisTalk.value, athenaDraft: window.__polisTalk.athenaDraft, chatting: window.__polisGame.chatting }));
    const afterCancel = (await inbox()).slice(n2);
    // 两位居民街头闲聊（只用模型）：发 ambient-chat；先开口的那位头顶一串点，C# 流式推他的话，说完另一位头顶一串点再接一句；
    // ambient-end 散场。没配模型（nomodel）：头顶的点收起，这座城里不再挑
    const pairIds = street.map((c) => c.id).filter((id) => id !== target?.id).slice(0, 2);
    const nAmb = (await inbox()).length;
    const startedAmbient = await page.evaluate(([a, b]) => window.__polisGame.ambientNow(a, b), pairIds);
    await page.waitForTimeout(150);
    const ambientSent = (await inbox()).slice(nAmb);
    const ambientDots = await sayingsNow();
    await send({ type: 'talk', who: pairIds[0], n: 7, text: '今天港口', done: false });
    await page.waitForTimeout(120);
    const ambientPartial = await sayingsNow();
    await send({ type: 'talk', who: pairIds[0], n: 7, text: '今天港口的风真大。', done: true, status: 'ok' });
    await send({ type: 'talk', who: pairIds[1], n: 0, text: '', done: false });
    await page.waitForTimeout(150);
    const ambientSecond = await sayingsNow();
    await send({ type: 'talk', who: pairIds[1], n: 8, text: '船都早早靠岸了。', done: true, status: 'ok' });
    await send({ type: 'ambient-end', a: pairIds[0], b: pairIds[1], status: 'ok' });
    await page.waitForTimeout(150);
    const ambientDone = await page.evaluate(() => ({ chat: window.__polis.chat, ambient: window.__polis.ambient, next: window.__polisGame.nextChatPickAt, now: performance.now() }));
    const ambientDoneBubbles = await sayingsNow();
    // 没配模型：先开口的那位还在想就散了，点收起；不再挑
    await page.evaluate(([a, b]) => window.__polisGame.ambientNow(b, a), pairIds);
    await page.waitForTimeout(100);
    await send({ type: 'ambient-end', a: pairIds[1], b: pairIds[0], status: 'nomodel' });
    await page.waitForTimeout(150);
    const noModel = await page.evaluate(() => ({ chat: window.__polis.chat, never: window.__polisGame.nextChatPickAt === Infinity }));
    const noModelBubbles = await sayingsNow();

    const all = await inbox();
    const chats = all.filter((m) => m.type === 'chat');
    const checks = {
      cardOffersChat: card.view?.type === 'citizen' && card.view.askable === true && card.regions.includes('citizen-chat') && !card.regions.includes('citizen-transcript'),
      barLentToCitizen: lent.talk.mode === 'chat' && lent.focused && lent.name === target?.name && lent.placeholder.includes(target?.name ?? '?') && lent.value === '',
      citizenHeldInCloseUp: lent.chatting?.id === target?.id && lent.closeUp === 'citizen',
      onlyChatSent: sentNow.length === 1 && sentNow[0].type === 'chat' && sentNow[0].who === target?.id && sentNow[0].text === '今天风大吗？',
      waitsForHim: waiting.talk.mode === 'chatting' && waiting.disabled && waiting.chatting?.status === 'waiting' && waiting.closeUp === 'citizen',
      thinkingDotsOverHisHead: thinkingBubbles.some((b) => b.id === target?.id && b.typing && /^·+$/.test(b.text)),
      yourLineOverYourHead: mineAfterSend === '今天风大吗？',
      streamsIntoHisBubble: partial.some((b) => b.id === target?.id && !b.typing && b.text === '风是不小，'),
      finalLineStays: repliedBubbles.some((b) => b.id === target?.id && b.text === finalLine && b.alpha === 1),
      barResumesForTheNextLine: replied.talk.mode === 'chat' && replied.focused && !replied.disabled && replied.chatting?.status === 'typing' && replied.panel == null && replied.closeUp === 'citizen',
      keepsChatting: second.length === 1 && second[0].type === 'chat' && second[0].text === '那我去港口看看',
      athenaNotSummoned: waiting.summoned === summonedBefore,
      // 告辞：栏回到你自己说话（2026-10-13），写给雅典娜的半句话照样收在她那边
      escTakesLeave: cancelled.talk.mode === 'speak' && cancelled.chatting === null && cancelled.athenaDraft === '写给雅典娜的半句话' && afterCancel.length === 0,
      neverSpeaksByItself: chats.length === 2 && !all.some((m) => m.type === 'say' || m.type === 'ask-citizen' || (m.type === 'draft' && /风|港口|算了/.test(m.text ?? ''))),
      ambientAsksTheModel: startedAmbient === true && ambientSent.length === 1 && ambientSent[0].type === 'ambient-chat' && ambientSent[0].a === pairIds[0] && ambientSent[0].b === pairIds[1],
      ambientDotsThenStream: ambientDots.some((b) => b.id === pairIds[0] && b.typing) && ambientPartial.some((b) => b.id === pairIds[0] && b.text === '今天港口'),
      ambientSecondThinks: ambientSecond.some((b) => b.id === pairIds[0] && b.text === '今天港口的风真大。') && ambientSecond.some((b) => b.id === pairIds[1] && b.typing),
      ambientEnds: ambientDone.chat === null && ambientDone.ambient?.status === 'ok' && ambientDoneBubbles.some((b) => b.id === pairIds[1] && b.text === '船都早早靠岸了。'),
      noModelNoLinesNoMorePicks: noModel.chat === null && noModel.never === true && !noModelBubbles.some((b) => b.id === pairIds[1] && b.typing),
      noProblems: problems.length === 0,
    };
    result.pageErrors = await page.evaluate(() => window.__polis.errors ?? []);
    Object.assign(result, { checks, pairIds, ambientSent, ambientDone, noModel, target, lent, sentNow, waiting, thinkingBubbles, partial, replied: { ...replied, panel: undefined }, repliedBubbles, second, cancelled, afterCancel, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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
 * 讲台前的辩论（M-E）：走到讲台前按 E——讲台的卡片（战绩、"提一个议题"）；议题与每一轮的论点都在借给辩论的对话栏里写，
 * 回车只发 debate-start / debate-say（不是 say、不同步草稿）；她走到舞台的一侧被留住；判票时街上的居民围过来站成半圆，
 * 判完按票数的比例为你挥手；"结束"或 Esc 发 debate-end；页面从不自己开辩、不自己陈述。
 */
async function debateCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, debate' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    const inbox = () => page.evaluate(() => window.__hostInbox);
    await send({ type: 'init', tables: {} });
    await send({ type: 'city', key: 'ws-1', city });
    const street = Array.from({ length: 10 }, (_, i) => ({
      id: `conv-${i}`, seed: (Math.imul(i + 21, 2654435761) >>> 0), female: false, name: `第${i}位`, title: `委托 ${i}`,
      home: ['agora:0', 'agora:1', 'agora:2', 'agora:3', 'agora:4', 'agora:5', 'market', 'agora:0', 'agora:2', 'agora:4'][i], age: 'adult', outcome: 'completed', folders: [], turns: 2,
    }));
    await send({ type: 'citizens', citizens: { street, elders: [], census: { total: 10, street: 10, indoors: 0, elders: 0 } } });
    await send({ type: 'composer', conversationId: 'c-1', busy: false, queued: false, waiting: false, compressing: false, canSend: true, attachments: [], draft: '写给雅典娜的半句话' });
    const record = { wins: 2, losses: 1, ties: 0 };
    await send({ type: 'debate', debate: { state: 'idle', record } });
    await page.waitForFunction(() => { const g = window.__polisGame; return g.world.citizens.size === 10 && [...g.world.citizens.values()].every((c) => c.figure && c.plan); }, null, { timeout: 60_000 });
    await page.waitForTimeout(300);

    // 走到讲台的台阶前（按 E 的那一点上，面朝讲台），按 E：讲台的卡片——旁边站着居民也是讲台先被挑中
    const stand = await page.evaluate(() => {
      const g = window.__polisGame;
      const st = g.stage;
      g.setFollow('player');
      g.placePlayer(st.interact.x, st.interact.z, Math.atan2(-st.front.x, -st.front.z));
      return { interact: st.interact, audience: st.audience.length };
    });
    await page.waitForTimeout(250);
    const focus = await page.evaluate(() => window.__polis.focus);
    await page.keyboard.press('KeyE');
    await page.waitForTimeout(200);
    const card = await page.evaluate(() => ({ view: window.__polisGame.panelView(), regions: window.__polisGame.regions.map((r) => r.id) }));
    // 提一个议题：栏借给辩论
    await page.evaluate(() => window.__polisGame.onButton('bema-propose'));
    await page.waitForTimeout(200);
    const motionBar = await page.evaluate(() => ({ talk: window.__polisTalk.state, value: window.__polisTalk.value, focused: document.activeElement?.classList.contains('talk-input'), placeholder: document.querySelector('#talk .talk-input').placeholder }));
    let n = (await inbox()).length;
    await page.keyboard.type('周末应该完全不碰工作');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(200);
    const startSent = (await inbox()).slice(n);
    // C# 开了一场：等你说第一轮
    const base = { id: 'd1', motion: '周末应该完全不碰工作', rounds: 3, jury: 10, record };
    await send({ type: 'debate', debate: { ...base, state: 'awaiting', round: 1, transcript: [] } });
    await page.waitForTimeout(400);
    const round1 = await page.evaluate(() => ({ talk: window.__polisTalk.state, placeholder: document.querySelector('#talk .talk-input').placeholder, athena: window.__polisGame.athenaSim.state(), closeUp: window.__polis.closeUp, audience: window.__polisGame.audience.size }));
    // 议题定下：你自己走上辩台的一端（2026-10-13，用户：日晷前增加一座讲台，两方选手站上去）
    const onDais = await page.waitForFunction(() => {
      const g = window.__polisGame, st = g.stage, p = g.player;
      return !!st.dais && Math.hypot(p.x - st.you.x, p.z - st.you.z) < 0.15;
    }, null, { timeout: 8000 }).then(() => true, () => false);
    const daisNow = await page.evaluate(() => { const g = window.__polisGame, st = g.stage; return { dais: st.dais, you: st.you, athena: st.athena, playerY: g.world.player.position.y, athenaTo: g.athenaSim.state().step.to ?? null, queued: g.athenaSim.state().queued }; });
    n = (await inbox()).length;
    await page.keyboard.type('休息让人第二天更有效率。');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(200);
    const saySent = (await inbox()).slice(n);
    const yourLine = await page.evaluate(() => window.__polisGame.mine?.text ?? null);
    const waiting = await page.evaluate(() => ({ talk: window.__polisTalk.state, disabled: document.querySelector('#talk .talk-input').disabled }));
    await send({ type: 'debate', debate: { ...base, state: 'athena', round: 1, transcript: [{ side: 'you', text: '休息让人第二天更有效率。' }] } });
    await send({ type: 'debate', debate: { ...base, state: 'awaiting', round: 2, transcript: [{ side: 'you', text: '休息让人第二天更有效率。' }, { side: 'athena', text: '休息固然好，可工作不会等你。周一的邮件会把周末的轻松全吃掉。' }] } });
    await page.waitForTimeout(300);
    const round2 = await page.evaluate(() => ({ talk: window.__polisTalk.state, placeholder: document.querySelector('#talk .talk-input').placeholder, regions: window.__polisGame.regions.map((r) => r.id), said: window.__polis.said, board: window.__polisGame.debateBoardView?.() ?? null }));
    // 围观的人起哄（C# 每轮一次模型调用）：放到已经站好的围观者头顶，一句接一句
    await page.waitForFunction(() => { const g = window.__polisGame; return [...g.audience.values()].some((a) => performance.now() - a.start >= a.ms); }, null, { timeout: 12_000 }).catch(() => {});
    await send({ type: 'debate-crowd', id: 'd1', round: 1, reactions: [{ kind: 'cheer', text: '说得好！' }, { kind: 'boo', text: '嘘——邮件可不等人' }] });
    await page.waitForTimeout(2200);
    const crowd = await page.evaluate(() => { const g = window.__polisGame; return [...g.sayings].filter(([id]) => g.audience.has(id)).map(([id, v]) => ({ id, text: v.text })); });
    // 三轮说完：裁决。栏还给雅典娜，陪审团围过来
    const transcript = [
      { side: 'you', text: '休息让人第二天更有效率。' }, { side: 'athena', text: '休息固然好，可工作不会等你。' },
      { side: 'you', text: '那就关掉通知。' }, { side: 'athena', text: '关掉通知，问题不会消失。' },
      { side: 'you', text: '问题可以等到周一。' }, { side: 'athena', text: '有些问题等不起。' },
    ];
    await send({ type: 'debate', debate: { ...base, state: 'judging', round: 3, transcript } });
    await page.waitForTimeout(300);
    const judging = await page.evaluate(() => ({ talk: window.__polisTalk.state, value: window.__polisTalk.value, athenaDraft: window.__polisTalk.athenaDraft, audience: window.__polisGame.audience.size }));
    await page.waitForTimeout(9500);   // 走得最远的也在九秒内到
    const gathered = await page.evaluate(() => {
      const g = window.__polisGame;
      const st = g.stage;
      const people = [...g.audience.entries()].map(([id, a]) => {
        const c = g.world.citizens.get(id);
        const p = c.figure.root.position;
        return { id, x: p.x, z: p.z, to: a.to, onSpot: Math.hypot(p.x - a.to.x, p.z - a.to.z) < 0.35, walkable: g.layout.walkable({ x: p.x, z: p.z }), nearStage: Math.hypot(p.x - st.stage.x, p.z - st.stage.z) < 6 };
      });
      return { count: people.length, people };
    });
    await send({ type: 'debate', debate: { ...base, state: 'done', round: 3, transcript, verdict: { votesForYou: 7, winner: 'you', reason: '正方的例子更具体。', judged: true }, record: { wins: 3, losses: 1, ties: 0 } } });
    await page.waitForTimeout(600);
    const done = await page.evaluate(() => ({ supporters: window.__polis.supporters, audience: window.__polisGame.audience.size, regions: window.__polisGame.regions.map((r) => r.id), debate: window.__polis.debate }));
    n = (await inbox()).length;
    await page.evaluate(() => window.__polisGame.onButton('debate-close'));
    await page.waitForTimeout(200);
    const closeSent = (await inbox()).slice(n);
    const after = await page.evaluate(() => ({ debate: window.__polisGame.debate, audience: window.__polisGame.audience.size }));
    await send({ type: 'debate', debate: { state: 'idle', record: { wins: 3, losses: 1, ties: 0 } } });
    // Esc：第二场辩到一半按 Esc——不辩了
    await send({ type: 'debate', debate: { ...base, id: 'd2', motion: '猫比狗更适合当宠物', state: 'awaiting', round: 1, transcript: [] } });
    await page.waitForTimeout(300);
    const lent2 = await page.evaluate(() => window.__polisTalk.state.mode);
    n = (await inbox()).length;
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    const escSent = (await inbox()).slice(n);
    const afterEsc = await page.evaluate(() => ({ talk: window.__polisTalk.state, value: window.__polisTalk.value, athenaDraft: window.__polisTalk.athenaDraft, debate: window.__polisGame.debate }));
    const all = await inbox();
    const of = (list, type) => list.filter((m) => m.type === type);
    const checks = {
      bemaFocus: focus?.type === 'bema',
      bemaCardWithRecord: card.view?.type === 'bema' && card.view.lines.some((l) => l.includes('2') && l.includes('1')) && card.regions.includes('bema-propose'),
      barLentForMotion: motionBar.talk.mode === 'debate' && motionBar.focused && motionBar.value === '' && motionBar.placeholder.includes('议题'),
      onlyDebateStartSent: startSent.length === 1 && startSent[0].type === 'debate-start' && startSent[0].motion === '周末应该完全不碰工作',
      roundOneLent: round1.talk.mode === 'debate' && round1.placeholder.includes('1') && round1.placeholder.includes('3'),
      athenaGoesToTheStage: round1.athena.queued > 0 || String(round1.athena.step?.to ?? round1.athena.position).startsWith('pt:'),
      bothOnTheDais: onDais && !!daisNow.dais && daisNow.playerY > 0.15 && Math.abs(daisNow.you.z - daisNow.dais.z) < 1e-6 && Math.abs(daisNow.athena.z - daisNow.dais.z) < 1e-6
        && Math.abs(daisNow.you.x - daisNow.athena.x) > 2 && (daisNow.queued > 0 || daisNow.athenaTo === `pt:${daisNow.athena.x.toFixed(2)},${daisNow.athena.z.toFixed(2)}`),
      crowdGathersFromTheStart: round1.audience > 0,
      wordsOverTheirHeads: yourLine === '休息让人第二天更有效率。' && round2.said === '休息固然好，可工作不会等你。周一的邮件会把周末的轻松全吃掉。' && (round2.board?.lines ?? []).length === 0,
      crowdReacts: crowd.length === 2 && crowd.some((c) => c.text === '说得好！') && crowd.some((c) => c.text === '嘘——邮件可不等人') && new Set(crowd.map((c) => c.id)).size === 2,
      closeUpOnTheDebate: round1.closeUp === 'debate',
      onlyDebateSaySent: saySent.length === 1 && saySent[0].type === 'debate-say' && saySent[0].text === '休息让人第二天更有效率。',
      waitsForAthena: waiting.talk.mode === 'debating' && waiting.disabled,
      roundTwoLent: round2.talk.mode === 'debate' && round2.placeholder.includes('2') && round2.regions.includes('debate-close'),
      barGivenBackForJudging: judging.talk.mode === 'speak' && judging.athenaDraft === '写给雅典娜的半句话',
      juryGathers: judging.audience > 0 && gathered.count === judging.audience && gathered.people.every((p) => p.onSpot && p.walkable && p.nearStage),
      supportersWave: done.supporters === Math.round((done.audience * 7) / 10) && done.supporters > 0,
      verdictOnTheBoard: done.debate?.state === 'done' && done.debate.votesForYou === 7 && done.regions.includes('debate-close'),
      closeEndsIt: closeSent.length === 1 && closeSent[0].type === 'debate-end' && after.debate === null && after.audience === 0,
      escEndsIt: lent2 === 'debate' && escSent.length === 1 && escSent[0].type === 'debate-end' && afterEsc.talk.mode === 'speak' && afterEsc.athenaDraft === '写给雅典娜的半句话' && afterEsc.debate === null,
      neverDebatesByItself: of(all, 'debate-start').length === 1 && of(all, 'debate-say').length === 1 && !all.some((m) => m.type === 'say' || (m.type === 'draft' && /周末|休息/.test(m.text ?? ''))),
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, stand, focus, onDais, daisNow, yourLine, crowd, said: round2.said, round1: { mode: round1.talk.mode, placeholder: round1.placeholder, athena: round1.athena, closeUp: round1.closeUp }, judging, gathered: { count: gathered.count, offSpot: gathered.people.filter((p) => !p.onSpot || !p.walkable || !p.nearStage) }, done, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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
 * 户与民居（M-D）：居民先到、民居后到（C# 先推居民的那一刻页面还没有那座民居）——城一变，他们就从民居门前重新出门；
 * 民居的名牌写户名，不写钥匙；新立的一户（origins 'household'）雅典娜说一句"……在城里安了家"；户名变了，名牌跟着换。
 */
async function householdCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, households' };
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    // 一块没人用的地（与 citizens.test.mjs 同一个找法）
    const used = new Set([...city.buildings.map((b) => `${b.plot.x},${b.plot.z}`), ...Object.values(city.publicSites).map((p) => `${p.x},${p.z}`), ...(city.vacant ?? []).map((v) => `${v.plot.x},${v.plot.z}`)]);
    let plot = null;
    for (let r = 1; r <= 3 && !plot; r++) for (let x = -r; x <= r && !plot; x++) for (let z = -r; z <= r && !plot; z++) {
      if (Math.max(Math.abs(x), Math.abs(z)) !== r || z >= city.seaStartsAtZ || used.has(`${x},${z}`)) continue;
      plot = { x, z };
    }
    const house = { key: 'household:abcd1234', label: '整理合同一家', kind: 'house', files: 3, bytes: 0, kinds: {}, lastModified: new Date().toISOString(), state: 'lived', sizeClass: 2, incomplete: false, plot, order: 999 };
    await send({ type: 'init', tables: {} });
    await send({ type: 'city', key: 'ws-1', city });
    const street = Array.from({ length: 3 }, (_, i) => ({
      id: `conv-h${i}`, seed: (Math.imul(i + 41, 2654435761) >>> 0), female: false, name: `整理合同的第${i}位`, title: '整理合同',
      home: 'b:household:abcd1234', age: 'adult', outcome: 'completed', folders: [], turns: 1,
    }));
    await send({ type: 'citizens', citizens: { street, elders: [], census: { total: 3, street: 3, indoors: 0, elders: 0 } } });
    await page.waitForFunction(() => { const g = window.__polisGame; return g.world.citizens.size === 3 && [...g.world.citizens.values()].every((c) => c.figure && c.plan); }, null, { timeout: 60_000 });
    const before = await page.evaluate(() => [...window.__polisGame.world.citizens.values()].map((c) => c.plan.home.site));
    // 民居到了，带着"新立的一户"
    await send({ type: 'city', key: 'ws-1', city: { ...city, buildings: [...city.buildings, house] }, origins: { 'household:abcd1234': 'household' } });
    await page.waitForTimeout(400);
    const after = await page.evaluate(() => ({
      homes: [...window.__polisGame.world.citizens.values()].map((c) => c.plan?.home.site ?? null),
      label: window.__polisGame.world.labels.find((l) => l.key === 'b:household:abcd1234')?.text ?? null,
      leaked: window.__polisGame.world.labels.filter((l) => l.kind === 'building' && l.text.startsWith('household:')).length,
      said: window.__polis.said ?? null,
      building: window.__polisGame.world.buildings.has('b:household:abcd1234'),
    }));
    // 户名变了（立户那一场会话改了标题）：名牌跟着换
    await send({ type: 'city', key: 'ws-1', city: { ...city, buildings: [...city.buildings, { ...house, label: '续签合同一家' }] } });
    await page.waitForTimeout(300);
    const renamed = await page.evaluate(() => window.__polisGame.world.labels.find((l) => l.key === 'b:household:abcd1234')?.text ?? null);
    const checks = {
      waitedOnTheSquare: before.every((s) => s !== 'b:household:abcd1234'),
      houseBuilt: after.building,
      labelIsTheHouseholdName: after.label === '整理合同一家' && after.leaked === 0,
      replannedFromTheHouse: after.homes.every((s) => s === 'b:household:abcd1234'),
      athenaWelcomesThem: typeof after.said === 'string' && after.said.includes('整理合同一家'),
      labelFollowsTheName: renamed === '续签合同一家',
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, plot, before, after, renamed, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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
 * 2026-10-11 实测之后的操作：游戏盖住了输入框，说话在城里的对话栏；视角缺省自由；WASD / 方向键走路，鼠标只管互动。
 */
async function controlsCheck(name, browser) {
  const started = Date.now();
  const result = { browser: `${name} ${browser.version()}`, mode: 'live, controls and talk bar' };
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 860 } });
    const problems = [];
    page.on('console', (m) => { if (m.type() === 'error') problems.push(`[console.error] ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
    await page.addInitScript(HOST);
    await page.goto(`${server.origin}/polis/index.html?mode=live`);
    await page.waitForFunction(() => (window.__hostInbox ?? []).some((m) => m.type === 'ready') || document.title === 'error', null, { timeout: 90_000 });
    const send = (message) => page.evaluate((m) => window.polis.receive(JSON.stringify(m)), message);
    const inbox = () => page.evaluate(() => window.__hostInbox);
    await send({ type: 'init', tables: {} });
    await send({ type: 'city', key: 'ws-1', city, player: { x: 2.6, z: 4.4 } });
    await page.waitForFunction(() => (window.__polis.frames ?? 0) > 5, null, { timeout: 20_000 });
    const talkBefore = await page.evaluate(() => document.getElementById('talk').hidden);
    const viewDefault = await page.evaluate(() => ({ status: window.__polis.view, radio: document.querySelector('#view button[aria-checked="true"]')?.dataset.view }));

    // 视角：V 轮换三种，再回到自由
    const views = [];
    for (let k = 0; k < 3; k++) { await page.keyboard.press('KeyV'); views.push(await page.evaluate(() => window.__polis.view)); }

    // 左键拖动转镜头：方位角变了，没有当成一次点击
    const a0 = await page.evaluate(() => window.__polisGame.cameraAngles());
    await page.mouse.move(400, 300);
    await page.mouse.down();
    await page.mouse.move(520, 300, { steps: 8 });
    await page.mouse.move(520, 840, { steps: 8 });   // 往下拖到头：俯视有上限
    await page.mouse.up();
    const a1 = await page.evaluate(() => window.__polisGame.cameraAngles());

    // WASD：按住 W 走 4 秒，每一刻都站在能走的地方（走不进房子、不下海），确实走出去了
    const p0 = await page.evaluate(() => window.__polis.player);
    const samples = [];
    await page.keyboard.down('KeyW');
    for (let k = 0; k < 8; k++) {
      await page.waitForTimeout(500);
      samples.push(await page.evaluate(() => { const p = window.__polis.player; return { ...p, ok: window.__polisGame.layout.walkable(p) }; }));
    }
    await page.keyboard.up('KeyW');
    await page.keyboard.down('ArrowRight');
    await page.waitForTimeout(600);
    await page.keyboard.up('ArrowRight');
    const p1 = await page.evaluate(() => window.__polis.player);
    await page.waitForTimeout(900);
    const saved = (await inbox()).filter((m) => m.type === 'save-player').at(-1);
    // M3：按住 Shift 跑（动作换成 run，速度更快）
    const idleAction = await page.evaluate(() => window.__polis.playerAction);
    const r0 = await page.evaluate(() => window.__polis.player);
    await page.keyboard.down('Shift');
    await page.keyboard.down('KeyS');
    await page.waitForTimeout(500);
    const runAction = await page.evaluate(() => window.__polis.playerAction);
    await page.keyboard.up('KeyS');
    await page.keyboard.up('Shift');
    const r1 = await page.evaluate(() => window.__polis.player);

    // 对话栏：收到 composer 才出现
    await send({ type: 'composer', conversationId: 'c-1', busy: false, queued: false, waiting: false, compressing: false, canSend: true, attachments: [], draft: '' });
    const talkShown = await page.evaluate(() => !document.getElementById('talk').hidden);
    await page.evaluate(() => { const g = window.__polisGame; g.setFollow('athena'); });
    await page.waitForTimeout(1500);
    // M3 第三轮：点她不再开口（鼠标不点城里的东西）……
    const athenaScreen = await page.evaluate(() => {
      const g = window.__polisGame, a = g.world.athena;
      const p = a.userData.chest.clone().applyMatrix4(a.matrixWorld).project(g.camera);
      return { x: ((p.x + 1) / 2) * innerWidth, y: ((1 - p.y) / 2) * innerHeight };
    });
    await page.mouse.click(athenaScreen.x, athenaScreen.y);
    await page.waitForTimeout(150);
    const clickedOpen = await page.evaluate(() => document.getElementById('talk').classList.contains('open'));
    // ……走到她跟前、面朝她按 E 才开口：脚下先出现"E 和雅典娜说话"，按下去对话栏展开、聚焦
    await page.evaluate(() => { const g = window.__polisGame, a = g.world.athena.position; g.placePlayer(a.x + 1.3, a.z + 0.4, Math.atan2(-1.3, -0.4)); });
    await page.waitForTimeout(200);
    const athenaFocus = await page.evaluate(() => ({ focus: window.__polis.focus, prompt: window.__polisGame.used.labels.some((l) => l.kind === 'prompt') }));
    // 开口之前镜头的远近与俯角：收起之后要退回的就是它
    const viewBefore = await page.evaluate(() => ({ zoom: window.__polisGame.camera.zoom, elevation: window.__polisGame.cameraAngles().elevation }));
    await page.keyboard.press('KeyE');
    const opened = await page.evaluate(() => ({ open: document.getElementById('talk').classList.contains('open'), focused: document.activeElement?.classList.contains('talk-input') }));
    await page.waitForTimeout(300);
    const talkAction = await page.evaluate(() => ({ player: window.__polis.playerAction, athena: window.__polis.athenaPose }));
    // M3（决定 4）：对话栏开着，镜头推成近景；收起就退回
    await page.waitForTimeout(1200);
    const closeUp = await page.evaluate(() => ({ kind: window.__polis.closeUp, zoom: window.__polisGame.camera.zoom, elevation: window.__polisGame.cameraAngles().elevation }));
    // 在对话栏里打字：W 是字，人不动
    const beforeTyping = await page.evaluate(() => window.__polis.player);
    await page.keyboard.type('wasd 把合同列个清单');
    await page.waitForTimeout(700);
    const afterTyping = await page.evaluate(() => ({ player: window.__polis.player, value: window.__polisTalk.value }));
    const draft = (await inbox()).filter((m) => m.type === 'draft').at(-1);
    // 输入法正在选字时的 Enter：不托付
    await page.evaluate(() => document.querySelector('#talk .talk-input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', isComposing: true, bubbles: true })));
    const saysWhileComposing = (await inbox()).filter((m) => m.type === 'say').length;
    await page.keyboard.press('Enter');
    const said = (await inbox()).filter((m) => m.type === 'say');
    const afterSay = await page.evaluate(() => ({ open: document.getElementById('talk').classList.contains('open'), value: window.__polisTalk.value }));
    // 她在办事：托付键换成"请她停下"（只在和她说话时有——2026-10-13 起平时的栏是你自己说话：再按 E 和她说话）
    await send({ type: 'composer', conversationId: 'c-1', busy: true, queued: false, waiting: false, compressing: false, canSend: false, attachments: ['合同.docx'] });
    await page.keyboard.press('KeyE');
    await page.waitForTimeout(150);
    const busyBar = await page.evaluate(() => ({ stop: !document.querySelector('#talk .talk-stop').hidden, send: !document.querySelector('#talk .talk-send').hidden }));
    await page.click('#talk .talk-stop');
    const stopSent = (await inbox()).some((m) => m.type === 'stop-turn');
    // 在城里按 Enter：你自己说话（2026-10-13）——栏是你的小像，说出去的话在你头顶，不托付给雅典娜；"/"开头的命令还在路上
    await page.evaluate(() => document.activeElement?.blur?.());
    await page.keyboard.press('Escape');
    await page.keyboard.press('Enter');
    const enterOpens = await page.evaluate(() => ({ open: document.getElementById('talk').classList.contains('open'), mode: window.__polisTalk.mode, name: document.querySelector('#talk .talk-name').textContent }));
    const nSpeak = (await inbox()).length;
    await page.keyboard.type('今天天气不错');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(150);
    const spoke = await page.evaluate(() => ({ mine: window.__polisGame.mine?.text ?? null, spoke: window.__polis.spoke, open: document.getElementById('talk').classList.contains('open'), mode: window.__polisTalk.mode }));
    await page.keyboard.press('Enter');
    await page.keyboard.type('/help');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(150);
    const commanded = await page.evaluate(() => ({ mine: window.__polisGame.mine?.text ?? null, spoke: window.__polis.spoke }));
    const speakSent = (await inbox()).slice(nSpeak);
    // 托付时栏收起，镜头才开始往回退（走完两秒多），这时又开口：近景记住的必须是你原来的远近与俯角，不是退到半路的那个。
    // 等页面真的进了近景再收起——以前 Enter 与 Esc 只隔 1–4 毫秒，只有机器忙、恰好一帧落在两者之间才走到这条路（2026-10-12）。
    // 近景只在和雅典娜说话时有：按 E（你还站在她身边）
    await page.keyboard.press('KeyE');
    const reopened = await page.waitForFunction(() => window.__polis.closeUp === 'talk', null, { timeout: 5000 }).then(() => true, () => false);
    await page.keyboard.press('Escape');
    // 收起：等镜头退回开口之前的远近与俯角（按条件等，不按固定时长）；退不回去就到时限，读出它停在哪儿
    await page.waitForFunction((v) => {
      const g = window.__polisGame;
      return window.__polis.closeUp == null && Math.abs(g.camera.zoom - v.zoom) < 0.01 && Math.abs(g.cameraAngles().elevation - v.elevation) < 0.5;
    }, viewBefore, { timeout: 8000 }).catch(() => {});
    const afterClose = await page.evaluate(() => ({ kind: window.__polis.closeUp, zoom: window.__polisGame.camera.zoom, elevation: window.__polisGame.cameraAngles().elevation }));
    // 退回的半路上滚轮：远近归你，镜头不再把它拽回开口前的值；俯角照样退回去（2026-10-12：滚轮像失灵，只有往回滚或停手等才恢复）。
    // 滚轮在一次 evaluate 里同步派发、当场读远近——不靠这台机器的帧率赶上某个时间窗
    await page.keyboard.press('KeyE');
    await page.waitForFunction(() => window.__polis.closeUp === 'talk', null, { timeout: 5000 }).catch(() => {});
    await page.waitForFunction(() => window.__polisGame.camera.zoom > 3, null, { timeout: 8000 }).catch(() => {});
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => window.__polis.closeUp == null, null, { timeout: 2000 }).catch(() => {});
    const wheel = await page.evaluate(() => {
      const g = window.__polisGame, canvas = document.querySelector('canvas');
      const before = g.camera.zoom, elevation = g.cameraAngles().elevation;
      canvas.dispatchEvent(new WheelEvent('wheel', { deltaY: -200, clientX: innerWidth / 2, clientY: innerHeight / 2, bubbles: true, cancelable: true }));
      return { before, after: g.camera.zoom, elevation };
    });
    // 俯角接着退：这台机器一帧几百毫秒、帧间隔又封顶 100 毫秒，镜头走得比墙钟慢几倍，所以只要求它还在往回走、没走完也行
    await page.waitForFunction((v) => Math.abs(window.__polisGame.cameraAngles().elevation - v.elevation) < 0.5, viewBefore, { timeout: 8000 }).catch(() => {});
    const wheeledDuringRestore = await page.evaluate(() => ({ kind: window.__polis.closeUp, zoom: window.__polisGame.camera.zoom, elevation: window.__polisGame.cameraAngles().elevation }));
    // 预填（委托板、退回……；2026-10-13，用户：文字留着等你去找她）：不自己展开、不发送；她说一句"过来找我"；走到她身边按 E，栏带着这段话打开
    const saysBeforePrefill = (await inbox()).filter((m) => m.type === 'say').length;
    await send({ type: 'composer', conversationId: 'c-2', busy: false, queued: false, waiting: false, compressing: false, canSend: true, attachments: [], draft: '关于《摘要》，请修改：', open: true });
    await page.waitForTimeout(200);
    const prefilled = await page.evaluate(() => ({ open: document.getElementById('talk').classList.contains('open'), waiting: window.__polis.athenaHasWords === true, said: window.__polis.said, draft: window.__polisTalk.athenaDraft }));
    await page.keyboard.press('KeyE');
    await page.waitForTimeout(200);
    const prefillAtAthena = await page.evaluate(() => ({ open: document.getElementById('talk').classList.contains('open'), mode: window.__polisTalk.mode, value: window.__polisTalk.value, focused: document.activeElement?.classList.contains('talk-input'), waiting: window.__polis.athenaHasWords === true }));
    const saysAfterPrefill = (await inbox()).filter((m) => m.type === 'say').length;
    await page.keyboard.press('Escape');
    // 你在远处开口（2026-10-13）：站到港口去按 Enter——那是你自己说话，她不会被叫过来，也不再瞬移
    await page.waitForFunction(() => !document.getElementById('talk').classList.contains('open'), null, { timeout: 3000 }).catch(() => {});
    const farAway = await page.evaluate(() => {
      const g = window.__polisGame, h = g.layout.sitePoint('harbor'), a = g.world.athena.position;
      g.placePlayer(h.x, h.z - 1.2, Math.PI);
      return { player: { x: h.x, z: h.z - 1.2 }, athena: { x: a.x, z: a.z }, steps: g.athenaSim.steps.length };
    });
    await page.waitForTimeout(200);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(2500);
    const summon = await page.evaluate((n) => {
      const g = window.__polisGame, a = g.world.athena.position, p = g.player;
      return { distance: Math.hypot(a.x - p.x, a.z - p.z), mode: window.__polisTalk.mode, summoned: window.__polis.summoned ?? 0, newSteps: g.athenaSim.steps.slice(n).map((s) => ({ kind: s.kind, to: s.to ?? s.at })) };
    }, farAway.steps);
    await page.keyboard.press('Escape');
    // M3 第三轮：夜里跟着应用的深色主题——推一条 theme，页面上的条立刻换，城里一秒多走到夜里；换回来也一样
    await send({ type: 'theme', night: true });
    await page.waitForFunction(() => window.__polis.night === 1, null, { timeout: 5000 }).catch(() => {});
    const nightNow = await page.evaluate(() => ({ body: document.body.classList.contains('night'), k: window.__polis.night, overlay: window.__polisGame.overlay.night, glow: window.__polisGame.R.glow.enabled, lamps: window.__polisGame.world.lights().length, pixels: window.__polis.pixelStats() }));
    await send({ type: 'theme', night: false });
    await page.waitForFunction(() => window.__polis.night === 0, null, { timeout: 5000 }).catch(() => {});
    const dayAgain = await page.evaluate(() => ({ body: document.body.classList.contains('night'), k: window.__polis.night, glow: window.__polisGame.R.glow.enabled, pixels: window.__polis.pixelStats() }));
    // 卷轴阅读器：一卷很长的回答（含表格）能滚到最后一行，最后一行在卷轴里看得见
    const longMd = `# 长卷\n\n| 列 | 值 |\n|---|---:|\n| a | 1 |\n| b | 2 |\n\n${Array.from({ length: 80 }, (_, i) => `- 第 ${i + 1} 行：一段足够长的说明文字，确保这一卷比窗口高得多`).join('\n')}\n\n最后一行。`;
    await send({ type: 'scroll', itemId: 'long', title: '长卷', markdown: longMd, closeLabel: '收起' });
    await page.waitForTimeout(200);
    const readerFit = await page.evaluate(async () => {
      const root = document.getElementById('reader'), card = root.querySelector('.scroll'), body = root.querySelector('.body');
      body.scrollTop = body.scrollHeight;
      await new Promise((r) => setTimeout(r, 120));
      const c = card.getBoundingClientRect(), b = body.getBoundingClientRect(), last = body.lastElementChild.getBoundingClientRect();
      return { cardInside: c.top >= 0 && c.bottom <= innerHeight + 0.5, scrollable: body.scrollHeight > body.clientHeight + 100, lastVisible: last.bottom <= b.bottom + 0.5 && last.top >= b.top, table: !!body.querySelector('table th.align-right, table td.align-right') };
    });
    await page.keyboard.press('Escape');
    const all = await inbox();
    const checks = {
      talkHiddenUntilComposer: talkBefore === true && talkShown === true,
      // 第四轮需求 7：缺省跟着你
      followsYouByDefault: viewDefault.status === 'player' && viewDefault.radio === 'player',
      viewCycles: views.join(',') === 'athena,free,player',
      dragTurns: Math.abs(a1.azimuth - a0.azimuth) > 20 && a1.elevation <= 72.001 && a1.elevation > a0.elevation,
      walks: Math.hypot(samples.at(-1).x - p0.x, samples.at(-1).z - p0.z) > 5 && Math.hypot(p1.x - samples.at(-1).x, p1.z - samples.at(-1).z) > 1,
      neverInsideWalls: samples.every((s) => s.ok),
      runsWithShift: idleAction === 'idle' && runAction === 'run' && Math.hypot(r1.x - r0.x, r1.z - r0.z) > 3.5,
      talksAtTheBar: talkAction.player === 'talk',
      closeUpWhileTalking: closeUp.kind === 'talk' && closeUp.zoom > 3.5 && closeUp.elevation < 32,
      wheelTakesOverRestore: wheeledDuringRestore.kind == null && wheel.before > viewBefore.zoom + 0.3 && wheel.after > wheel.before && Math.abs(wheeledDuringRestore.zoom - wheel.after) < 1e-6 && Math.abs(wheeledDuringRestore.elevation - viewBefore.elevation) < Math.abs(wheel.elevation - viewBefore.elevation) / 2,
      zoomRestoredAfterClose: reopened && afterClose.kind == null && Math.abs(afterClose.zoom - viewBefore.zoom) < 0.01 && Math.abs(afterClose.elevation - viewBefore.elevation) < 0.5,
      savesWhenStopped: !!saved && Math.abs(saved.x - p1.x) < 0.05 && Math.abs(saved.z - p1.z) < 0.05,
      clickingDoesNotInteract: clickedOpen === false,
      approachAndPressEOpensTalk: athenaFocus.focus?.type === 'athena' && athenaFocus.prompt && opened.open && opened.focused,
      typingIsNotWalking: afterTyping.value === 'wasd 把合同列个清单' && afterTyping.player.x === beforeTyping.x && afterTyping.player.z === beforeTyping.z,
      draftSynced: draft?.conversationId === 'c-1' && draft?.text === 'wasd 把合同列个清单',
      composingEnterDoesNotSend: saysWhileComposing === 0,
      enterEntrusts: said.length === 1 && said[0].conversationId === 'c-1' && said[0].text === 'wasd 把合同列个清单' && !afterSay.open && afterSay.value === '',
      stopWhileBusy: busyBar.stop && !busyBar.send && stopSent,
      enterSpeaksForYourself: enterOpens.open && enterOpens.mode === 'speak' && enterOpens.name === '你',
      speakingIsNotEntrusting: spoke.mine === '今天天气不错' && spoke.spoke?.kind === 'say' && spoke.mode === 'speak' && !spoke.open
        && !speakSent.some((m) => m.type === 'say' || m.type === 'draft'),
      commandsAreReserved: commanded.spoke?.kind === 'command' && commanded.mine === '（「/」命令还在路上）' && speakSent.length === 0,
      prefillWaitsAtAthena: !prefilled.open && prefilled.waiting && prefilled.draft === '关于《摘要》，请修改：' && /写好的话/.test(prefilled.said ?? '')
        && prefillAtAthena.open && prefillAtAthena.mode === 'talk' && prefillAtAthena.focused && prefillAtAthena.value === '关于《摘要》，请修改：' && !prefillAtAthena.waiting
        && saysAfterPrefill === saysBeforePrefill,
      athenaIsNotSummonedFromAfar: Math.hypot(farAway.athena.x - farAway.player.x, farAway.athena.z - farAway.player.z) > 12 && summon.distance > 12
        && summon.mode === 'speak' && summon.summoned === 0 && !summon.newSteps.some((x) => x.kind === 'teleport' || /^pt:/.test(x.to ?? '')),
      nightFollowsTheme: nightNow.body && nightNow.k === 1 && nightNow.overlay && !dayAgain.body && dayAgain.k === 0 && nightNow.pixels.mean < dayAgain.pixels.mean - 20,
      // 夜里的辉光（灯火的散射）只在夜里开；城里有灯
      nightGlow: nightNow.glow === true && dayAgain.glow === false && nightNow.lamps > 0,
      longScrollReachesTheEnd: readerFit.cardInside && readerFit.scrollable && readerFit.lastVisible && readerFit.table,
      neverApproves: !all.some((m) => /approv|allow|grant/i.test(m.type)),
      noProblems: problems.length === 0,
    };
    Object.assign(result, { checks, angles: { before: a0, after: a1 }, walked: { from: p0, to: p1 }, closeUp: { before: viewBefore, during: closeUp, reopened, afterClose, wheel, wheeledDuringRestore }, summon: { farAway, spoke, commanded, prefilled, prefillAtAthena, ...summon }, problems, seconds: +((Date.now() - started) / 1000).toFixed(1) });
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
 * 一个最小的 GLB（glTF 2.0 二进制）：一只 w × h × d 的盒子，底面在 y = 0，带一张内嵌在 GLB 里的 2 × 2 贴图。测试现写现用，不下载任何素材。
 * 贴图不是装饰：真的外部模型几乎都把贴图打在 GLB 里，GLTFLoader 用 fetch(blob:) 读它，页面的 CSP 少了 connect-src blob:
 * 就只剩白模、只在控制台留一行错误（2026-10-11，一个文生 3D 的雅典娜；之前这只盒子没有贴图，所以一直测不出来）。
 */
function boxGlb(w, h, d) {
  const x = w / 2, z = d / 2;
  const positions = new Float32Array([-x, 0, -z, x, 0, -z, x, h, -z, -x, h, -z, -x, 0, z, x, 0, z, x, h, z, -x, h, z]);
  const uvs = new Float32Array([0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1]);
  const indices = new Uint16Array([0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3]);
  const png = tinyPng(2, 2, [200, 190, 175, 255, 120, 110, 100, 255, 120, 110, 100, 255, 200, 190, 175, 255]);
  const parts = [Buffer.from(positions.buffer), Buffer.from(uvs.buffer), Buffer.from(indices.buffer), png];   // 前三段都是 4 字节的整数倍
  const bin = Buffer.concat(parts);
  let offset = 0;
  const bufferViews = parts.map((p) => { const v = { buffer: 0, byteOffset: offset, byteLength: p.length }; offset += p.length; return v; });
  const json = {
    asset: { version: '2.0', generator: 'athena smoke test' },
    scene: 0, scenes: [{ nodes: [0] }], nodes: [{ mesh: 0 }],
    meshes: [{ primitives: [{ attributes: { POSITION: 0, TEXCOORD_0: 1 }, indices: 2, material: 0 }] }],
    materials: [{ pbrMetallicRoughness: { baseColorTexture: { index: 0 }, metallicFactor: 0, roughnessFactor: 0.9 } }],
    textures: [{ source: 0, sampler: 0 }],
    samplers: [{}],
    images: [{ bufferView: 3, mimeType: 'image/png' }],
    buffers: [{ byteLength: bin.length }],
    bufferViews,
    accessors: [
      { bufferView: 0, componentType: 5126, count: 8, type: 'VEC3', min: [-x, 0, -z], max: [x, h, z] },
      { bufferView: 1, componentType: 5126, count: 8, type: 'VEC2' },
      { bufferView: 2, componentType: 5123, count: indices.length, type: 'SCALAR' },
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

/** 一张 RGBA 的小 PNG（每行一个滤波字节 0，zlib 压缩，块带 CRC）。 */
function tinyPng(width, height, rgba) {
  const stride = width * 4 + 1;
  const raw = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) Buffer.from(rgba.slice(y * width * 4, (y + 1) * width * 4)).copy(raw, y * stride + 1);
  const block = (type, data) => {
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body));
    return Buffer.concat([len, body, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0); ihdr.writeUInt32BE(height, 4); ihdr[8] = 8; ihdr[9] = 6;   // 8 位、RGBA
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), block('IHDR', ihdr), block('IDAT', deflateSync(raw)), block('IEND', Buffer.alloc(0))]);
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
