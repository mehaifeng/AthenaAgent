// 把城邦文档（C# 组装的 PolisFixtureDocument：建筑、地块、空地、市集）和布局变成一座城：地面、海、街道、公共建筑、
// 按文件夹类型生成的建筑、树、人物。整座城挂在一个根节点下，换城时整体拆掉；同一座城里的建筑变化（文件增减、
// 新建或删除文件夹）按座替换，不重建整座城——外部改动只让建筑轻微变化（设计稿 11.2）。
//
// M2：每一件东西都经资产库（art/assets.mjs）按 Id 造出来——缺省程序化，清单可以把任何一件换成外部模型。
// M3：挡路的不再是整块地块，而是每件东西自己的足迹（footprint.mjs）：建好之后把每件可见资产的足迹按摆放矩阵盖到
// 一张全城的占用栅格上（rebuildOccupancy），layout.walkable 查它；城里有东西增减（换建筑、空地、橄榄林、神谕）就重盖一遍。
// 港前第 z = 2 行加了三处场馆（彩绘柱廊、书库、金库，每处 12 个底座）和橄榄林的台地；广场上加了日晷与传令官的讲台，
// 委托板上钉着建议的纸条；居民站在各自能力的建筑旁；设立了神谕，神庙前立起三足鼎。
import * as THREE from '../vendor/three.bundle.mjs';
import { groundPlane, Kit, m4 } from './art/kit.mjs';
import { rng, hashSeed } from './art/noise.mjs';
import { oliveGeometries, OLIVE_STAGES } from './art/plants.mjs';
import { buildSlotMarker } from './art/items.mjs';
import { RESIDENT_ROLES, buildCitizen } from './art/characters.mjs';
import { citizenLook } from './art/citizens.mjs';
import { BOAT_MOORING, BOAT_CLEAT } from './art/civic.mjs';
import { BLOCK, STREET, PLOT } from './layout.mjs';
import { venueSlotLocal, groveSpot, VENUES, SLOTS_PER_VENUE } from './civic-layout.mjs';
import { createOccupancy, footprintOf, footprintOfGeometry, BLOCKING_HEIGHT } from './footprint.mjs';

/** 公共建筑的名字。实时模式里由 C# 按界面语言推来（locale 文件），这里是独立打开页面时的默认值。 */
export const PUBLIC_NAMES = Object.freeze({
  agora: '广场', temple: '神庙', library: '图书馆', forge: '锻炉', harbor: '港口', market: '市集', gate: '城门',
  sanctuary: '雅典娜的神殿', paintedStoa: '彩绘柱廊', archive: '书库', vault: '金库', grove: '橄榄林',
});

/** 地图边缘切下去的底座有多深（米）。 */
export const PLINTH_DEPTH = 2.4;

const shadowed = (o) => { o.castShadow = true; o.receiveShadow = true; return o; };

