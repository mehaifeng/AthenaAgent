// 整座城的碰撞（M3 决定 1）：在 Node 里按合成夹具把城建出来（three.js 不需要画布），盖出占用栅格，核对：
// 房子、公共建筑、广场的家具、树干、底座、空地的地基石挡路；街、广场、市集的摊位之间、树冠下、城门洞能走；
// 雅典娜站过的每个地方（站立点、闲逛点、沿街的路线）你都走得到；居民出门的那段路没有东西挡着。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import { createLayout, BLOCK, PLOT } from '../../../../Assets/Polis/src/layout.mjs';
import { createAssetLibrary } from '../../../../Assets/Polis/src/art/assets.mjs';
import { buildWorld, findOuting } from '../../../../Assets/Polis/src/world.mjs';
import { slide } from '../../../../Assets/Polis/src/controls.mjs';
import { demoAccumulation } from '../../../../Assets/Polis/src/demo.mjs';
import { PLAYER_RADIUS } from '../../../../Assets/Polis/src/footprint.mjs';
import { TERRACE } from '../../../../Assets/Polis/src/civic-layout.mjs';
import { RESIDENT_ROLES } from '../../../../Assets/Polis/src/art/characters.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));

/** 材质的替身（Node 里没有画布）。 */
function stubMaterials() {
  const cache = new Map();
  return new Proxy({}, {
    get: (_, key) => {
      if (typeof key !== 'string') return undefined;
      if (!cache.has(key)) {
        cache.set(key, key.endsWith('Shades')
          ? [0, 1, 2, 3, 4].map((i) => new THREE.MeshStandardMaterial({ name: `${key}${i}` }))
          : new THREE.MeshStandardMaterial({ name: key }));
      }
      return cache.get(key);
    },
  });
}

/** sanctuary：神殿（全局对话的城）——没有文件夹的建筑，神庙放大成主角。 */
function buildCity({ demo = true, sanctuary = false } = {}) {
  const M = stubMaterials();
  const assets = createAssetLibrary(M);
  const acc = demoAccumulation(fixture);
  const city = sanctuary ? { ...fixture, buildings: [], vacant: [] } : fixture;
  const layout = createLayout({ ...city, groveTrees: demo ? acc.trees.length : 0 });
  const scene = new THREE.Scene();
  const started = performance.now();
  const world = buildWorld(scene, city, layout, M, { assets, sanctuary });
  const built = performance.now() - started;
  if (demo) {
    world.setGrove(acc.trees);
    world.setResidents(acc.residents, (k) => k);
    world.setOracle(true);
  }
  return { world, layout, assets, acc, built };
}

const W = (layout, x, z) => layout.walkable({ x, z });
const plotCenter = (key) => fixture.buildings.find((b) => b.key === key).plot;

test('city collision: buildings, civic buildings and the agora furniture block by their own shape', () => {
  const { world, layout, built } = buildCity();
  assert.ok(world.occupancy.stamped > 30, `盖了 ${world.occupancy.stamped} 件`);
  assert.ok(built < 4000, `建城（含足迹）${built.toFixed(0)} ms`);
  for (const b of fixture.buildings) {
    const { x, z } = b.plot;
    // 雕塑园是一座园子（雕像与矮墙各挡各的，中间是园径）；废墟掀了顶、墙上有豁口，能走进去——别的建筑中间都是屋里
    if (b.kind !== 'sculptureGarden' && b.state !== 'ruin') assert.ok(!W(layout, x * BLOCK, z * BLOCK), `${b.key}（${b.kind}，${b.state}）：房子中间进不去`);
    if (b.state === 'ruin' && b.kind === 'house') assert.ok(W(layout, x * BLOCK, z * BLOCK), `${b.key}：废墟里能走进去`);
    assert.ok(world.buildings.get(`b:${b.key}`).group.userData.footprint.area > 10, `${b.key}：足迹至少 10 m²`);
    assert.ok(W(layout, x * BLOCK + 9, z * BLOCK + 9), `${b.key}：街口能走`);
  }
  // 地块不再整块封死：房子四周、地块之内的空地能走（民居 6.6 米宽，地块 12.5 米）
  const house = plotCenter('杂物');
  assert.ok(W(layout, house.x * BLOCK + PLOT / 2 - 0.5, house.z * BLOCK + PLOT / 2 - 0.5), '民居地块的角落（院墙之外）能站——不是套在地块外面的空气墙');
  const temple = layout.plots.get('temple');
  assert.ok(!W(layout, temple.cx, temple.cz), '神庙里进不去');
  assert.ok(!W(layout, temple.cx, temple.cz + 5.2), '神庙的台基从第二级起就挡（第一级迈得上去）');
  const market = layout.plots.get('market');
  assert.ok(!W(layout, market.cx - 4.2, market.cz - 3.2 + 0.4), '市集的摊位（柜台）挡');
  assert.ok(W(layout, market.cx - 4.2 + 1.45, market.cz - 0.5), '市集的摊位之间能走——以前整块市集都封着');
  // 广场：委托板、讲台、日晷、长凳、方柱头像各挡自己那一块，中间是开阔地
  assert.ok(!W(layout, 0, -4.2), '委托板');
  assert.ok(!W(layout, -4.4, -2.05), '讲台');
  assert.ok(!W(layout, -4.6, -3.4), '日晷');
  assert.ok(!W(layout, 4.2, 1.5), '长凳');
  assert.ok(!W(layout, 4.6, -3.6), '方柱头像');
  assert.ok(W(layout, 0, 0) && W(layout, 2.5, -2.5) && W(layout, -2, 2), '广场中间开阔');
  // 城门洞能过
  assert.ok(W(layout, layout.gate.x, layout.gate.z - 1.5), '城门洞');
  assert.ok(!W(layout, layout.gate.x + 2.6, layout.gate.z - 1.5), '门柱');
});

