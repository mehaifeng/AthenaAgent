// 把城邦文档（C# 组装的 PolisFixtureDocument：建筑、地块、空地、市集）和布局变成一座城：地面、海、街道、公共建筑、
// 按文件夹类型生成的建筑、树、人物。整座城挂在一个根节点下，换城时整体拆掉；同一座城里的建筑变化（文件增减、
// 新建或删除文件夹）按座替换，不重建整座城——外部改动只让建筑轻微变化（设计稿 11.2）。
import * as THREE from '../vendor/three.bundle.mjs';
import {
  Kit, m4, groundPlane, buildTemple, buildStoaLibrary, buildHouse, buildWorkshop, buildSculptureGarden,
  buildTreasury, buildWarehouse, buildTholos, buildForge, buildMarket, buildGate, buildAgora, buildHarbor,
  buildBoat, buildScaffold,
} from './art/architecture.mjs';
import {
  buildAthena, buildPlayer, maidenGeometry, statueGeometry, buildProps, buildCypress, buildOlive,
} from './art/figures.mjs';
import { rng, hashSeed } from './art/noise.mjs';
import { BLOCK, STREET, PLOT } from './layout.mjs';

/** 公共建筑的名字。实时模式里由 C# 按界面语言推来（locale 文件），这里是独立打开页面时的默认值。 */
export const PUBLIC_NAMES = Object.freeze({
  agora: '广场', temple: '神庙', library: '图书馆', forge: '锻炉', harbor: '港口', market: '市集', gate: '城门',
  sanctuary: '雅典娜的神殿',
});

const BUILDERS = {
  stoaLibrary: buildStoaLibrary, house: buildHouse, workshop: buildWorkshop, sculptureGarden: buildSculptureGarden,
  treasury: buildTreasury, warehouse: buildWarehouse,
};

/**
 * @param {THREE.Scene} scene
 * @param {object} city 城邦文档（buildings / vacant / market / publicSites / seaStartsAtZ）
 * @param {object} layout createLayout(city)
 * @param {object} M 材质
 * @param {{ names?: object, sanctuary?: boolean }} [options]
 */