/** 神庙的缩放：普通城邦里 0.62；神殿（全局对话，没有文件夹的城）里神庙是主角，0.95。居民站的地方按 city 的那一档给。 */
export const TEMPLE_SCALE = Object.freeze({ city: 0.62, sanctuary: 0.95 });

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
    root, layout, buildings: new Map(), vacant: new Map(), labels: [], sanctuary: !!options.sanctuary,
    venues: new Map(), residents: new Map(), groveTrees: [], assets,
  };
  const add = (o) => { root.add(o); return o; };

  // —— 占用栅格（M3 决定 1）：城的范围再往外留一圈（城门在最北一条街之外、码头大街向西接长到橄榄林） ——
  const ob = layout.bounds, qr = layout.quayRange;
  const occupancy = createOccupancy({
    minX: Math.min(ob.minX, qr.minX - BLOCK / 2) - 12, maxX: Math.max(ob.maxX, qr.maxX + BLOCK / 2) + 12,
    minZ: ob.minZ - 12, maxZ: ob.maxZ + 12,
  });
  world.occupancy = occupancy;

  /**
   * 把每件可见资产的足迹重新盖到占用栅格上。资产的足迹按它自己的网格算一次（assets.footprint）；嵌在别的资产里的资产
   * （民居旁的柏树、场馆台地上的器物与底座）各自盖；空地的地基石与橄榄林的实例化树干另记在 userData 上。
   * 隐藏的东西（没设立的神谕、停工的脚手架、空位的白环、消散的侍女）不算。
   */
  world.rebuildOccupancy = () => {
    root.updateMatrixWorld(true);
    occupancy.clear();
    const inst = new THREE.Matrix4(), m = new THREE.Matrix4();
    const walk = (o) => {
      if (o.visible === false) return;
      if (o.userData.asset) occupancy.stamp(assets.footprint(o), o.matrixWorld);
      else if (o.userData.footprint) occupancy.stamp(o.userData.footprint, o.matrixWorld);
      if (o.userData.instancedFootprint && o.isInstancedMesh) {
        for (let k = 0; k < o.count; k++) {
          o.getMatrixAt(k, inst);
          occupancy.stamp(o.userData.instancedFootprint, m.multiplyMatrices(o.matrixWorld, inst));
        }
      }
      for (const child of o.children) walk(child);
    };
    walk(root);
    layout.setObstacles(obstacles);
    // 城里的东西变了，居民站的地方和出门的那段路也要重新挑（神谕的月桂可能正落在侍女出门的路上）
    for (const resident of world.residents.values()) settleResident(resident);
    return occupancy.stamped;
  };
  const obstacles = (p) => occupancy.blocked(p.x, p.z);

  /**
   * 居民站在哪：设定点（他那项能力的建筑旁）附近最近的能站的地方——器物挡路之后，设定点可能正压在货堆或铁砧的边上；
   * 站在讲台上的传令官例外（role.fixed）。出门走一小段的落脚点也在这里挑（findOuting：一路上没有东西挡着）。
   */
  function settleResident(resident) {
    const { role, spot } = resident;
    // 有工位的（铁匠的铁砧、工匠的石料、画师的画架）：站在工位上、面朝活儿，不出门闲逛——手上的活儿是按这个站位解出来的
    const station = stationFor(role);
    if (station) {
      resident.figure.root.position.set(station.stand.x, station.stand.y + 0.012, station.stand.z);
      resident.label.anchor.set(station.stand.x, station.stand.y + (resident.figure.root.userData.height ?? 1.75) + 0.55, station.stand.z);
      resident.home = { x: station.stand.x, z: station.stand.z, y: station.stand.y + 0.012, yaw: station.yaw };
      resident.out = { x: station.stand.x, z: station.stand.z };
      resident.station = station;
      return;
    }
    const at = role.fixed ? { x: spot.x, z: spot.z } : nearestWalkable(spot, layout.walkable);
    const y = world.groundAt(at.x, at.z) + 0.012;
    resident.figure.root.position.set(at.x, y, at.z);
    resident.label.anchor.x = at.x;
    resident.label.anchor.z = at.z;
    resident.home = { x: at.x, z: at.z, y, yaw: role.facing };
    // 站在讲台上的传令官不出门：走路的人一路按家门口的高度摆（不会上下台阶），他从 0.44 米的台面走下去，就是一路穿过讲台
    // （M3 第四轮，用户实测）。别的居民挑一条平地上、没东西挡着的路
    resident.out = role.fixed ? { x: at.x, z: at.z } : findOuting(resident.home, layout.walkable, 2.4, world.groundAt);
  }

  /**
   * 工位（RESIDENT_ROLES[kind].station = { in: 'forge' | 场馆名, key }）：宿主（锻炉、场馆的那一组）userData.stations 里的一项，
   * 按宿主的摆放矩阵换到世界里——stand 是人站的地方（含地坪的高度），at 是活儿落下的那一点，yaw 是他面朝的方向。
   */
  function stationFor(role) {
    if (!role?.station) return null;
    const host = role.station.in === 'forge' ? world.forge : role.station.in === 'harbor' ? world.harbor : world.venues.get(role.station.in)?.group;
    const st = host?.userData.stations?.[role.station.key];
    if (!st) return null;
    host.updateMatrixWorld(true);
    return { stand: st.stand.clone().applyMatrix4(host.matrixWorld), at: st.at.clone().applyMatrix4(host.matrixWorld), yaw: (st.yaw ?? 0) + host.rotation.y, rope: st.rope ?? null };
  }
  world.stationFor = stationFor;

  /**
   * 脚下的地面有多高：锻炉的地坪比街面高 0.2 米（userData.floors，宿主的局部坐标里的矩形）；别处都是 0。
   * 你、雅典娜、居民走上去，脚落在地坪上（第一版人一律 y = 0，铁匠和工匠的脚陷在地坪里 19 厘米）。
   */
  const floors = [];
  world.addFloors = (host) => {
    host.updateMatrixWorld(true);
    for (const f of host.userData.floors ?? []) {
      const box = new THREE.Box3();
      for (const x of [f.minX, f.maxX]) for (const z of [f.minZ, f.maxZ]) box.expandByPoint(new THREE.Vector3(x, 0, z).applyMatrix4(host.matrixWorld));
      floors.push({ minX: box.min.x, maxX: box.max.x, minZ: box.min.z, maxZ: box.max.z, y: f.y });
    }
  };
  world.groundAt = (x, z) => {
    let y = 0;
    for (const f of floors) if (x >= f.minX && x <= f.maxX && z >= f.minZ && z <= f.maxZ && f.y > y) y = f.y;
    return y;
  };

  /** 布局重建了（橄榄林长出新台地）：新布局接上同一张栅格。 */
  world.setLayout = (next) => {
    layout = next;
    world.layout = next;
    layout.setObstacles(obstacles);
  };

  // —— 地面与海：整张地图是一块有限的矩形（layout.mapRect，随城的圈数变大），南边带一片海；矩形外是跟着主题的纯色
  // （render.mjs 的 uFill）。边缘切下去一层底座，陆地下是土、海下是深蓝，读起来是一块沙盘 ——
  const shoreZ = layout.shoreZ;   // 与键盘走路的边界（layout.walkable）是同一条岸线
  const map = layout.mapRect;
  const mapW = map.maxX - map.minX, mapCX = (map.minX + map.maxX) / 2;
  const landD = shoreZ - map.minZ, seaD = map.maxZ - shoreZ;
  world.mapRect = map;
  const land = new THREE.Mesh(groundPlane(mapW, landD, 15), M.earth);
  land.position.set(mapCX, 0, map.minZ + landD / 2);
  land.receiveShadow = true;
  add(land);
  // 海面细分过（约 1.7 米一格）：顶点上叠着涌浪（art/water.mjs）。法线在片元里按同一组波算，格子粗一点看不出来
  const sea = new THREE.Mesh(new THREE.PlaneGeometry(mapW, seaD, Math.ceil(mapW / 1.7), Math.ceil(seaD / 1.7)).rotateX(-Math.PI / 2), M.water);
  sea.position.set(mapCX, -0.7, shoreZ + seaD / 2);
  sea.receiveShadow = true;
  add(sea);
  const quay = new Kit();
  quay.box(M.stone, mapW, 1.2, 1.6, mapCX, -0.55, shoreZ + 0.6);
  add(quay.build());
  const plinth = new Kit();
  const base = PLINTH_DEPTH;
  plinth.box(M.plinthEarth, mapW, base, landD, mapCX, -0.02 - base / 2, map.minZ + landD / 2);
  // 海底那一块的顶面在涌浪的波谷之下；三面的边沿高出它，一直到平均水面略下，侧面看不到水和底座之间的缝
  plinth.box(M.plinthSea, mapW, base - 0.8, seaD, mapCX, -0.82 - (base - 0.8) / 2, shoreZ + seaD / 2);
  const rim = base - 0.68, rimY = -0.68 - rim / 2, t = 0.06;
  plinth.box(M.plinthSea, t, rim, seaD, map.minX + t / 2, rimY, shoreZ + seaD / 2);
  plinth.box(M.plinthSea, t, rim, seaD, map.maxX - t / 2, rimY, shoreZ + seaD / 2);
  plinth.box(M.plinthSea, mapW, rim, t, mapCX, rimY, map.maxZ - t / 2);
  const plinthGroup = plinth.build();
  plinthGroup.traverse((o) => { o.castShadow = false; });
  add(plinthGroup);
  const insideMap = (x, z, pad = 0) => x >= map.minX + pad && x <= map.maxX - pad && z >= map.minZ + pad && z <= map.maxZ - pad;

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
  const templeScale = world.sanctuary ? TEMPLE_SCALE.sanctuary : TEMPLE_SCALE.city;
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
  world.addFloors(forge);
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
  world.addFloors(agora);   // 传令官的讲台

  const harbor = assets.object('civic.harbor', { quayLocalZ: layout.quayZ, pierLength: 14, plotCenterZ: pub('harbor').cz });
  add(harbor);
  world.harbor = harbor;
  world.addFloors(harbor);   // 栈桥的桥面
  world.labels.push({ text: names.harbor, kind: 'public', key: 'harbor', anchor: new THREE.Vector3(0, 2.6, layout.quayZ + 6) });
  const boat = assets.object('civic.boat');
  world.boatHome = new THREE.Vector3(BOAT_MOORING[0], BOAT_MOORING[1], layout.quayZ + BOAT_MOORING[2]);
  boat.position.copy(world.boatHome);
  add(boat);
  world.boat = boat;
  // 船上拴缆的那一点（世界坐标，跟着船起伏、出海）与船是不是泊在家里：水手只在船泊着的时候收缆
  const cleat = new THREE.Vector3(...BOAT_CLEAT);
  world.boatCleat = (out) => { boat.updateMatrixWorld(); return out.copy(cleat).applyMatrix4(boat.matrixWorld); };
  world.boatMoored = () => Math.hypot(boat.position.x - world.boatHome.x, boat.position.z - world.boatHome.z) < 0.3;

  const gate = assets.object('civic.gate');
  gate.position.set(layout.gate.x, 0, layout.gate.z - 1.5);
  add(gate);
  world.labels.push({ text: names.gate, kind: 'public', key: 'gate', anchor: new THREE.Vector3(layout.gate.x, (gate.userData.height ?? 7) + 0.6, layout.gate.z - 1.5) });

  // —— M2：港前的三处场馆 ——
  const venueBuilders = { paintedStoa: 'civic.paintedStoa', archive: 'civic.archive', vault: 'civic.vault' };
  // 场馆门前的陈设贴着台基的正面摆（M3：器物挡路了，第一版摆在 ±5.4 正好压在两侧的步道上）
  const venueDecor = {
    paintedStoa: [['prop.easel', -3.4, -0.3, 0.4], ['prop.easel', 3.4, -0.3, -0.4]],
    archive: [['prop.scrollCase', -3.4, -0.1, 0], ['prop.scrollCase', 3.4, -0.1, 0], ['prop.lekythos', -5.7, 5.9, 0]],
    vault: [['prop.chest', -3.2, 0.2, 0.2], ['prop.chest', 3.2, 0.2, -0.2]],
  };
  const seaTerraces = [];   // 伸进海里的台地：海面的白沫绕着它们（world.waterShores）
  for (const [venue, site] of Object.entries(VENUES)) {
    const plot = pub(site);
    if (!plot) continue;
    const group = new THREE.Group();
    const terraceObj = assets.object('civic.terrace');
    group.add(terraceObj);
    seaTerraces.push(terraceObj);
    const building = assets.object(venueBuilders[venue]);
    group.add(building);
    for (const [id, x, z, yaw] of venueDecor[venue] ?? []) {
      const o = assets.object(id);
      o.position.set(x, 0, z);
      o.rotation.y = yaw;
      group.add(o);
      // 画师的工位：西边那架画架前。她面朝画面，笔尖落在画面上——poses.ARMS 是用她自己的骨架解的"身前 0.53 米、偏右 0.08 米"
      if (venue === 'paintedStoa' && id === 'prop.easel' && x < 0 && o.userData.panel) {
        const c = o.userData.panel.centre;
        const at = new THREE.Vector3(c[0], c[1], c[2]).applyAxisAngle(new THREE.Vector3(0, 1, 0), yaw).add(o.position);
        const face = yaw + Math.PI;   // 面朝画架（画面朝 +z 转过 yaw）
        const fwd = new THREE.Vector3(Math.sin(face), 0, Math.cos(face)), right = new THREE.Vector3(-Math.cos(face), 0, Math.sin(face));
        const stand = at.clone().addScaledVector(fwd, -0.53).addScaledVector(right, -0.08);
        stand.y = 0;
        group.userData.stations = { paint: { at, stand, yaw: face } };
      }
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
    groveTerraces.push({ key, plot, object: terrace });
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
    g.userData.footprint = footprintOf(g);   // 地基石齐膝高，挡路（M3）
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
        if (!insideMap(tree.position.x, tree.position.z, 1.5)) continue;   // 树干落在地图边上就不种（树冠探出切面像是悬空）
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
  // 派发子代理时飞出去的六位黄金侍女（M1–M3）随直播一起拿掉了（2026-10-13）：她们只为镜像那一次工具调用而建、平时藏着。
  // 黄金侍女这位居民还在（characters.RESIDENT_ROLES.maiden）

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
        // 站在神庙前的（侍女、女祭司）按神庙真正的正面站：神殿里的神庙放大到 0.95，台基一直伸到街上，
        // 按普通城邦的米数站，两个人整个陷在 0.94 米高的台基里（M3 第四轮，按人站的地方往下照出来的）
        const front = role.site === 'temple' ? (temple.userData.front ?? 0) * (templeScale - TEMPLE_SCALE.city) : 0;
        const x = plot.cx + role.spot[0], z = plot.cz + role.spot[1] + front;
        figure.root.position.set(x, 0.012, z);
        figure.root.rotation.y = role.facing;
        add(figure.root);
        const label = { text: nameOf(kind), kind: 'resident', key: `resident:${kind}`, anchor: new THREE.Vector3(x, (figure.root.userData.height ?? 1.75) + 0.55, z) };
        world.labels.push(label);
        resident = { kind, figure, label, role, spot: { x, z }, home: { x, z, yaw: role.facing }, out: { x, z }, work: role.work, phase: hashSeed(kind) % 9000, met: false };
        settleResident(resident);
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
          // 树干挡路、树冠不挡（人身高度带）：足迹按这一份几何算一次，每棵按实例矩阵盖（M3）
          if (part === geo.bark) inst.userData.instancedFootprint = footprintOfGeometry(part, { minHeight: BLOCKING_HEIGHT });
          groveRoot.add(inst);
          groveMeshes.push(inst);
        }
      }
    }
    world.rebuildOccupancy();
    return world.groveTrees;
  };

  /** 委托板上钉几张纸条（有几条建议钉几张）。 */
  world.setBoard = (count) => {
    (agora.userData.notices ?? []).forEach((sheet, i) => { sheet.visible = i < count; });
  };

  /** 设立了每日神谕：神庙前立起三足鼎与月桂（它们从此挡路）。 */
  world.setOracle = (on) => {
    const next = !!on;
    if (oracle.visible === next) return;
    oracle.visible = next;
    world.rebuildOccupancy();
  };

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
    }
    const vacant = world.vacant.get(bld.key);
    if (vacant) {
      disposeGroup(vacant);
      world.vacant.delete(bld.key);
    }
    if (!layout.plots.has(id)) return false;
    layout.plots.get(id).building = bld;
    const placed = placeBuilding(bld) != null;
    world.rebuildOccupancy();
    return placed;
  };

  /** 文件夹被删：拆掉建筑，留下一圈地基石（空地记在账本里，永不回收）。 */
  world.vacate = (v) => {
    const id = `b:${v.key}`;
    const old = world.buildings.get(id);
    if (old) {
      disposeGroup(old.group);
      world.labels.splice(world.labels.indexOf(old.label), 1);
      world.buildings.delete(id);
    }
    if (!world.vacant.has(v.key)) placeVacant(v);
    world.rebuildOccupancy();
  };

  // —— 居民即会话（M-B，2026-10-13）：一场会话一位居民，长相由种子定（art/citizens.mjs）。人物一位建一次要几十毫秒，
  // 几十位一起建会卡住一帧，所以 setCitizens 只记下名单，人物由 buildNextCitizen 一位一位建（game 每帧建几位）。 ——
  world.citizens = new Map();   // id → { id, data, look, figure|null, label, plan|null, nudge, tier, ... }
  world.setCitizens = (list) => {
    const want = new Map((list ?? []).map((c) => [c.id, c]));
    for (const [id, c] of world.citizens) {
      if (want.has(id)) continue;
      if (c.figure) {
        root.remove(c.figure.root);
        c.figure.root.traverse((o) => { if (o.isMesh && o.geometry && !o.geometry.userData?.shared) o.geometry.dispose(); });
      }
      const i = world.labels.indexOf(c.label);
      if (i >= 0) world.labels.splice(i, 1);
      world.citizens.delete(id);
    }
    for (const [id, data] of want) {
      const existing = world.citizens.get(id);
      if (existing) {
        const moved = existing.data.home !== data.home;
        existing.data = data;
        existing.label.text = data.name ?? '';
        if (moved) existing.plan = null;   // 搬了家（最常碰的文件夹换了）：日程重算
        continue;
      }
      const look = citizenLook(data.seed, { age: data.age ?? 'adult' });
      const label = { text: data.name ?? '', kind: 'citizen', key: `citizen:${id}`, anchor: new THREE.Vector3() };
      world.labels.push(label);
      world.citizens.set(id, { id, data, look, figure: null, label, plan: null, nudge: { x: 0, z: 0 }, tier: 'C', yawNow: null });
    }
  };
  /** 建下一位还没有人物的居民；建了返回 true。 */
  world.buildNextCitizen = () => {
    for (const c of world.citizens.values()) {
      if (c.figure) continue;
      c.figure = buildCitizen(M, c.look.spec);
      c.figure.root.name = `figure:citizen:${c.id}`;
      c.figure.root.visible = false;
      add(c.figure.root);
      return true;
    }
    return false;
  };

  /**
   * 老居民（一年多没动过的会话）：不上街，名字刻在他家门口的一块小碑上（一家一块）。碑不挡路——它立在门前的街沿上，
   * 挡了会把雅典娜和你够得着的那一点堵死。groups：[{ home, names: [] }]。
   */
  world.steles = new Map();   // home → { home, names, object, x, z }
  world.setElders = (groups) => {
    const want = new Map((groups ?? []).filter((g) => g.names?.length).map((g) => [g.home, g]));
    for (const [home, s] of world.steles) {
      if (want.has(home)) continue;
      root.remove(s.object);
      world.steles.delete(home);
    }
    for (const [home, g] of want) {
      const existing = world.steles.get(home);
      if (existing) { existing.names = g.names; continue; }
      let stand;
      try { stand = layout.sitePoint(home); } catch { continue; }
      const r = rng(hashSeed(`stele:${home}`));
      // 门前那一点旁边 1.6–2.4 米、能站的地方（碑本身不挡路，但不要立在墙里、水里）
      let spot = null;
      for (let k = 0; k < 12 && !spot; k++) {
        const a = r() * Math.PI * 2, d = 1.6 + r() * 0.8;
        const p = { x: stand.x + Math.sin(a) * d, z: stand.z + Math.cos(a) * d };
        if (layout.walkable(p)) spot = p;
      }
      if (!spot) continue;
      const object = assets.object('prop.stele');
      object.scale.setScalar(0.5);
      object.position.set(spot.x, world.groundAt(spot.x, spot.z), spot.z);
      object.rotation.y = Math.atan2(stand.x - spot.x, stand.z - spot.z);
      object.userData.footprint = null;   // 不挡路（见上）
      add(object);
      world.steles.set(home, { home, names: g.names, object, x: spot.x, z: spot.z });
    }
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

  world.rebuildOccupancy();

  // 海的岸（art/water.mjs）：码头石岸、栈桥、三处场馆的台地、橄榄林的台地（几块连成一排，并成一个矩形）——
  // 世界坐标里的矩形 [minX, minZ, maxX, maxZ]。白沫贴着它们，离它们越近水越浅；game 每帧连同时间、船一起交给海的材质
  root.updateMatrixWorld(true);
  const rectOf = (objects) => {
    const box = new THREE.Box3();
    for (const o of objects) box.expandByObject(o);
    return [box.min.x, box.min.z, box.max.x, box.max.z];
  };
  world.waterShores = [
    [map.minX, shoreZ - 0.2, map.maxX, shoreZ + 1.4],           // 码头石岸（上面 quay.box 的范围）
    [-1.6, layout.quayZ, 1.6, layout.quayZ + 14],               // 栈桥（civic.buildHarbor 的石台）
    ...seaTerraces.map((t) => rectOf([t])),
    ...(groveTerraces.length ? [rectOf(groveTerraces.map((g) => g.object))] : []),
  ];
  return world;
}