test('city collision: trees block their trunks only; pedestals, grove trunks, the oracle tripod and vacant-plot stones block', () => {
  const { world, layout } = buildCity();
  const trees = [];
  // 空地上的橄榄（城的范围之内的那些：最外一圈的空地有一半在城墙外，那里本来就没有地）
  world.root.traverse((o) => { if (o.userData.asset?.id === 'plant.olive' && o.parent === world.root && layout.floor(o.position)) trees.push(o); });
  assert.ok(trees.length > 3, `空地上有 ${trees.length} 棵橄榄`);
  let underTotal = 0, tried = 0;
  for (const t of trees) {
    const p = t.getWorldPosition(new THREE.Vector3());
    assert.ok(!W(layout, p.x, p.z), `树干 (${p.x.toFixed(1)}, ${p.z.toFixed(1)}) 挡`);
    // 树冠半径 1.2 米以上，树干半径 0.14 + 人的半径 0.25：1 米外在树冠底下、能走（别的树和房子不在那里的话）
    for (const a of [0, 1.5, 3, 4.5]) {
      const q = { x: p.x + Math.cos(a), z: p.z + Math.sin(a) };
      if (!layout.floor(q)) continue;
      tried++;
      if (W(layout, q.x, q.z)) underTotal++;
    }
  }
  assert.ok(underTotal >= tried * 0.7, `树冠下能走（${underTotal}/${tried} 个方向）——树只挡树干`);
  const archive = layout.plots.get('archive');
  assert.ok(!W(layout, archive.cx - 4.6, archive.cz + 2.0), '场馆的底座挡');
  assert.ok(W(layout, archive.cx, archive.cz + 3.4), '场馆两排底座之间的过道能站');
  assert.ok(!W(layout, archive.cx, archive.cz - 3), '场馆的建筑挡');
  for (const dz of [-6, -3, 0, 3.4]) assert.ok(W(layout, archive.cx + TERRACE.sideWalk, archive.cz + dz), `石台东侧的步道（x = ${TERRACE.sideWalk}）在 z = ${dz} 处能走`);
  const stoa = layout.plots.get('paintedStoa');
  for (const dz of [-6, -3, 0, 3.4]) assert.ok(W(layout, stoa.cx + TERRACE.sideWalk, stoa.cz + dz), `最宽的彩绘柱廊旁的步道在 z = ${dz} 处能走`);
  const tree = world.groveTrees[0];
  assert.ok(tree && !W(layout, tree.x, tree.z), '橄榄林的树干（实例化）挡');
  const grove0 = layout.plots.get('grove:0');
  assert.ok(W(layout, grove0.stand.x, grove0.stand.z), '橄榄林台地北沿的站立点能站');
  const temple = layout.plots.get('temple');
  assert.ok(!W(layout, temple.cx, temple.cz + PLOT / 2 - 0.4), '设立了神谕：神庙前的三足鼎挡');
  world.setOracle(false);
  assert.ok(W(layout, temple.cx, temple.cz + PLOT / 2 - 0.4), '撤了神谕：那里又能站');
  // 空地的地基石
  world.vacate({ key: '网站', plot: plotCenter('网站') });
  const v = plotCenter('网站');
  assert.ok(!W(layout, v.x * BLOCK + 4, v.z * BLOCK + 0.0), '空地上一圈地基石挡（东边那块）');
  assert.ok(W(layout, v.x * BLOCK, v.z * BLOCK), '地基石围着的中间是空地，能走');
});

