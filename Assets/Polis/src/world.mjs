// 把城邦文档（C# 组装的 PolisFixtureDocument：建筑、地块、空地、市集）和布局变成一座城：地面、海、街道、公共建筑、
// 按文件夹类型生成的建筑、树、人物。整座城挂在一个根节点下，换城时整体拆掉；同一座城里的建筑变化（文件增减、
// 新建或删除文件夹）按座替换，不重建整座城——外部改动只让建筑轻微变化（设计稿 11.2）。
//
// M2：每一件东西都经资产库（art/assets.mjs）按 Id 造出来——缺省程序化，清单可以把任何一件换成外部模型。
// 港前第 z = 2 行加了三处场馆（彩绘柱廊、书库、金库，每处 12 个底座）和橄榄林的台地；广场上加了日晷与传令官的讲台，
// 委托板上钉着建议的纸条；居民站在各自能力的建筑旁；设立了神谕，神庙前立起三足鼎。
import * as THREE from '../vendor/three.bundle.mjs';
import { groundPlane, Kit, m4, buildScaffold } from './art/kit.mjs';
import { rng, hashSeed } from './art/noise.mjs';
import { oliveGeometries, OLIVE_STAGES } from './art/plants.mjs';
import { buildSlotMarker } from './art/items.mjs';
import { RESIDENT_ROLES } from './art/characters.mjs';
import { BLOCK, STREET, PLOT } from './layout.mjs';
import { venueSlotLocal, groveSpot, VENUES, SLOTS_PER_VENUE } from './civic-layout.mjs';

/** 公共建筑的名字。实时模式里由 C# 按界面语言推来（locale 文件），这里是独立打开页面时的默认值。 */
export const PUBLIC_NAMES = Object.freeze({
  agora: '广场', temple: '神庙', library: '图书馆', forge: '锻炉', harbor: '港口', market: '市集', gate: '城门',
  sanctuary: '雅典娜的神殿', paintedStoa: '彩绘柱廊', archive: '书库', vault: '金库', grove: '橄榄林',
});

const shadowed = (o) => { o.castShadow = true; o.receiveShadow = true; return o; };

/**
 * @param {THREE.Scene} scene
 * @param {object} city 城邦文档（buildings / vacant / market / publicSites / seaStartsAtZ）
 * @param {object} layout createLayout(city)
 * @param {object} M 材质
 * @param {{ names?: object, sanctuary?: boolean, assets: object }} options assets 是资产库（createAssetLibrary）
 */