/** 离 p 最近的能站的点（0.1 米一圈向外找，最远 maxR）；找不到就原地。 */
export function nearestWalkable(p, walkable, maxR = 1.5) {
  if (walkable(p)) return { x: p.x, z: p.z };
  for (let r = 0.1; r <= maxR + 1e-9; r += 0.1) {
    const n = Math.max(8, Math.round((2 * Math.PI * r) / 0.1));
    for (let k = 0; k < n; k++) {
      const a = (k / n) * Math.PI * 2;
      const q = { x: p.x + Math.cos(a) * r, z: p.z + Math.sin(a) * r };
      if (walkable(q)) return q;
    }
  }
  return { x: p.x, z: p.z };
}

/**
 * 居民出门走一小段的落脚点：从家门口按他的朝向偏一点走 2.4 米；撞上东西就换个方向试，八个方向都不通就待在家里不出门。
 * 一路上每 0.2 米都要能站（家门口本身被占着时，头 0.8 米不算）；给了 groundAt 的话，一路上的地面还要和家门口一样高——
 * 走路的人按家门口的高度摆、不会上下台阶，走到讲台下一级那样 0.22 米高的台面上，脚就陷在石头里。
 */
export function findOuting(home, walkable, distance = 2.4, groundAt = null) {
  const base = home.yaw + 1.2;
  const homeClear = walkable(home);
  const level = groundAt ? groundAt(home.x, home.z) : 0;
  for (let k = 0; k < 8; k++) {
    const a = base + (k % 2 === 0 ? 1 : -1) * Math.ceil(k / 2) * (Math.PI / 4);
    const out = { x: home.x + Math.sin(a) * distance, z: home.z + Math.cos(a) * distance };
    let clear = true;
    for (let d = 0.2; d <= distance + 1e-6 && clear; d += 0.2) {
      const p = { x: home.x + Math.sin(a) * d, z: home.z + Math.cos(a) * d };
      if (!walkable(p) && (homeClear || d > 0.8)) clear = false;
      if (groundAt && Math.abs(groundAt(p.x, p.z) - level) > 0.02) clear = false;
    }
    if (clear) return out;
  }
  return { x: home.x, z: home.z };
}