test('city collision: every place Athena stands or walks you can reach; residents walk clear paths', () => {
  const { world, layout } = buildCity();
  const sites = ['agora', 'harbor', 'gate', 'temple', 'library', 'forge', 'market', 'paintedStoa', 'archive', 'vault', 'grove:0',
    ...fixture.buildings.map((b) => `b:${b.key}`), ...layout.wanderSpots.map((_, k) => `agora:${k}`)];
  for (const site of sites) {
    const p = layout.sitePoint(site);
    assert.ok(W(layout, p.x, p.z), `${site} 的站立点能走到（${p.x.toFixed(1)}, ${p.z.toFixed(1)}）`);
  }
  // 沿街的路线：每 0.25 米采样
  const pairs = [['agora', 'harbor'], ['gate', 'b:合同'], ['b:账本', 'archive'], ['temple', 'vault'], ['b:杂物', 'grove:0'], ['library', 'b:旧项目']];
  for (const [a, b] of pairs) {
    const pts = layout.route(a, b);
    for (let k = 1; k < pts.length; k++) {
      const p = pts[k - 1], q = pts[k];
      const len = Math.hypot(q.x - p.x, q.z - p.z);
      for (let d = 0; d <= len; d += 0.25) {
        const u = len > 0 ? d / len : 0;
        const x = p.x + (q.x - p.x) * u, z = p.z + (q.z - p.z) * u;
        assert.ok(W(layout, x, z), `${a} → ${b} 的路线在 (${x.toFixed(1)}, ${z.toFixed(1)}) 被挡`);
      }
    }
  }
  // 居民：家门口能站（传令官站在讲台上是例外；有工位的——铁匠、工匠、画师——站在活儿跟前，铁砧、石料、画架就在他身前，
  // 那一格按人的半径膨胀之后本来就算挡着：改为断言活儿真的在那儿、离他一臂之内），出门那段路每 0.2 米都能站
  for (const r of world.residents.values()) {
    const role = RESIDENT_ROLES[r.kind], plot = layout.plots.get(role.site);
    if (r.station) {
      const reach = Math.hypot(r.station.at.x - r.home.x, r.station.at.z - r.home.z);
      assert.ok(reach > 0.3 && reach < 0.75, `${r.kind} 站在活儿跟前（${reach.toFixed(2)} 米）`);
      const facing = Math.atan2(r.station.at.x - r.home.x, r.station.at.z - r.home.z) - r.home.yaw;
      assert.ok(Math.abs(Math.atan2(Math.sin(facing), Math.cos(facing))) < 0.3, `${r.kind} 面朝他的活儿`);
      assert.ok(!W(layout, r.station.at.x, r.station.at.z), `${r.kind} 的活儿（铁砧、石料、画架）是实在的东西、挡路`);
      assert.equal(Math.hypot(r.out.x - r.home.x, r.out.z - r.home.z), 0, `${r.kind} 在工位上，不出门`);
    } else if (r.kind !== 'herald') {
      assert.ok(W(layout, r.home.x, r.home.z), `${r.kind} 的家门口能站`);
    }
    assert.ok(Math.hypot(r.home.x - (plot.cx + role.spot[0]), r.home.z - (plot.cz + role.spot[1])) <= 1.5, `${r.kind} 离设定的位置不超过 1.5 米`);
    const len = Math.hypot(r.out.x - r.home.x, r.out.z - r.home.z);
    for (let d = len > 0 ? 0.2 : 1; d <= len; d += 0.2) {
      const u = d / len;
      const p = { x: r.home.x + (r.out.x - r.home.x) * u, z: r.home.z + (r.out.z - r.home.z) * u };
      if (r.kind === 'herald' && d <= 0.8) continue;
      assert.ok(W(layout, p.x, p.z), `${r.kind} 出门的路在 ${d.toFixed(1)} 米处被挡`);
    }
  }
  const wanderers = [...world.residents.values()].filter((r) => !r.station && r.kind !== 'herald');
  assert.ok(wanderers.filter((r) => Math.hypot(r.out.x - r.home.x, r.out.z - r.home.z) > 2).length >= wanderers.length - 2, '没有工位的居民多数有一段能走的出门路');
  // findOuting：家门口被围住就待在家里
  const stay = findOuting({ x: 0, z: 0, yaw: 0 }, () => false);
  assert.deepEqual(stay, { x: 0, z: 0 });
});

