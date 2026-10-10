// 把夹具和布局变成一座城：地面、海、街道、公共建筑、按文件夹类型生成的建筑、树、人物。
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

export const PUBLIC_NAMES = Object.freeze({
  agora: '广场', temple: '神庙', library: '图书馆', forge: '锻炉', harbor: '港口', market: '市集', gate: '城门',
});

export function buildWorld(scene, fixture, layout, M) {
  const world = { buildings: new Map(), labels: [], maidens: [], scaffolds: new Map() };
  const statue = statueGeometry();

  // —— 地面与海 ——
  const shoreZ = layout.quayZ + STREET / 2 + 0.2;
  const landDepth = 600;
  const land = new THREE.Mesh(groundPlane(600, landDepth, 15), M.earth);
  land.position.set(0, 0, shoreZ - landDepth / 2);
  land.receiveShadow = true;
  scene.add(land);
  const sea = new THREE.Mesh(groundPlane(600, 400, 8), M.water);
  sea.position.set(0, -0.7, shoreZ + 200);
  sea.receiveShadow = true;
  scene.add(sea);
  const quay = new Kit();
  quay.box(M.stone, 600, 1.2, 1.6, 0, -0.55, shoreZ + 0.6);
  scene.add(quay.build());

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
  scene.add(streetGroup);

  const placeAt = (group, plot) => {
    group.position.set(plot.cx, 0, plot.cz);
    group.rotation.y = plot.yaw;
    scene.add(group);
    return group;
  };
  const labelAt = (text, plot, height, kind) => {
    world.labels.push({ text, kind, anchor: new THREE.Vector3(plot.cx, height + 0.8, plot.cz) });
  };

  // —— 公共建筑 ——
  const pub = (name) => layout.plots.get(name);
  const temple = buildTemple(M, { nx: 6, nz: 7 });
  temple.scale.setScalar(0.62);
  placeAt(temple, pub('temple'));
  labelAt(PUBLIC_NAMES.temple, pub('temple'), temple.userData.height * 0.62, 'public');
  const tholos = placeAt(buildTholos(M), pub('library'));
  labelAt(PUBLIC_NAMES.library, pub('library'), tholos.userData.height, 'public');
  const forge = placeAt(buildForge(M), pub('forge'));
  labelAt(PUBLIC_NAMES.forge, pub('forge'), forge.userData.height, 'public');
  world.forge = forge;
  const market = placeAt(buildMarket(M, (fixture.market ?? []).length), pub('market'));
  labelAt(PUBLIC_NAMES.market, pub('market'), market.userData.height, 'public');
  (fixture.market ?? []).forEach((key, k) => {
    const spot = market.userData.stallSpots[k % market.userData.stallSpots.length];
    const local = new THREE.Vector3(spot[0], 3.0, spot[1]).applyAxisAngle(new THREE.Vector3(0, 1, 0), pub('market').yaw);
    world.labels.push({ text: key, kind: 'stall', anchor: new THREE.Vector3(pub('market').cx + local.x, local.y, pub('market').cz + local.z) });
  });
  const agora = placeAt(buildAgora(M), pub('agora'));
  agora.rotation.y = 0;
  labelAt(PUBLIC_NAMES.agora, { cx: 0, cz: -4.2 }, 3.4, 'public');

  const harbor = buildHarbor(M, layout.quayZ, 14, pub('harbor').cz, statue);
  scene.add(harbor);
  world.labels.push({ text: PUBLIC_NAMES.harbor, kind: 'public', anchor: new THREE.Vector3(0, 2.6, layout.quayZ + 6) });
  const boat = buildBoat(M);
  world.boatHome = new THREE.Vector3(3.8, -0.05, layout.quayZ + 9.5);
  boat.position.copy(world.boatHome);
  scene.add(boat);
  world.boat = boat;

  const gate = buildGate(M);
  gate.position.set(layout.gate.x, 0, layout.gate.z - 1.5);
  scene.add(gate);
  world.labels.push({ text: PUBLIC_NAMES.gate, kind: 'public', anchor: new THREE.Vector3(layout.gate.x, gate.userData.height + 0.6, layout.gate.z - 1.5) });

  // —— 文件夹的建筑 ——
  const builders = {
    stoaLibrary: buildStoaLibrary, house: buildHouse, workshop: buildWorkshop, sculptureGarden: buildSculptureGarden,
    treasury: buildTreasury, warehouse: buildWarehouse,
  };
  for (const bld of fixture.buildings ?? []) {
    const plot = layout.plots.get(`b:${bld.key}`);
    const r = rng(hashSeed(bld.key));
    const build = builders[bld.kind] ?? buildHouse;
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
    world.buildings.set(`b:${bld.key}`, { group, height, kind: bld.kind, building: bld });
    labelAt(bld.key, plot, height, 'building');
  }

  // 被删掉的文件夹留下的空地：一圈残存的地基石
  for (const v of fixture.vacant ?? []) {
    const kit = new Kit();
    const r = rng(hashSeed(v.key));
    for (let i = 0; i < 9; i++) {
      const a = (i / 9) * Math.PI * 2;
      kit.box(M.stone, 1.2 + r(), 0.4 + r() * 0.3, 0.7, Math.cos(a) * 4, 0.2, Math.sin(a) * 3.5, a);
    }
    const g = kit.build();
    g.position.set(v.plot.x * BLOCK, 0, v.plot.z * BLOCK);
    scene.add(g);
  }

  // —— 树：没盖房子的地块种橄榄，神庙和城门边种柏树 ——
  const occupied = new Set([...layout.plots.values()].map((p) => `${p.x},${p.z}`));
  for (const v of fixture.vacant ?? []) occupied.add(`${v.plot.x},${v.plot.z}`);
  const tr = rng(20261011);
  for (let x = -layout.ring - 1; x <= layout.ring + 1; x++) {
    for (let z = -layout.ring - 1; z <= layout.seaZ - 1; z++) {
      if (occupied.has(`${x},${z}`)) continue;
      const n = 1 + Math.floor(tr() * 3);
      for (let k = 0; k < n; k++) {
        const tree = tr() < 0.75 ? buildOlive(M, 31 + k + x * 7 + z * 13, tr) : buildCypress(M, 6 + tr() * 3, 3 + k);
        tree.position.set(x * BLOCK + (tr() - 0.5) * PLOT * 0.7, 0, z * BLOCK + (tr() - 0.5) * PLOT * 0.7);
        tree.rotation.y = tr() * 6;
        scene.add(tree);
      }
    }
  }
  const tp = pub('temple');
  for (const sx of [-1, 1]) {
    const c = buildCypress(M, 8, 5 + sx);
    c.position.set(tp.cx + sx * 7.4, 0, tp.cz - 3);
    scene.add(c);
  }

  // —— 人物 ——
  const athena = buildAthena(M);
  scene.add(athena);
  world.athena = athena;
  world.props = buildProps(M);
  const hand = athena.userData.hand;
  for (const prop of Object.values(world.props)) {
    prop.position.copy(hand);
    athena.add(prop);
  }
  const player = buildPlayer(M);
  scene.add(player);
  world.player = player;
  const mg = maidenGeometry();
  for (let k = 0; k < 6; k++) {
    const maiden = new THREE.Mesh(mg, M.maiden);
    maiden.castShadow = true;
    maiden.visible = false;
    maiden.scale.setScalar(0.9);
    scene.add(maiden);
    world.maidens.push(maiden);
  }
  return world;
}