export function buildWorld(scene, city, layout, M, options = {}) {
  const names = { ...PUBLIC_NAMES, ...(options.names ?? {}) };
  const root = new THREE.Group();
  root.name = 'polis-world';
  scene.add(root);
  const world = {
    root, layout, buildings: new Map(), vacant: new Map(), labels: [], maidens: [], scaffolds: new Map(), sanctuary: !!options.sanctuary,
  };
  const statue = statueGeometry();
  // 雕像几何被每座雕塑园和港口共用：替换一座建筑时不能释放它
  statue.userData.shared = true;
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

  // —— 街道：横街、竖街铺石板，广场整块铺地 ——
  const b = layout.bounds;
  const streets = new Kit();
  const iMin = Math.round(b.minX / BLOCK), iMax = Math.round(b.maxX / BLOCK);
  for (let j = Math.round(b.minZ / BLOCK); (j + 0.5) * BLOCK <= layout.quayZ + 0.01; j++) {
    const z = (j + 0.5) * BLOCK;
    const width = j + 0.5 === layout.seaZ - 0.5 ? STREET + 1.5 : STREET;   // 码头那条宽一些
    streets.add(M.street, groundPlane(b.maxX - b.minX, width, 4), m4((b.maxX + b.minX) / 2, 0.012, z));
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

  // —— 公共建筑 ——
  const pub = (name) => layout.plots.get(name);
  const temple = buildTemple(M, { nx: 6, nz: 7 });
  // 神殿（全局对话）：没有文件夹的城，神庙就是主角，盖得更大
  const templeScale = world.sanctuary ? 0.95 : 0.62;
  temple.scale.setScalar(templeScale);
  placeAt(temple, pub('temple'));
  labelAt(world.sanctuary ? names.sanctuary : names.temple, pub('temple'), temple.userData.height * templeScale, 'public');
  const tholos = placeAt(buildTholos(M), pub('library'));
  labelAt(names.library, pub('library'), tholos.userData.height, 'public');
  const forge = placeAt(buildForge(M), pub('forge'));
  labelAt(names.forge, pub('forge'), forge.userData.height, 'public');
  world.forge = forge;
  const marketKeys = city.market ?? [];
  const market = placeAt(buildMarket(M, marketKeys.length), pub('market'));
  labelAt(names.market, pub('market'), market.userData.height, 'public');
  marketKeys.forEach((key, k) => {
    const spot = market.userData.stallSpots[k % market.userData.stallSpots.length];
    const local = new THREE.Vector3(spot[0], 3.0, spot[1]).applyAxisAngle(new THREE.Vector3(0, 1, 0), pub('market').yaw);
    world.labels.push({ text: key, kind: 'stall', key: `stall:${key}`, anchor: new THREE.Vector3(pub('market').cx + local.x, local.y, pub('market').cz + local.z) });
  });
  const agora = placeAt(buildAgora(M), pub('agora'));
  agora.rotation.y = 0;
  labelAt(names.agora, { cx: 0, cz: -4.2 }, 3.4, 'public');

  const harbor = buildHarbor(M, layout.quayZ, 14, pub('harbor').cz, statue);
  add(harbor);
  world.labels.push({ text: names.harbor, kind: 'public', key: null, anchor: new THREE.Vector3(0, 2.6, layout.quayZ + 6) });
  const boat = buildBoat(M);
  world.boatHome = new THREE.Vector3(3.8, -0.05, layout.quayZ + 9.5);
  boat.position.copy(world.boatHome);
  add(boat);
  world.boat = boat;

  const gate = buildGate(M);
  gate.position.set(layout.gate.x, 0, layout.gate.z - 1.5);
  add(gate);
  world.labels.push({ text: names.gate, kind: 'public', key: null, anchor: new THREE.Vector3(layout.gate.x, gate.userData.height + 0.6, layout.gate.z - 1.5) });

  // —— 文件夹的建筑：一座一座放，之后可以按座替换 ——
  function placeBuilding(bld) {
    const plot = layout.plots.get(`b:${bld.key}`);
    if (!plot) return null;
    const r = rng(hashSeed(bld.key));
    const build = BUILDERS[bld.kind] ?? buildHouse;
    const group = placeAt(build(M, bld, r, statue), plot);
    for (const [tx, tz] of group.userData.trees ?? []) {
      const tree = buildCypress(M, 5 + r() * 2, hashSeed(bld.key) % 97);
      tree.position.set(tx, 0, tz);
      group.add(tree);
    }
    const height = group.userData.height;
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
        const tree = tr() < 0.75 ? buildOlive(M, 31 + k + x * 7 + z * 13, tr) : buildCypress(M, 6 + tr() * 3, 3 + k);
        tree.position.set(x * BLOCK + (tr() - 0.5) * PLOT * 0.7, 0, z * BLOCK + (tr() - 0.5) * PLOT * 0.7);
        tree.rotation.y = tr() * 6;
        add(tree);
      }
    }
  }
  const tp = pub('temple');
  for (const sx of [-1, 1]) {
    const c = buildCypress(M, 8, 5 + sx);
    c.position.set(tp.cx + sx * 7.4, 0, tp.cz - 3);
    add(c);
  }

  // —— 人物 ——
  const athena = buildAthena(M);
  add(athena);
  world.athena = athena;
  world.props = buildProps(M);
  const hand = athena.userData.hand;
  for (const prop of Object.values(world.props)) {
    prop.position.copy(hand);
    athena.add(prop);
  }
  const player = buildPlayer(M);
  add(player);
  world.player = player;
  const mg = maidenGeometry();
  mg.userData.shared = true;
  for (let k = 0; k < 6; k++) {
    const maiden = new THREE.Mesh(mg, M.maiden);
    maiden.castShadow = true;
    maiden.visible = false;
    maiden.scale.setScalar(0.9);
    add(maiden);
    world.maidens.push(maiden);
  }

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
      if (!o.isMesh || !o.geometry || seen.has(o.geometry)) return;
      seen.add(o.geometry);
      o.geometry.dispose();
    });
  };

  return world;
}