/**
 * 一座城里每位居民一整圈的路线（家门口、出门的落脚点、中间每 0.1 米）：从头顶往下照竖线（脚下一根、周围 0.1 米一圈八根），
 * 第一个碰到的东西不能高过脚底 3 厘米（高了就是石头在他身体里）；脚下那一根也不能比脚底低 3 厘米以上（不悬空）。
 */
function assertResidentsClear(world, where) {
  world.root.updateMatrixWorld(true);
  const solids = [];
  const collect = (o) => {
    if (o.visible === false || o.name.startsWith('figure:')) return;
    if (o.isMesh) solids.push(o);
    for (const c of o.children) collect(c);
  };
  collect(world.root);
  const ray = new THREE.Raycaster();
  const down = new THREE.Vector3(0, -1, 0);
  const firstHit = (x, z, top) => {
    ray.set(new THREE.Vector3(x, top, z), down);
    return ray.intersectObjects(solids, false)[0]?.point.y ?? -Infinity;
  };
  for (const r of world.residents.values()) {
    const feet = r.home.y - 0.012;   // 根比脚下的地面高 0.012（world.settleResident）
    const top = feet + (r.figure.root.userData.height ?? 1.75);
    const len = Math.hypot(r.out.x - r.home.x, r.out.z - r.home.z);
    for (let d = 0; d <= len + 1e-9; d += 0.1) {
      const u = len > 0 ? d / len : 0;
      const x = r.home.x + (r.out.x - r.home.x) * u, z = r.home.z + (r.out.z - r.home.z) * u;
      const under = firstHit(x, z, top);
      assert.ok(Math.abs(under - feet) < 0.03, `${where}: ${r.kind} ${d.toFixed(1)} m out: his soles are at ${feet.toFixed(2)}, the ground under them at ${under.toFixed(2)}`);
      for (let k = 0; k < 8; k++) {
        const a = (k / 8) * Math.PI * 2;
        const h = firstHit(x + Math.cos(a) * 0.1, z + Math.sin(a) * 0.1, top);
        assert.ok(h < feet + 0.03, `${where}: ${r.kind} ${d.toFixed(1)} m out: something reaches ${(h - feet).toFixed(2)} m up into his body`);
      }
    }
  }
}

test('city collision: every resident stands on what is under his feet, and nothing passes through him anywhere on his round', () => {
  // M3 第四轮，用户：（戴宽檐帽、拿杖的那位）出门、回来都从讲台的侧面穿过去，非常明显的穿模。那是传令官：他站在 0.44 米高的讲台上，
  // 高度却按街面算（y = 0.012），整个人陷在台子里，出门那段路又从台面一路走到街上——占用栅格只管 0.35 米以上的人身高度带，
  // 讲台下一级、台阶这样的矮东西不挡路，平面上的检查（上一例）看不出来。同一把尺子还量出两处：图书管理员站在圆形图书馆
  // 最下一级台阶（0.25 米）的边上；神殿（全局对话）里的神庙放大到 0.95、台基伸到街上，侍女和女祭司整个人陷在 0.94 米高的台基里
  const { world } = buildCity();
  assertResidentsClear(world, 'city');
  // 传令官站在讲台的台面上，不出门
  const herald = world.residents.get('herald');
  assert.ok(Math.abs(herald.home.y - (0.44 + 0.012)) < 1e-6, `the herald stands on his bema (${herald.home.y.toFixed(3)})`);
  assert.equal(Math.hypot(herald.out.x - herald.home.x, herald.out.z - herald.home.z), 0, 'the herald stays on his bema');
  assert.ok(Math.abs(world.groundAt(-4.4, -1.4) - 0.22) < 1e-6, '讲台下一级的前沿是 0.22 米的台面：你踩上去也落在台面上');
  assertResidentsClear(buildCity({ sanctuary: true }).world, 'sanctuary');
});

