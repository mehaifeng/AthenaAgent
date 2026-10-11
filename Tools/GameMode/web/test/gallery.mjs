// 美术审查的截图（M2 完成标准 c）：每件资产游戏视距与中景各一张（只截资产那一块，按真实像素），
// 建筑的每个子型、几种状态，橄榄的四个树龄，每个人物的每个动作一张连续帧（上一行游戏视距、下一行中景），
// 每个人物的半身像（三种神情，大的看线条、小的是对话面板里的实际大小）。截图存进被忽略的 Tools/GameMode/.local/m2-gallery/。
//   bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/gallery.mjs [--only building.] [--out <目录>] [--browser chromium]
import { mkdirSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { startServer } from './serve.mjs';
import { launch } from './playwright.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), []));
const out = args.out ?? fileURLToPath(new URL('../../.local/m2-gallery/', import.meta.url));
const only = args.only ?? '';
const browserName = args.browser ?? 'chromium';

// 与 Assets/Polis/src/art/buildings.mjs 的 SUBTYPES 一致（页面的资产表是事实来源；这里列出来是为了逐个渲染）
const SUBTYPES = {
  stoaLibrary: ['straight', 'wings', 'double'],
  house: ['courtyard', 'balcony', 'pergola', 'twin'],
  workshop: ['potter', 'weaver', 'carpenter', 'mason'],
  sculptureGarden: ['arc', 'avenue', 'exedra'],
  treasury: ['doric', 'ionic', 'acroteria'],
  warehouse: ['store', 'granary', 'jarShed'],
};
const ATHENA_WORK = ['read', 'write', 'hammer', 'summon', 'haul', 'await'];
const BUSTS = ['athena', 'player', 'surveyor', 'scribe', 'accountant', 'smith', 'sailor', 'librarian', 'maiden', 'herald', 'painter', 'artisan', 'priestess'];

const server = await startServer();
const browser = await launch(browserName);
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
const problems = [];
page.on('console', (m) => { if (m.type() === 'error') problems.push(m.text()); });
page.on('pageerror', (e) => problems.push(e.message));
const shots = [];

async function open(query) {
  await page.goto(`${server.origin}/polis/index.html?${query}`);
  await page.waitForFunction(() => document.title === 'gallery-ready' || document.title === 'error', null, { timeout: 60_000 });
  const st = await page.evaluate(() => ({ title: document.title, gallery: window.__polis.gallery, list: window.__polis.list, errors: window.__polis.errors }));
  if (st.title === 'error') throw new Error(`${query}: ${st.errors.join(' | ')}`);
  return st;
}

async function shoot(query, file, { clip = true } = {}) {
  const started = Date.now();
  const st = await open(query);
  const target = path.join(out, file);
  mkdirSync(path.dirname(target), { recursive: true });
  await page.screenshot({ path: target, clip: clip && st.gallery?.clip ? st.gallery.clip : undefined, fullPage: !clip });
  shots.push({ file, query, clip: st.gallery?.clip ?? null, ms: Date.now() - started });
  console.log(`${file}${st.gallery?.clip ? `  ${st.gallery.clip.width}×${st.gallery.clip.height}` : ''}`);
}

try {
  const { list } = await open('gallery=list');
  for (const entry of list) {
    if (!entry.id.startsWith(only)) continue;
    const [kind, name] = entry.id.split('.');
    if (kind === 'building') {
      for (const variant of SUBTYPES[name]) {
        // 有的子型只在大体量出现（两层带廊的民居、双排柱的柱廊书库）：按它能出现的体量渲染
        const size = ['balcony', 'double'].includes(variant) ? 3 : 2;
        for (const view of ['game', 'mid']) await shoot(`gallery=${entry.id}&view=${view}&variant=${variant}&size=${size}&state=lived`, `building/${name}.${variant}.${view}.png`);
      }
      // 状态：常用、爬藤、废墟（同一个子型），游戏视距
      for (const state of ['bustling', 'ivy', 'ruin']) await shoot(`gallery=${entry.id}&view=game&variant=${SUBTYPES[name][0]}&size=3&state=${state}`, `building/${name}.state-${state}.game.png`);
      continue;
    }
    if (kind === 'figure') {
      for (const view of ['game', 'mid']) await shoot(`gallery=${entry.id}&view=${view}`, `figure/${name}.still.${view}.png`);
      // M3：跑、说话、挥手、接过也各一张连续帧（弹簧骨上的衣摆在走和跑的帧里有滞后）
      for (const action of ['walk', 'run', 'idle', 'deliver', 'talk', 'wave', 'receive']) await shoot(`gallery=${entry.id}&action=${action}&frames=8`, `figure/${name}.${action}.png`, { clip: false });
      const works = name === 'athena' ? ATHENA_WORK : [null];
      for (const variant of works) await shoot(`gallery=${entry.id}&action=work&frames=8${variant ? `&variant=${variant}` : ''}`, `figure/${name}.work${variant ? `-${variant}` : ''}.png`, { clip: false });
      continue;
    }
    if (entry.id === 'plant.olive') {
      for (const stage of ['sapling', 'young', 'grown', 'ancient']) {
        for (const view of ['game', 'mid']) await shoot(`gallery=${entry.id}&view=${view}&variant=${stage}`, `plant/olive.${stage}.${view}.png`);
      }
      continue;
    }
    for (const view of ['game', 'mid']) await shoot(`gallery=${entry.id}&view=${view}`, `${kind}/${name}.${view}.png`);
  }
  if ('bust:'.startsWith(only) || only.startsWith('bust')) {
    for (const c of BUSTS) await shoot(`gallery=bust:${c}`, `bust/${c}.png`, { clip: false });
  }
} finally {
  writeFileSync(path.join(out, 'index.json'), JSON.stringify({ browser: `${browserName} ${browser.version()}`, shots, problems }, null, 2));
  await browser.close();
  await server.close();
}
console.log(`${shots.length} 张，${problems.length} 个页面问题${problems.length ? `：\n  ${problems.join('\n  ')}` : ''}`);
process.exit(problems.length ? 1 : 0);