export function buildWorld(scene, city, layout, M, options) {
  const assets = options.assets;
  if (!assets) throw new Error('world: an asset library is required');
  const names = { ...PUBLIC_NAMES, ...(options.names ?? {}) };
  const root = new THREE.Group();
  root.name = 'polis-world';
  scene.add(root);
  const world = {
    root, layout, buildings: new Map(), vacant: new Map(), labels: [], maidens: [], scaffolds: new Map(), sanctuary: !!options.sanctuary,
    venues: new Map(), residents: new Map(), groveTrees: [], assets,
  };
  const add = (o) => { root.add(o); return o; };

  // —— 地面与海 ——
  const shoreZ = layout.quayZ + STREET / 2 + 0.2;
  const landDepth = 600;
  const land = new THREE.Mesh(groundPlane(600, landDepth, 15), M.earth);
  land.position.set(0, 0, shoreZ - landDepth / 2);
  land.receiveShadow = true;
  add(land);
  const sea = new THREE.Mesh(groundPlane(600, 400, 8), M.water);
  sea.position.set(0, -0.7, shoreZ + 200);
  sea.receiveShadow = true;
  add(sea);
  const quay = new Kit();
  quay.box(M.stone, 600, 1.2, 1.6, 0, -0.55, shoreZ + 0.6);
  add(quay.build());

  // —— 街道：横街、竖街铺石板，广场整块铺地；码头大街接长到港前的石台 ——
  const b = layout.bounds;
  const streets = new Kit();
  const iMin = Math.round(b.minX / BLOCK), iMax = Math.round(b.maxX / BLOCK);
  for (let j = Math.round(b.minZ / BLOCK); (j + 0.5) * BLOCK <= layout.quayZ + 0.01; j++) {
    const z = (j + 0.5) * BLOCK;
    const isQuay = Math.abs(z - layout.quayZ) < 0.01;
    const width = isQuay ? STREET + 1.5 : STREET;   // 码头那条宽一些
    const x0 = isQuay ? Math.min(b.minX, layout.quayRange.minX - BLOCK / 2) : b.minX;
    const x1 = isQuay ? Math.max(b.maxX, layout.quayRange.maxX + BLOCK / 2) : b.maxX;
    streets.add(M.street, groundPlane(x1 - x0, width, 4), m4((x1 + x0) / 2, 0.012, z));
  }
  for (let i = iMin; i < iMax; i++) {
    const x = (i + 0.5) * BLOCK;
    streets.add(M.street, groundPlane(STREET, layout.quayZ - b.minZ, 4), m4(x, 0.013, (layout.quayZ + b.minZ) / 2));
  }
  streets.add(M.pave, groundPlane(BLOCK - 0.2, BLOCK - 0.2, 4), m4(0, 0.016, 0));   // 广场
  const streetGroup = streets.build();
  streetGroup.traverse((o) => { o.castShadow = false; });
  add(streetGroup);

  const placeAt = (group, plot) => {
    group.position.set(plot.cx, 0, plot.cz);
    group.rotation.y = plot.yaw;
    return add(group);
  };
  const labelAt = (text, plot, height, kind, key = null) => {
    const label = { text, kind, key, anchor: new THREE.Vector3(plot.cx, height + 0.8, plot.cz) };
    world.labels.push(label);
    return label;
  };
  const pub = (name) => layout.plots.get(name);

  // —— 公共建筑 ——
  const temple = assets.object('civic.temple', { nx: 6, nz: 7 });
  // 神殿（全局对话）：没有文件夹的城，神庙就是主角，盖得更大
  const templeScale = world.sanctuary ? 0.95 : 0.62;
  temple.scale.setScalar(templeScale);
  placeAt(temple, pub('temple'));
  labelAt(world.sanctuary ? names.sanctuary : names.temple, pub('temple'), (temple.userData.height ?? 9) * templeScale, 'public', 'temple');
  world.temple = temple;
  // 神谕的三足鼎与两丛月桂：设立了每日神谕才出现（setOracle）
  const oracle = new THREE.Group();
  const tripod = assets.object('prop.tripod');
  tripod.position.set(0, 0, 0);
  oracle.add(tripod);
  for (const sx of [-1, 1]) {
    const laurel = assets.object('plant.laurel', { seed: 7 + sx });
    laurel.position.set(sx * 1.3, 0, -0.3);
    oracle.add(laurel);
  }
  oracle.position.set(pub('temple').cx, 0, pub('temple').cz + PLOT / 2 - 0.4);
  oracle.visible = false;
  add(oracle);
  world.oracle = oracle;

  const tholos = placeAt(assets.object('civic.tholos'), pub('library'));
  labelAt(names.library, pub('library'), tholos.userData.height, 'public', 'library');
  const forge = placeAt(assets.object('civic.forge'), pub('forge'));
  labelAt(names.forge, pub('forge'), forge.userData.height, 'public', 'forge');
  world.forge = forge;
  const marketKeys = city.market ?? [];
  const market = placeAt(assets.object('civic.market', { stalls: marketKeys.length }), pub('market'));
  labelAt(names.market, pub('market'), market.userData.height, 'public', 'market');
  marketKeys.forEach((key, k) => {
    const spots = market.userData.stallSpots ?? [[0, 0]];
    const spot = spots[k % spots.length];
    const local = new THREE.Vector3(spot[0], 3.0, spot[1]).applyAxisAngle(new THREE.Vector3(0, 1, 0), pub('market').yaw);
    world.labels.push({ text: key, kind: 'stall', key: `stall:${key}`, anchor: new THREE.Vector3(pub('market').cx + local.x, local.y, pub('market').cz + local.z) });
  });
  const agora = placeAt(assets.object('civic.agora'), pub('agora'));
  agora.rotation.y = 0;
  labelAt(names.agora, { cx: 0, cz: -4.2 }, 3.4, 'public', 'agora');
  world.agora = agora;

  const harbor = assets.object('civic.harbor', { quayLocalZ: layout.quayZ, pierLength: 14, plotCenterZ: pub('harbor').cz });
  add(harbor);
  world.labels.push({ text: names.harbor, kind: 'public', key: 'harbor', anchor: new THREE.Vector3(0, 2.6, layout.quayZ + 6) });
  const boat = assets.object('civic.boat');
  world.boatHome = new THREE.Vector3(3.8, -0.05, layout.quayZ + 9.5);
  boat.position.copy(world.boatHome);
  add(boat);
  world.boat = boat;

  const gate = assets.object('civic.gate');
  gate.position.set(layout.gate.x, 0, layout.gate.z - 1.5);
  add(gate);
  world.labels.push({ text: names.gate, kind: 'public', key: 'gate', anchor: new THREE.Vector3(layout.gate.x, (gate.userData.height ?? 7) + 0.6, layout.gate.z - 1.5) });

  // —— M2：港前的三处场馆 ——
  const venueBuilders = { paintedStoa: 'civic.paintedStoa', archive: 'civic.archive', vault: 'civic.vault' };
  const venueDecor = {
    paintedStoa: [['prop.easel', -5.4, 1.2, 0.4], ['prop.easel', 5.4, 1.2, -0.4]],
    archive: [['prop.scrollCase', -5.5, 1.1, 0], ['prop.scrollCase', 5.5, 1.1, 0], ['prop.lekythos', -5.2, 5.6, 0]],
    vault: [['prop.chest', -5.3, 1.2, 0.2], ['prop.chest', 5.3, 1.2, -0.2]],
  };
  for (const [venue, site] of Object.entries(VENUES)) {
    const plot = pub(site);
    if (!plot) continue;
    const group = new THREE.Group();
    group.add(assets.object('civic.terrace'));
    const building = assets.object(venueBuilders[venue]);
    group.add(building);
    for (const [id, x, z, yaw] of venueDecor[venue] ?? []) {
      const o = assets.object(id);
      o.position.set(x, 0, z);
      o.rotation.y = yaw;
      group.add(o);
    }
    placeAt(group, plot);
    const slots = [];
    for (let i = 0; i < SLOTS_PER_VENUE; i++) {
      const local = venueSlotLocal(i);
      const pedestal = assets.object('prop.pedestal');
      pedestal.position.set(local.x, 0, local.z);
      group.add(pedestal);
      const marker = buildSlotMarker();
      marker.position.set(local.x, 0.86, local.z);
      marker.visible = false;
      group.add(marker);
      slots.push({ index: i, local, world: new THREE.Vector3(plot.cx + local.x, 0.8, plot.cz + local.z), pedestal, marker, item: null, display: null });
    }
    const label = labelAt(names[venue], plot, building.userData.height ?? 5, 'public', venue);
    label.anchor.z = plot.cz - 2.5;
    world.venues.set(venue, { venue, site, plot, group, building, slots, label });
  }

  // —— M2：橄榄林的台地（树由 setGrove 按委托种） ——
  const groveRoot = new THREE.Group();
  groveRoot.name = 'grove';
  add(groveRoot);
  const groveTerraces = [];
  for (const [key, plot] of layout.plots) {
    if (plot.grove == null) continue;
    const terrace = assets.object('civic.terrace', { surface: 'earth' });
    placeAt(terrace, plot);
    groveTerraces.push({ key, plot });
    if (plot.grove === 0) {
      const marker = assets.object('civic.groveMarker');
      marker.position.set(plot.cx + PLOT / 2 - 1.0, 0, plot.cz - PLOT / 2 + 1.0);
      add(marker);
      world.labels.push({ text: names.grove, kind: 'public', key: 'grove', anchor: new THREE.Vector3(plot.cx, 3.6, plot.cz - 3.5) });
    }
  }
  world.groveTerraces = groveTerraces;

  // —— 文件夹的建筑：一座一座放，之后可以按座替换 ——
  function placeBuilding(bld) {
    const plot = layout.plots.get(`b:${bld.key}`);
    if (!plot) return null;
    const r = rng(hashSeed(bld.key));
    const group = placeAt(assets.object(`building.${bld.kind}`, { building: bld, rand: r }), plot);
    for (const [tx, tz] of group.userData.trees ?? []) {
      const tree = assets.object('plant.cypress', { height: 5 + r() * 2, seed: hashSeed(bld.key) % 97 });
      tree.position.set(tx, 0, tz);
      group.add(tree);
    }
    const height = group.userData.height ?? 5;
    const scaffold = buildScaffold(M, 6.5, Math.min(5.5, height));
    scaffold.position.set(0, 0, PLOT / 2 - 1.2);
    scaffold.visible = false;
    group.add(scaffold);
    world.scaffolds.set(`b:${bld.key}`, scaffold);
    const label = labelAt(bld.key, plot, height, 'building', `b:${bld.key}`);
    const entry = { group, height, kind: bld.kind, building: bld, label };
    world.buildings.set(`b:${bld.key}`, entry);
    return entry;
  }

  // 被删掉的文件夹留下的空地：一圈残存的地基石
  function placeVacant(v) {
    const kit = new Kit();
    const r = rng(hashSeed(v.key));
    for (let i = 0; i < 9; i++) {
      const a = (i / 9) * Math.PI * 2;
      kit.box(M.stone, 1.2 + r(), 0.4 + r() * 0.3, 0.7, Math.cos(a) * 4, 0.2, Math.sin(a) * 3.5, a);
    }
    const g = kit.build();
    g.position.set(v.plot.x * BLOCK, 0, v.plot.z * BLOCK);
    add(g);
    world.vacant.set(v.key, g);
  }

  function disposeGroup(group) {
    root.remove(group);
    group.traverse((o) => { if (o.isMesh && o.geometry && !o.geometry.userData?.shared) o.geometry.dispose(); });
  }

  for (const bld of city.buildings ?? []) placeBuilding(bld);
  for (const v of city.vacant ?? []) placeVacant(v);

  // —— 树：没盖房子的地块种橄榄，神庙和城门边种柏树 ——
  const occupied = new Set([...layout.plots.values()].map((p) => `${p.x},${p.z}`));
  for (const v of city.vacant ?? []) occupied.add(`${v.plot.x},${v.plot.z}`);
  const tr = rng(20261011);
  for (let x = -layout.ring - 1; x <= layout.ring + 1; x++) {
    for (let z = -layout.ring - 1; z <= layout.seaZ - 1; z++) {
      if (occupied.has(`${x},${z}`)) continue;
      const n = 1 + Math.floor(tr() * 3);
      for (let k = 0; k < n; k++) {
        const tree = tr() < 0.75
          ? assets.object('plant.olive', { stage: tr() < 0.6 ? 'grown' : 'young', seed: 31 + k + x * 7 + z * 13 })
          : assets.object('plant.cypress', { height: 6 + tr() * 3, seed: 3 + k });
        tree.position.set(x * BLOCK + (tr() - 0.5) * PLOT * 0.7, 0, z * BLOCK + (tr() - 0.5) * PLOT * 0.7);
        tree.rotation.y = tr() * 6;
        add(tree);
      }
    }
  }
  const tp = pub('temple');
  for (const sx of [-1, 1]) {
    const c = assets.object('plant.cypress', { height: 8, seed: 5 + sx });
    c.position.set(tp.cx + sx * 7.4, 0, tp.cz - 3);
    add(c);
  }

  // —— 人物 ——
  const athena = assets.figure('figure.athena');
  add(athena.root);
  world.athenaFigure = athena;
  world.athena = athena.root;
  const player = assets.figure('figure.player');
  add(player.root);
  world.playerFigure = player;
  world.player = player.root;
  for (let k = 0; k < 6; k++) {
    const maiden = assets.figure('figure.maiden');
    maiden.root.visible = false;
    maiden.root.scale.setScalar(0.9);
    add(maiden.root);
    world.maidens.push(maiden);
  }

  /** 居民：每位站在他那项能力的建筑旁（characters.RESIDENT_ROLES），按 C# 推导出来的名单出现。 */
  world.setResidents = (list, nameOf = (kind) => kind) => {
    const want = new Map((list ?? []).map((r) => [r.kind, r]));
    for (const [kind, resident] of world.residents) {
      if (want.has(kind)) continue;
      root.remove(resident.figure.root);
      world.labels.splice(world.labels.indexOf(resident.label), 1);
      world.residents.delete(kind);
    }
    for (const [kind, info] of want) {
      const role = RESIDENT_ROLES[kind];
      const plot = role ? pub(role.site) : null;
      if (!role || !plot) continue;
      let resident = world.residents.get(kind);
      if (!resident) {
        const figure = assets.figure(`figure.${kind}`);
        const x = plot.cx + role.spot[0], z = plot.cz + role.spot[1];
        figure.root.position.set(x, 0.012, z);
        figure.root.rotation.y = role.facing;
        add(figure.root);
        const label = { text: nameOf(kind), kind: 'resident', key: `resident:${kind}`, anchor: new THREE.Vector3(x, (figure.root.userData.height ?? 1.75) + 0.55, z) };
        world.labels.push(label);
        resident = { kind, figure, label, home: { x, z, yaw: role.facing }, work: role.work, phase: hashSeed(kind) % 9000, met: false };
        world.residents.set(kind, resident);
      }
      resident.met = !!info.met;
      resident.label.text = nameOf(kind);
      resident.label.unmet = !info.met;
    }
  };

  /**
   * 橄榄林：每份完成的委托一棵树（C# 从会话推导），按树龄（四档）实例化——几百棵也只有八个绘制调用。
   * 第 n 棵树的位置只由 n 决定（civic-layout.groveSpot）。返回每棵树的树梢位置，给点击与名牌用。
   */
  const groveMeshes = [];
  world.setGrove = (trees) => {
    for (const m of groveMeshes) { groveRoot.remove(m); m.geometry.dispose(); }
    groveMeshes.length = 0;
    world.groveTrees = [];
    const origin = layout.groveOrigin;
    if (!origin) return world.groveTrees;
    const byStage = new Map(OLIVE_STAGES.map((s) => [s, []]));
    for (const tree of trees ?? []) {
      const spot = groveSpot(tree.index, origin);
      const terrace = layout.plots.get(`grove:${spot.terrace}`);
      if (!terrace) continue;   // 林子长出了这座城画出来的台地（下次换城时补上）
      const x = terrace.cx + spot.local.x, z = terrace.cz + spot.local.z;
      const stage = OLIVE_STAGES[tree.stage] ?? tree.stage ?? 'grown';
      byStage.get(stage)?.push({ x, z, yaw: (hashSeed(tree.conversationId) % 628) / 100 });
      const height = { sapling: 1.3, young: 2.3, grown: 3.2, ancient: 3.7 }[stage] ?? 3;
      world.groveTrees.push({ ...tree, x, z, top: new THREE.Vector3(x, height + 0.4, z), base: new THREE.Vector3(x, 0.6, z) });
    }
    for (const [stage, spots] of byStage) {
      if (spots.length === 0) continue;
      const variants = [1, 2, 3].map((seed) => oliveGeometries(stage, seed + OLIVE_STAGES.indexOf(stage) * 10));
      for (const [k, geo] of variants.entries()) {
        const mine = spots.filter((_, i) => i % variants.length === k);
        if (mine.length === 0) continue;
        for (const [part, mat] of [[geo.bark, M.bark], [geo.leaves, M.olive]]) {
          const inst = shadowed(new THREE.InstancedMesh(part, mat, mine.length));
          mine.forEach((s, i) => inst.setMatrixAt(i, m4(s.x, 0, s.z, s.yaw)));
          inst.instanceMatrix.needsUpdate = true;
          inst.computeBoundingSphere();
          groveRoot.add(inst);
          groveMeshes.push(inst);
        }
      }
    }
    return world.groveTrees;
  };

  /** 委托板上钉几张纸条（有几条建议钉几张）。 */
  world.setBoard = (count) => {
    (agora.userData.notices ?? []).forEach((sheet, i) => { sheet.visible = i < count; });
  };

  /** 设立了每日神谕：神庙前立起三足鼎与月桂。 */
  world.setOracle = (on) => { oracle.visible = !!on; };

  /** 摆放时空位上的白环：只给空着的位次（filter(venue, slot) 为 true 的那些）。 */
  world.showSlotMarkers = (on, filter = () => true) => {
    for (const v of world.venues.values()) for (const s of v.slots) s.marker.visible = !!on && !s.item && filter(v.venue, s.index);
  };

  /**
   * 换掉一座建筑（文件增减让类型、体量、状态变了）：拆掉旧的那一座，按同一块地重新盖。
   * 地块不在这座城的布局里（新文件夹占了更外一圈）时返回 false，调用方整城重建。
   */
  world.replaceBuilding = (bld) => {
    const id = `b:${bld.key}`;
    const old = world.buildings.get(id);
    if (old) {
      disposeGroup(old.group);
      world.labels.splice(world.labels.indexOf(old.label), 1);
      world.buildings.delete(id);
      world.scaffolds.delete(id);
    }
    const vacant = world.vacant.get(bld.key);
    if (vacant) {
      disposeGroup(vacant);
      world.vacant.delete(bld.key);
    }
    if (!layout.plots.has(id)) return false;
    layout.plots.get(id).building = bld;
    return placeBuilding(bld) != null;
  };

  /** 文件夹被删：拆掉建筑，留下一圈地基石（空地记在账本里，永不回收）。 */
  world.vacate = (v) => {
    const id = `b:${v.key}`;
    const old = world.buildings.get(id);
    if (old) {
      disposeGroup(old.group);
      world.labels.splice(world.labels.indexOf(old.label), 1);
      world.buildings.delete(id);
      world.scaffolds.delete(id);
    }
    if (!world.vacant.has(v.key)) placeVacant(v);
  };

  /** 整座城拆掉（换城、或者城长出了新的一圈）。共享的材质留着，几何体（包括这座城共用的那几份）全部释放。 */
  world.dispose = () => {
    scene.remove(root);
    const seen = new Set();
    root.traverse((o) => {
      if (!o.isMesh || !o.geometry || seen.has(o.geometry) || o.geometry.userData?.shared) return;
      seen.add(o.geometry);
      o.geometry.dispose();
    });
  };

  return world;
}