test('city collision: holding a key against a house slides you along its wall, around its corner and never inside', () => {
  const { layout } = buildCity({ demo: false });
  // 从北边的街走向民居的后墙（门前有葡萄架的柱子和陶罐，后墙是一整面）
  const b = plotCenter('杂物');
  let pos = { x: b.x * BLOCK + 1.5, z: b.z * BLOCK - 9 };
  assert.ok(W(layout, pos.x, pos.z), '起点在街上');
  for (let i = 0; i < 300; i++) pos = slide(pos, { x: 0, z: 0.05 }, layout.walkable);
  assert.ok(W(layout, pos.x, pos.z), '停在墙外');
  assert.ok(pos.z < b.z * BLOCK - 2.5, `没穿进房子（z=${pos.z.toFixed(2)}）`);
  const before = pos.x;
  for (let i = 0; i < 400; i++) pos = slide(pos, { x: -0.04, z: 0.04 }, layout.walkable);
  assert.ok(pos.x < before - 4, `沿后墙滑出去了（x 从 ${before.toFixed(1)} 到 ${pos.x.toFixed(1)}）`);
  assert.ok(W(layout, pos.x, pos.z), '一路都在能走的地方');
  assert.ok(PLAYER_RADIUS >= 0.2, '人的半径至少 0.2 米：贴墙走不会把肩膀插进墙里');
  // 斜着撞上一根柱子（广场的方柱头像）：偏着滑过去，不卡住
  let p = { x: 4.6 + 1.0, z: -3.6 + 1.2 };
  assert.ok(W(layout, p.x, p.z));
  for (let i = 0; i < 80; i++) p = slide(p, { x: -0.03, z: -0.03 }, layout.walkable);
  assert.ok(Math.hypot(p.x - 5.6, p.z - (-2.4)) > 1.2, `绕过柱子继续走了（${p.x.toFixed(2)}, ${p.z.toFixed(2)}）`);
});

test('map: a finite rectangle that holds the whole city, a strip of sea, every walkable point and the boat; it grows with the city', async () => {
  // 2026-10-12，用户：地图是有限面积的矩形（随工作区规模自适应），包括一部分海洋，矩形外是纯色
  const { BOAT_MOORING } = await import('../../../../Assets/Polis/src/art/civic.mjs');
  const { world, layout } = buildCity();
  const r = layout.mapRect;
  assert.ok(r && r.maxX > r.minX && r.maxZ > r.minZ, '有一块矩形');
  assert.equal(world.mapRect, r);
  const inside = (x, z, eps = 1e-6) => x >= r.minX - eps && x <= r.maxX + eps && z >= r.minZ - eps && z <= r.maxZ + eps;
  // 地面与海正好铺满矩形：陆地到岸线，海从岸线到南边
  world.root.updateMatrixWorld(true);
  const boxOf = (o) => new THREE.Box3().setFromObject(o);
  const meshes = world.root.children.filter((o) => o.isMesh);
  const land = meshes.find((o) => o.material.name === 'earth');
  const sea = meshes.find((o) => o.material.name === 'water');
  const lb = boxOf(land), sb = boxOf(sea);
  for (const [b, name] of [[lb, '陆地'], [sb, '海']]) {
    assert.ok(Math.abs(b.min.x - r.minX) < 1e-3 && Math.abs(b.max.x - r.maxX) < 1e-3, `${name}东西两边贴着矩形`);
  }
  assert.ok(Math.abs(lb.min.z - r.minZ) < 1e-3 && Math.abs(lb.max.z - layout.shoreZ) < 1e-3, '陆地从北边到岸线');
  assert.ok(Math.abs(sb.min.z - layout.shoreZ) < 1e-3 && Math.abs(sb.max.z - r.maxZ) < 1e-3, '海从岸线到南边');
  assert.ok(r.maxZ - layout.shoreZ > 30, '矩形里带着一片海');
  // 城里每一样东西（建筑、树、公共建筑、场馆、人）都站在矩形里；底座与切面也不出矩形
  for (const o of world.root.children) {
    if (o === land || o === sea) continue;
    const p = o.getWorldPosition(new THREE.Vector3());
    assert.ok(inside(p.x, p.z), `${o.name || o.userData.asset || o.type} 在 (${p.x.toFixed(1)}, ${p.z.toFixed(1)})，出了地图`);
  }
  for (const entry of world.buildings.values()) {
    const b = boxOf(entry.group);
    assert.ok(inside(b.min.x, b.min.z) && inside(b.max.x, b.max.z), '建筑整座在地图里');
  }
  // 能走的地方都在矩形里（外面一圈 2 米一个点抽查），海岸的白沫矩形也不出界
  for (let x = r.minX - 20; x <= r.maxX + 20; x += 2) {
    for (const z of [r.minZ - 1, r.maxZ + 1]) assert.ok(!W(layout, x, z), `(${x}, ${z}) 在地图外，不能走`);
  }
  for (let z = r.minZ - 20; z <= r.maxZ + 20; z += 2) {
    for (const x of [r.minX - 1, r.maxX + 1]) assert.ok(!W(layout, x, z), `(${x}, ${z}) 在地图外，不能走`);
  }
  for (const [x0, z0, x1, z1] of world.waterShores) assert.ok(inside(x0, z0) && inside(x1, z1), '岸线在地图里');
  // 船出海最远（game.applyEffects：泊位往南 32 米、东西摆 2.5 米），连船身半长 3.6 米也还在海里
  const far = { x: world.boatHome.x + 2.5, z: world.boatHome.z + 32 + 3.6 };
  assert.ok(inside(far.x, far.z) && far.z < r.maxZ, '船出海最远的地方还在地图里');
  assert.ok(Math.abs(world.boatHome.z - (layout.quayZ + BOAT_MOORING[2])) < 1e-9);
  // 随工作区规模：多一圈文件夹，矩形四面都大一圈街区
  const base = createLayout(fixture), ring = base.ring;
  const bigger = createLayout({ ...fixture, buildings: [...fixture.buildings, { ...fixture.buildings[0], key: '新的一圈', plot: { x: ring + 1, z: -ring - 1 } }] });
  const R = bigger.mapRect;
  assert.equal(bigger.ring, ring + 1);
  const b0 = base.mapRect;
  assert.ok(R.maxX - R.minX >= b0.maxX - b0.minX + BLOCK * 2 - 1e-6, '东西宽了两个街区');
  assert.ok(R.minZ <= b0.minZ - BLOCK + 1e-6, '往北长了一个街区');
  assert.ok(Math.abs((R.maxZ - bigger.shoreZ) - (b0.maxZ - base.shoreZ)) < 1e-6, '海的那一条不变');
});

test('street lamps: a lamp post at every crossing that has a free corner, clear of everything, and the crossings are lit', async () => {
  // 2026-10-13，用户：外面的街道没有被打亮。门窗的光只照到门前（门离街边还有 3 米），街口靠街灯
  const { lampAt } = await import('../../../../Assets/Polis/src/art/lamps.mjs');
  const { STREET } = await import('../../../../Assets/Polis/src/layout.mjs');
  const { world, layout } = buildCity();
  assert.ok(world.lampPosts.length >= 6, `立了街灯（${world.lampPosts.length}）`);
  for (const { x, z } of world.lampPosts) {
    const X = (Math.round(x / BLOCK - 0.5) + 0.5) * BLOCK, Z = (Math.round(z / BLOCK - 0.5) + 0.5) * BLOCK;
    assert.ok(Math.abs(Math.abs(x - X) - (STREET / 2 + 0.45)) < 1e-6 && Math.abs(Math.abs(z - Z) - (STREET / 2 + 0.45)) < 1e-6, `(${x}, ${z}) 在街角，不占街面`);
    assert.ok(!layout.walkable({ x, z }), '柱子挡路');
    assert.ok(W(layout, X, Z), '街口照样能走');
    // 街灯在街口斜对角 4.5 米处，灯笼往街口伸出半米：街口中心约 0.17（没有灯的地方是 0）
    assert.ok(lampAt(world.lampField, X, Z) > 0.12, `街口 (${X}, ${Z}) 被照亮（${lampAt(world.lampField, X, Z).toFixed(2)}）`);
  }
  // 每处灯火都进了光照图：建筑的门窗、柱廊和门边的灯笼、街灯
  const lights = world.lights();
  for (const [key, b] of world.buildings) {
    if (b.building.state === 'ruin' || !['house', 'workshop', 'treasury', 'stoaLibrary'].includes(b.kind)) continue;
    const p = b.group.position;
    assert.ok(lights.some((l) => Math.hypot(l.x - p.x, l.z - p.z) < PLOT / 2 + 1), `${key}（${b.kind}）的灯火进了光照图`);
  }
  assert.ok(lights.every((l) => Number.isFinite(l.x) && Number.isFinite(l.z) && Math.hypot(l.nx, l.nz) < 1 + 1e-6), '光源都在世界坐标里');
});
