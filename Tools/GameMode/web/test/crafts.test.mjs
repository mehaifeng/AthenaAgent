// 手艺（第三轮需求 4，2026-10-13，用户：增加一些简单的手工产业动画、使用工具的动画，让街道上的人不再全部无所事事）。
// art/crafts.mjs 的职业 → 活、家什、工位；poses 的十种活在真骨架上的接触点（和 tools.test.mjs 核对铁匠、工匠一样）；
// citizens.mjs 规则 7 的日程、工位的摆放与收放；在 Node 里把合成夹具的城建出来核对门前的工位。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import { countrySpot, sheepAt, FIELD, PEN, furrowZ } from '../../../../Assets/Polis/src/art/countryside.mjs';
import { PLOT } from '../../../../Assets/Polis/src/layout.mjs';
import {
  TRADE_KEYS, TRADE_CRAFT, CRAFTS, STATION_GAPS, STATION_EXTENT, CRAFT_REFERENCE_HEIGHT, tradeOf, craftFor, craftSite,
} from '../../../../Assets/Polis/src/art/crafts.mjs';
import { STATION_TOP } from '../../../../Assets/Polis/src/art/stations.mjs';
import { citizenLook, RESIDENT_SIGNATURES } from '../../../../Assets/Polis/src/art/citizens.mjs';
import { buildCitizen } from '../../../../Assets/Polis/src/art/characters.mjs';
import { poseAt, WORK_VARIANTS, JOINT_NAMES } from '../../../../Assets/Polis/src/art/poses.mjs';
import { GRIP, LEFT_HAND_PROPS, TWO_HANDED_PROPS } from '../../../../Assets/Polis/src/art/rig.mjs';
import { planCitizen, citizenAt, settleStations, stationClear, inStation, routeClear, TOWN } from '../../../../Assets/Polis/src/citizens.mjs';
import { createLayout } from '../../../../Assets/Polis/src/layout.mjs';
import { createAssetLibrary } from '../../../../Assets/Polis/src/art/assets.mjs';
import { buildWorld } from '../../../../Assets/Polis/src/world.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));

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

const M = stubMaterials();
const V = (x, y, z) => new THREE.Vector3(x, y, z);
const tradeOfCraft = Object.fromEntries(Object.entries(TRADE_CRAFT).map(([t, c]) => [c, t]));
const at = (f, side, local) => new THREE.Vector3(...(side === 'R' ? [-local[0], local[1], local[2]] : local)).applyMatrix4(f.joints[`hand${side}`].matrixWorld);
const dirOf = (f, side, local) => new THREE.Vector3(...local).transformDirection(f.joints[`hand${side}`].matrixWorld);
const verticesOf = (obj) => {
  const out = [];
  obj.updateMatrixWorld(true);
  obj.traverse((o) => {
    if (!o.isMesh || o.visible === false) return;
    const pos = o.geometry.attributes.position, v = new THREE.Vector3();
    for (let i = 0; i < pos.count; i++) out.push(v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld).clone());
  });
  return out;
};
const lowest = (obj) => verticesOf(obj).reduce((a, v) => (!a || v.y < a.y ? v : a), null);

/** 参照人（身高 1.75、体格 1）干某一门手艺；sample(t) 摆到那一刻（先站稳、过渡走完，布料与两手家什都推进过）。 */
function worker(trade, { female = false, height = CRAFT_REFERENCE_HEIGHT } = {}) {
  const seed = female ? 12347 : 12346;
  const f = buildCitizen(M, { ...citizenLook(seed, { trade }).spec, height, build: 1 });
  const { variant } = craftFor(trade, seed);
  let clock = 0;
  for (let k = 0; k < 10; k++) { clock += 16; f.pose('idle', clock, { clock }); }
  const sample = (t) => {
    for (let k = 20; k >= 0; k--) { clock += 16; f.pose('work', Math.max(0, t - k * 16), { variant, clock }); }
    f.root.updateMatrixWorld(true);
    return f;
  };
  return { f, sample, variant };
}

test('crafts: the trade table is C#\'s (PolisTrades), every trade but the idle has a craft, every craft is somebody\'s, and an unknown trade falls back to the seed', () => {
  // 与 C# 的 PolisTrades.Keys 同一个次序（Archive 的 polis citizens 断言同一张表）
  assert.deepEqual([...TRADE_KEYS], ['farmer', 'herder', 'fisher', 'weaver', 'spinner', 'potter', 'baker', 'miller', 'cobbler', 'mason', 'carpenter', 'musician', 'merchant', 'idle']);
  for (const t of TRADE_KEYS) {
    if (t === 'idle') { assert.equal(craftFor(t, 1).craft, null, '无业的人没有活'); assert.deepEqual([...craftFor(t, 1).props], []); continue; }
    assert.ok(CRAFTS[TRADE_CRAFT[t]], `${t} 有活干`);
  }
  for (const c of Object.keys(CRAFTS)) assert.ok(Object.values(TRADE_CRAFT).includes(c), `${c} 有人干`);
  assert.deepEqual(craftFor('nonsense', 12345), craftFor(tradeOf(12345), 12345), '不认识的职业按种子算');
  assert.ok(TRADE_KEYS.includes(craftFor(null, 7).trade));
  assert.equal(craftFor('farmer', 1).site, 'fields', '农夫在城北的田里锄地');
  assert.equal(craftFor('farmer', 1).needsSpot, true, '农夫要田里的一条垄沟');
  assert.equal(craftFor('herder', 1).site, 'pasture', '牧人在牧场');
  assert.equal(craftFor('herder', 1).variant, 'herd');
  assert.equal(craftFor('weaver', 1).station, 'loom', '织工在门前的织机上织布');
  assert.equal(craftFor('merchant', 1).site, 'market', '商贩在市集吆喝');
  assert.equal(craftFor('merchant', 1).portable, true, '走不到市集就在门前吆喝');
  assert.equal(craftFor('miller', 1).site, 'home', '磨坊工在自家门前研磨');
  assert.equal(craftSite(craftFor('musician', 9), 9), 'agora:3', '乐师在广场一圈里按种子挑的那一点');
  assert.equal(craftFor('fisher', 1).needsSpot, true, '渔夫要世界给的站位');
});

test('crafts: each craft\'s pose takes out exactly its tools, none of them a resident\'s signature, each in the hand it belongs to', () => {
  for (const [name, c] of Object.entries(CRAFTS)) {
    assert.ok(WORK_VARIANTS.includes(c.variant), `${name}：poses 认得 ${c.variant}`);
    const named = new Set();
    for (let t = 0; t < 8000; t += 53) {
      const p = poseAt('work', t, { variant: c.variant });
      for (const [side, prop] of [['L', p.propL], ['R', p.propR]]) {
        if (!prop) continue;
        named.add(prop);
        if (TWO_HANDED_PROPS.includes(prop)) continue;
        assert.equal(LEFT_HAND_PROPS.includes(prop), side === 'L', `${name}：${prop} 在 ${side} 手`);
      }
    }
    assert.deepEqual([...named].sort(), [...c.props].sort(), `${name}：姿势拿出来的就是它的家什`);
    for (const p of c.props) assert.ok(!RESIDENT_SIGNATURES.props.includes(p), `${name}：${p} 不是居民的招牌`);
  }
});

test('crafts: the loops run smoothly — no joint jumps between two frames, the seam included', () => {
  const angles = (pose) => JOINT_NAMES.flatMap((j) => Object.entries(pose[j]).filter(([k]) => !['x', 'y', 'z', 'sz'].includes(k)).map(([, v]) => v));
  for (const c of Object.values(CRAFTS)) {
    let worst = 0;
    for (let t = 0; t < 14_000; t += 16) {
      const a = angles(poseAt('work', t, { variant: c.variant })), b = angles(poseAt('work', t + 16, { variant: c.variant }));
      worst = Math.max(worst, ...a.map((v, i) => Math.abs(v - b[i])));
    }
    // 落斧、落锄那一下快（0.2 秒落 1.3 弧度），但不会跳
    assert.ok(worst < 0.25, `${c.variant}：一帧最多转 ${worst.toFixed(3)} 弧度`);
  }
});

test('crafts: on the real rig the hands meet the work — dough, clay, the stone, the saw cut, the ground', () => {
  // 揉面：两只掌心按在面团上（面团顶 0.9 米，台子在人前 0.42 米：面团前后 0.31–0.53、左右 ±0.14），掌心朝下
  {
    const { f, sample } = worker('baker');
    for (const t of [0, 300, 715, 1000]) {
      sample(t);
      for (const side of ['L', 'R']) {
        const p = at(f, side, GRIP.palm);
        assert.ok(p.y > 0.87 && p.y < 0.93, `揉面 t=${t} ${side}：掌心在面团顶上（${p.y.toFixed(3)}）`);
        assert.ok(p.z > 0.31 && p.z < 0.53 && Math.abs(p.x) < 0.14, `揉面 t=${t} ${side}：掌心在面团上（${p.x.toFixed(2)}, ${p.z.toFixed(2)}）`);
        assert.ok(dirOf(f, side, [0, 0, 1]).y < -0.9, `揉面 t=${t} ${side}：掌心朝下`);
      }
    }
  }
  // 拉坯：两只掌心贴着陶坯（坯的轴在人前 0.42 米；坯腰 0.9 米、半径 0.105）——离坯面不到 2.5 厘米，掌心朝着坯
  {
    const { f, sample } = worker('potter');
    const radius = (y) => {
      const pts = [[0, 0], [0.07, 0.005], [0.1, 0.05], [0.105, 0.11], [0.085, 0.16], [0.07, 0.19], [0.075, 0.2]];
      const h = y - STATION_TOP.wheel;
      for (let k = 1; k < pts.length; k++) if (h <= pts[k][1]) return pts[k - 1][0] + (pts[k][0] - pts[k - 1][0]) * ((h - pts[k - 1][1]) / (pts[k][1] - pts[k - 1][1]));
      return pts[pts.length - 1][0];
    };
    for (const t of [0, 650, 1300, 1950]) {
      sample(t);
      for (const side of ['L', 'R']) {
        const p = at(f, side, GRIP.palm);
        const gap = Math.hypot(p.x, p.z - STATION_GAPS.wheel) - radius(p.y);
        assert.ok(gap > -0.005 && gap < 0.025, `拉坯 t=${t} ${side}：掌心贴着坯（离坯面 ${gap.toFixed(3)} 米，高 ${p.y.toFixed(2)}）`);
        const n = dirOf(f, side, [0, 0, 1]);
        assert.ok(n.x * (side === 'L' ? -1 : 1) > 0.9, `拉坯 t=${t} ${side}：掌心朝着坯`);
      }
    }
  }
  // 琢石：落斧那一下斧头最低的一点在石面上（顶 0.6 米，石料在人前 0.58 米：前后 0.34–0.82）；举起来时斧头过头顶
  {
    const { f, sample } = worker('mason');
    sample(1150);
    const low = lowest(f.props.get('stoneAxe'));
    assert.ok(low.y > 0.56 && low.y < 0.64, `落斧落在石面上（${low.y.toFixed(3)}）`);
    assert.ok(low.z > 0.34 && low.z < 0.82 && Math.abs(low.x) < 0.27, `落在石料上（${low.x.toFixed(2)}, ${low.z.toFixed(2)}）`);
    sample(770);
    assert.ok(lowest(f.props.get('stoneAxe')).y > 1.3, '举过肩');
    for (const side of ['L', 'R']) assert.ok(f.props.get('stoneAxe').visible, `两手握着斧（${side}）`);
  }
  // 锯木：锯条竖着、锯齿朝下，锯齿在木板的中线上（人前 0.42 米）切进去一点（0.81–0.85 米），整个来回都在；左掌按着木板
  {
    const { f, sample } = worker('carpenter');
    const F = GRIP.fist;
    for (const t of [0, 250, 500, 750]) {
      sample(t);
      // 锯齿那一条边：右手小指那一侧（at() 给右手镜像了 x，所以写 −0.1）
      const a = at(f, 'R', [-0.1, F[1] - 0.605, F[2]]), b = at(f, 'R', [-0.1, F[1] - 0.055, F[2]]);
      const u = (STATION_GAPS.sawhorse - a.z) / (b.z - a.z);
      const edge = u >= 0 && u <= 1 ? a.clone().lerp(b, u) : null;
      assert.ok(edge && edge.y > 0.81 && edge.y < 0.85, `锯木 t=${t}：锯齿在锯口里（${edge?.y.toFixed(3)}）`);
      assert.ok(Math.abs(dirOf(f, 'R', [0, 0, 1]).y) < 0.1, `锯木 t=${t}：锯条竖着`);
      const palm = at(f, 'L', GRIP.palm);
      assert.ok(palm.y > 0.84 && palm.y < 0.9 && palm.z > 0.31 && palm.z < 0.53, `锯木 t=${t}：左掌按着木板（${palm.y.toFixed(3)}, ${palm.z.toFixed(2)}）`);
    }
  }
  // 织布（第四轮）：经线面在人前 0.42 米；整个来回两只掌心都贴着经线（离面不到 3 厘米）、在织口一带、大致朝前
  {
    const { f, sample } = worker('weaver');
    for (const t of [0, 520, 1040, 1300, 1560, 2080, 2340]) {
      sample(t);
      for (const side of ['L', 'R']) {
        const p = at(f, side, GRIP.palm);
        assert.ok(Math.abs(p.z - STATION_GAPS.loom) < 0.03, `织布 t=${t} ${side}：掌心贴着经线（离面 ${(p.z - STATION_GAPS.loom).toFixed(3)} 米）`);
        assert.ok(p.y > 1.15 && p.y < 1.4 && Math.abs(p.x) < 0.55, `织布 t=${t} ${side}：在织口一带（${p.x.toFixed(2)}, ${p.y.toFixed(2)}）`);
        assert.ok(dirOf(f, side, [0, 0, 1]).z > 0.5, `织布 t=${t} ${side}：掌心朝着织机`);
      }
    }
  }
  // 锄地：落锄那一下锄刃落在人前 0.7–1 米的地上；两只手都在锄柄上
  {
    const { f, sample } = worker('farmer');
    sample(1300);
    const low = lowest(f.props.get('hoe'));
    assert.ok(low.y > -0.03 && low.y < 0.06, `锄刃到地（${low.y.toFixed(3)}）`);
    assert.ok(low.z > 0.7 && low.z < 1.0, `落在身前（${low.z.toFixed(2)}）`);
  }
});

test('crafts: on the real rig the hand-held crafts touch what they hold — the mortar, the sole, the strings', () => {
  // 研磨：杵头捣下去落在钵底（钵里 1.6 厘米上），提起来离钵底 0.1 米以上；研钵口朝上
  {
    const { f, sample } = worker('miller');
    sample(0);
    const mortar = f.props.get('mortar').children[0];
    const bottom = V(0, 0.016, 0).applyMatrix4(mortar.matrixWorld);
    const tip = lowest(f.props.get('pestle'));
    assert.ok(tip.distanceTo(bottom) < 0.03, `杵头落在钵底（差 ${tip.distanceTo(bottom).toFixed(3)} 米）`);
    assert.ok(new THREE.Vector3(0, 1, 0).transformDirection(mortar.matrixWorld).y > 0.95, '研钵口朝上');
    sample(456);
    assert.ok(lowest(f.props.get('pestle')).y - bottom.y > 0.1, '提起来离钵底 0.1 米以上');
  }
  // 缝鞋：锥尖扎在鞋底的面上
  {
    const { f, sample } = worker('cobbler');
    sample(0);
    const sole = at(f, 'L', [0, GRIP.palm[1] - 0.06, GRIP.palm[2] + 0.015]);
    const tip = at(f, 'R', [0.004, -0.128, 0.052]);
    assert.ok(tip.distanceTo(sole) < 0.02, `锥尖在鞋底上（差 ${tip.distanceTo(sole).toFixed(3)} 米）`);
    sample(700);
    assert.ok(at(f, 'R', [0.004, -0.128, 0.052]).distanceTo(sole) > 0.05, '拉线时锥子离开鞋底');
  }
  // 弹琴：琴竖着（弦朝上）、弦面朝前；右手的指尖在弦前、弦的中段
  {
    const { f, sample } = worker('musician');
    for (const t of [0, 480]) {
      sample(t);
      assert.ok(dirOf(f, 'L', [0, 0, 1]).y > 0.95, `弹琴 t=${t}：琴竖着`);
      const strings = at(f, 'L', [0, GRIP.palm[1] - 0.05, GRIP.palm[2] + 0.26]);
      const pinch = at(f, 'R', GRIP.pinch);
      assert.ok(pinch.distanceTo(strings) < 0.07, `弹琴 t=${t}：指尖在弦上（差 ${pinch.distanceTo(strings).toFixed(3)} 米）`);
    }
  }
  // 看羊（第四轮）：牧杖竖着、杖脚落在右前方的地上，左拳叠在右拳上面
  {
    const { f, sample } = worker('herder');
    for (const t of [0, 3000, 6000]) {
      sample(t);
      const crook = f.props.get('crook');
      assert.ok(crook.visible, `看羊 t=${t}：拿着牧杖`);
      assert.ok(dirOf(f, 'R', [0, 1, 0]).y > 0.97, `看羊 t=${t}：牧杖竖着`);
      const foot = lowest(crook);
      assert.ok(foot.y > -0.03 && foot.y < 0.08, `看羊 t=${t}：杖脚在地上（${foot.y.toFixed(3)}）`);
      assert.ok(foot.z > 0.1 && foot.x < -0.05, `看羊 t=${t}：在右前方（${foot.x.toFixed(2)}, ${foot.z.toFixed(2)}）`);
      const L = at(f, 'L', GRIP.fist), R = at(f, 'R', GRIP.fist);
      assert.ok(L.y - R.y > 0.07 && Math.hypot(L.x - R.x, L.z - R.z) < 0.06, `看羊 t=${t}：左拳叠在右拳上`);
    }
  }
  // 纺线：卷线杆竖着、纺锤竖着垂下
  {
    const { f, sample } = worker('spinner');
    sample(400);
    assert.ok(dirOf(f, 'L', [0, 1, 0]).y > 0.9, '卷线杆竖着');
    assert.ok(dirOf(f, 'R', [0, 1, 0]).y > 0.85, '纺锤竖着垂下');
  }
  // 钓鱼：竿朝前上 15°–45°，线从竿梢垂到脚底以下（站在栈桥上就进了水）
  {
    const { f, sample } = worker('fisher');
    sample(1000);
    const rod = f.props.get('fishingRod');
    const L = at(f, 'L', GRIP.fist), R = at(f, 'R', GRIP.fist);
    const d = L.clone().sub(R).normalize();
    const elevation = THREE.MathUtils.radToDeg(Math.asin(d.y));
    assert.ok(d.z > 0.5 && elevation > 15 && elevation < 45, `竿朝前上（${elevation.toFixed(1)}°）`);
    assert.ok(lowest(rod).y < -0.15, `线垂到脚底以下（${lowest(rod).y.toFixed(2)}）`);
  }
});

test('crafts: a taller or shorter worker meets a station scaled by his height (crafts rule 4)', () => {
  for (const height of [1.58, 1.82]) {
    const k = height / CRAFT_REFERENCE_HEIGHT;
    const { f, sample } = worker('baker', { height });
    sample(0);
    const p = at(f, 'L', GRIP.palm);
    assert.ok(Math.abs(p.y - 0.905 * k) < 0.02 && Math.abs(p.z - 0.35 * k) < 0.02, `身高 ${height}：掌心在按比例缩放的面团上（${p.y.toFixed(3)}, ${p.z.toFixed(3)}）`);
  }
});

test('crafts: the stations are the size crafts says, stand on the ground, and never block', () => {
  const assets = createAssetLibrary(M);
  for (const kind of Object.keys(STATION_EXTENT)) {
    const object = assets.object(`prop.station-${kind}`);
    object.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(object);
    const [ex, ez] = STATION_EXTENT[kind];
    assert.ok(box.max.x <= ex + 0.005 && box.min.x >= -ex - 0.005, `${kind}：左右在 ±${ex} 之内（${box.min.x.toFixed(3)}…${box.max.x.toFixed(3)}）`);
    assert.ok(box.max.z <= ez + 0.005 && box.min.z >= -ez - 0.005, `${kind}：前后在 ±${ez} 之内（${box.min.z.toFixed(3)}…${box.max.z.toFixed(3)}）`);
    assert.ok(Math.max(box.max.x, -box.min.x) > ex - 0.06 && Math.max(box.max.z, -box.min.z) > ez - 0.06, `${kind}：量的是它自己的大小，不是一个大得多的框`);
    assert.ok(Math.abs(box.min.y) < 0.02, `${kind}：立在地上`);
    assert.ok(box.max.y >= STATION_TOP[kind] - 0.01, `${kind}：干活的那一面在 ${STATION_TOP[kind]} 米`);
    assert.equal(assets.footprint(object), null, `${kind}：不挡路`);
  }
});

function buildCity(city = fixture) {
  const layout = createLayout(city);
  const world = buildWorld(new THREE.Scene(), city, layout, M, { assets: createAssetLibrary(M) });
  return { world, layout };
}
const has = (layout) => (s) => s.startsWith('agora') || layout.plots.has(s) || s === 'harbor' || s === 'market';

test('crafts: at home a worker stands along the wall with the door behind him and works at his station; it never blocks the doorway (citizens rule 7)', () => {
  const { world, layout } = buildCity();
  const doors = [...world.doors.values()];
  let worked = 0, stations = 0;
  for (const door of doors) {
    for (const trade of ['potter', 'baker', 'mason', 'carpenter', 'miller', 'cobbler', 'weaver', 'spinner']) {
      for (const seed of [11, 12, 13, 14]) {
        const craft = craftFor(trade, seed);
        const plan = planCitizen({ id: `${trade}-${seed}`, seed, home: door.building, craft: { ...craft, site: craftSite(craft, seed), scale: 1 } }, {
          sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite: has(layout), neighbours: [], doorFor: (s, sd) => world.doorFor(s, sd),
        });
        if (!plan.door || !plan.craft?.atHome) continue;
        worked++;
        const home = citizenAt(plan, TOWN.insideMs + 3000 - plan.offset);
        assert.equal(home.at, 'home');
        assert.equal(home.action, 'work', `${trade}：在门前干活`);
        assert.equal(home.work, craft.variant);
        // 背对着门：他面朝的方向离门阶越走越远
        const f = { x: Math.sin(home.yaw), z: Math.cos(home.yaw) };
        assert.ok((home.x - plan.door.step.x) * f.x + (home.z - plan.door.step.z) * f.z > 0, `${trade}：门在他背后`);
        if (plan.craft.station) {
          stations++;
          const st = plan.craft.station;
          assert.ok(stationClear(st.kind, st, st.yaw, layout.walkable), `${trade}：工位四角都能站`);
          const gap = Math.hypot(st.x - home.x, st.z - home.z);
          assert.ok(Math.abs(gap - STATION_GAPS[st.kind]) < 1e-9, `${trade}：工位在他面前 ${STATION_GAPS[st.kind]} 米`);
          assert.ok(!inStation(st, plan.door.step, 0.35), `${trade}：工位不压着门阶`);
          const approach = world.doorPlan(plan.door.id).approach;
          for (let k = 1; k < approach.length; k++) {
            for (let i = 0; i <= 10; i++) {
              const p = { x: approach[k - 1].x + (approach[k].x - approach[k - 1].x) * i / 10, z: approach[k - 1].z + (approach[k].z - approach[k - 1].z) * i / 10 };
              assert.ok(!inStation(st, p, 0.2), `${trade}：工位不在门阶到街上的路上`);
            }
          }
        }
      }
    }
  }
  assert.ok(worked >= 10, `合成的城里有人在门前干活（${worked}）`);
  assert.ok(stations >= 4, `门前摆着工位（${stations}）`);
});

test('crafts: several people behind one door — stations never cover anyone\'s spot or each other, whoever was planned first', () => {
  const { world, layout } = buildCity();
  const door = [...world.doors.values()][0];
  const make = (seed) => {
    const trade = ['potter', 'baker', 'mason', 'carpenter'][seed % 4];
    const craft = craftFor(trade, seed);
    return planCitizen({ id: `c${String(seed).padStart(3, '0')}`, seed, home: door.building, craft: { ...craft, site: 'home', scale: 1 } }, {
      sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite: has(layout), neighbours: [], doorFor: (s, sd) => world.doorFor(s, sd),
    });
  };
  const plans = Array.from({ length: 6 }, (_, i) => make(100 + i * 7));
  const kept = settleStations(plans);
  const forward = plans.map((p) => p.craft?.station ? `${p.id}:${p.craft.station.kind}` : `${p.id}:-`);
  // 换个次序再收一遍：结果一样（只看整份名单）
  const again = Array.from({ length: 6 }, (_, i) => make(100 + i * 7));
  settleStations([...again].reverse());
  assert.deepEqual(again.map((p) => p.craft?.station ? `${p.id}:${p.craft.station.kind}` : `${p.id}:-`), forward, '和谁先排日程无关');
  const placed = plans.filter((p) => p.craft?.station);
  assert.equal(placed.length, kept);
  for (const p of placed) {
    for (const o of plans) if (o !== p) assert.ok(!inStation(p.craft.station, o.home.point, 0.3), `${p.id} 的工位不压在 ${o.id} 站的地方`);
    for (const o of placed) if (o !== p) assert.ok(Math.hypot(o.craft.station.x - p.craft.station.x, o.craft.station.z - p.craft.station.z) > 0.5, '两件工位不挨着');
  }
  for (const p of plans.filter((q) => q.craft?.proposed && !q.craft.station)) {
    assert.equal(p.craft.atHome, false, `${p.id}：没收下工位的在门前闲着`);
    const s = citizenAt(p, TOWN.insideMs + 3000 - p.offset);
    assert.equal(s.action, 'idle');
  }
});

test('crafts: away from home — the fisher fishes only from a spot the world gives him, the musician plays facing the agora (citizens rule 7)', () => {
  const { world, layout } = buildCity();
  // 离港口（乐师：离广场）最近的一座建筑——一程最多走 30 秒，远处的人去不了
  const nearestTo = (q) => [...layout.plots.keys()].filter((k) => k.startsWith('b:'))
    .sort((a, b) => Math.hypot(layout.sitePoint(a).x - q.x, layout.sitePoint(a).z - q.z) - Math.hypot(layout.sitePoint(b).x - q.x, layout.sitePoint(b).z - q.z))[0];
  const pierSpot = { x: layout.harborPoint.x - 1.05, z: layout.quayZ + 6.8, yaw: -Math.PI / 2, via: [{ x: layout.harborPoint.x, z: layout.quayZ + 6.8 }] };
  const plan = (trade, seed, workSpot) => {
    const craft = craftFor(trade, seed);
    const site = nearestTo(trade === 'fisher' ? layout.harborPoint : layout.sitePoint('agora'));
    return planCitizen({ id: `${trade}-${seed}`, seed, home: site, craft: { ...craft, site: craftSite(craft, seed), scale: 1 } }, {
      sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite: has(layout), neighbours: [], doorFor: (s, sd) => world.doorFor(s, sd), workSpot,
    });
  };
  // 去处待着的那一段：周期里 homeMs + 一程 之后（有门的家在周期头尾另有屋里的那一段，不影响中间）
  const stayAt = (p) => citizenAt(p, TOWN.homeMs + p.walkMs + 2000 - p.offset);
  let fished = 0;
  for (let seed = 1; seed < 60 && fished < 2; seed++) {
    const p = plan('fisher', seed, (s) => (s === 'harbor' ? pierSpot : null));
    if (p.dest?.site !== 'harbor') continue;
    fished++;
    assert.deepEqual({ x: p.dest.point.x, z: p.dest.point.z }, { x: pierSpot.x, z: pierSpot.z }, '站在世界给的那一点上');
    assert.ok(routeClear(p.out, layout.walkable), '走得过去');
    const s = stayAt(p);
    assert.equal(s.at, 'dest');
    assert.equal(s.action, 'work');
    assert.equal(s.work, 'fish');
    assert.equal(s.yaw, -Math.PI / 2, '朝着海');
    const none = plan('fisher', seed, () => null);
    assert.equal(none.craft?.atDest ?? false, false, '没有站位就不钓');
  }
  assert.ok(fished >= 1, '有渔夫走得到港口');
  let played = 0;
  for (let seed = 1; seed < 60 && played < 2; seed++) {
    const p = plan('musician', seed, () => null);
    if (!p.dest?.site.startsWith('agora')) continue;
    played++;
    const s = stayAt(p);
    assert.equal(s.action, 'work');
    assert.equal(s.work, 'lyre');
    const centre = layout.sitePoint('agora');
    const toCentre = Math.atan2(centre.x - s.x, centre.z - s.z);
    assert.ok(Math.abs(Math.atan2(Math.sin(s.yaw - toCentre), Math.cos(s.yaw - toCentre))) < 1e-9, '朝着广场中间');
  }
  assert.ok(played >= 1, '有乐师在广场上弹琴');
});

test('crafts: a public place out of reach — the musician and the merchant work at their own door; the fisher and the farmer never fish or hoe on the paving; at a public home only crafts without a station (citizens rule 7)', () => {
  const { world, layout } = buildCity();
  const far = 'b:杂物';   // 城的东北角：广场、橄榄林、港口都在一程（30 秒）以外
  const planFor = (trade, seed, home) => {
    const craft = craftFor(trade, seed);
    return planCitizen({ id: `${trade}-${seed}`, seed, home, craft: { ...craft, site: craftSite(craft, seed), scale: 1 } }, {
      sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite: has(layout), neighbours: [], doorFor: (s, sd) => world.doorFor(s, sd),
    });
  };
  for (const trade of ['musician', 'merchant']) {
    let n = 0;
    for (let seed = 1; seed < 40; seed++) {
      const k = craftFor(trade, seed);
      const p = planFor(trade, seed, far);
      if (p.dest?.site === craftSite(k, seed) || !p.craft) continue;   // 还是走到了；或者只能站在门阶上
      n++;
      assert.equal(p.craft.atHome, true, `${trade}：去处走不到，就在家门前干`);
      assert.equal(p.craft.station, null);
      const s = citizenAt(p, TOWN.insideMs + 3000 - p.offset);
      assert.equal(s.at, 'home');
      assert.equal(s.action, 'work');
      assert.equal(s.work, k.variant);
    }
    assert.ok(n > 0, `${trade}：有人在家门前干`);
  }
  for (let seed = 1; seed < 40; seed++) {
    assert.ok(!planFor('fisher', seed, far).craft?.atHome, '渔夫不在家门前钓鱼');
    assert.ok(!planFor('farmer', seed, far).craft?.atHome, '农夫不在门前的铺石上锄地');
    assert.ok(!planFor('farmer', seed, 'agora:2').craft?.atHome, '住在广场上的农夫也不在广场上锄地');
    assert.ok(!planFor('herder', seed, far).craft?.atHome, '牧人不在门前看羊');
  }
  // 住在公共地方：纺线（不要工位）照干，拉坯（要陶轮）不干
  let spun = 0;
  for (let seed = 1; seed < 20; seed++) {
    if (planFor('spinner', seed, 'agora:2').craft?.atHome) spun++;
    assert.ok(!planFor('weaver', seed, 'agora:2').craft?.atHome, '公共地方不摆织机');
    assert.ok(!planFor('potter', seed, 'agora:2').craft?.atHome, '公共地方不摆陶轮');
  }
  assert.ok(spun > 10, `住在广场上的纺纱工在那儿纺线（${spun}）`);
});

test('crafts: a station never sits on the way to the door — the worker and his station step out from the wall until it clears (citizens rule 7)', () => {
  // 一面墙在 z ≤ 0.3；门在原点朝 +z，门阶 (0, 0.55)，门前的路先沿着墙往 +x 走到 2.5 再出去——站在门阶 +x 那一边的人，
  // 工位正落在这条路上，要往外让到工位不碰它
  const walkable = (q) => q.z > 0.3 && Math.abs(q.x) < 8 && q.z < 8;
  const door = { id: 'd', x: 0, z: 0, nx: 0, nz: 1, sill: 0, step: { x: 0, z: 0.55 }, inside: { x: 0, z: -0.7 }, approach: [{ x: 0, z: 0.55 }, { x: 2.5, z: 0.55 }, { x: 2.5, z: 4 }] };
  const layout = {
    sitePoint: () => ({ x: 2.5, z: 4 }), route: () => { throw new Error('nowhere'); }, walkable, hasSite: (x) => x === 'b:x', neighbours: [], doorFor: () => door,
  };
  const onApproach = (st) => {
    for (let k = 1; k < door.approach.length; k++) {
      const a = door.approach[k - 1], b = door.approach[k];
      for (let i = 0; i <= 40; i++) if (inStation(st, { x: a.x + (b.x - a.x) * i / 40, z: a.z + (b.z - a.z) * i / 40 }, 0.2)) return true;
    }
    return inStation(st, door.step, 0.3);
  };
  let placed = 0, shifted = 0;
  for (let seed = 1; seed <= 30; seed++) {
    const craft = craftFor('baker', seed);
    const plan = planCitizen({ id: `b${seed}`, seed, home: 'b:x', craft: { ...craft, site: 'home', scale: 1 } }, layout);
    const st = plan.craft?.station;
    if (!st) continue;
    placed++;
    assert.ok(!onApproach(st), `种子 ${seed}：工位不在门阶到街上的路上`);
    if (plan.home.point.z > door.step.z + 0.5) shifted++;
  }
  assert.ok(placed >= 10, `摆得下（${placed}）`);
  assert.ok(shifted >= 1, `有人和工位一起往外让了（${shifted}）`);
});

test('crafts: settling stations — one covering a neighbour\'s spot is not placed and its owner stands idle; two that would touch keep the first by id', () => {
  const trough = (x, z) => ({ kind: 'trough', x, z, yaw: Math.PI / 2, scale: 1 });
  const plan = (id, home, proposed) => ({ id, home: { point: home }, craft: { variant: 'knead', atHome: true, atDest: false, homeYaw: Math.PI / 2, destYaw: null, station: proposed, proposed } });
  // a 的揉面台（正中 1.42，朝 +x，前后 ±0.25）压着 b 站的地方（1.6）；b 的台子在 2.02，和 a 的那一件挨着
  const a = plan('a', { x: 1, z: 0.5 }, trough(1.42, 0.5));
  const b = plan('b', { x: 1.6, z: 0.5 }, trough(2.02, 0.5));
  const c = plan('c', { x: 5, z: 3 }, trough(5.42, 3));
  assert.equal(settleStations([c, b, a]), 2);
  assert.equal(a.craft.station, null, 'a 的台子压着 b 站的地方：不摆');
  assert.equal(a.craft.atHome, false, 'a 在门前闲着');
  assert.ok(b.craft.station && c.craft.station, 'b、c 的照摆');
  // 两件挨着的：按 id 收第一件
  const d = plan('d', { x: 0, z: 9 }, trough(0.42, 9));
  const e = plan('e', { x: 0.2, z: 10.2 }, trough(0.62, 9.6));
  settleStations([e, d]);
  assert.ok(d.craft.station && !e.craft.station, '挨着的两件只收 id 小的那件');
  // 名单变了再收一遍：没收下的那件回来
  settleStations([e]);
  assert.ok(e.craft.station && e.craft.atHome, '名单变了，没收下的那件回来');
});

test('countryside (fourth round, need 5): the fields and the pasture sit on the northern plot row; the farmer hoes in a furrow facing along it, the herder stands inside the pen through its gate, and the sheep stay in the pen', () => {
  const { world, layout } = buildCity();
  const fields = layout.country.filter((c) => c.country === 'field');
  const pen = layout.country.find((c) => c.country === 'pasture');
  assert.ok(pen && fields.length >= 2, `城北有一块牧场、几块农田（${fields.length}）`);
  for (const site of layout.country) {
    assert.equal(site.z, -layout.ring - 1, '在城外最北一排地块');
    assert.ok(!layout.plots.has(`b:${site.x},${site.z}`), '不占文件夹的地');
    assert.ok(site.cz - PLOT / 2 >= layout.mapRect.minZ + 1, '整块在地图里（地图往北多留了一条）');
  }
  assert.equal(world.fields.length, fields.length, '每块农田都盖了');
  assert.ok(world.sheep.length >= 5 && world.sheep.length <= 7, `圈里 5–7 只羊（${world.sheep.length}）`);
  // 农田不挡路（庄稼在人身高度带里），羊圈的栅栏挡路、圈门不挡
  for (const f of fields) for (const [dx, dz] of [[0, 0], [2, furrowZ(2)], [-3, furrowZ(4)]]) assert.ok(layout.walkable({ x: f.cx + dx, z: f.cz + dz }), '田里能走');
  assert.ok(!layout.walkable({ x: pen.cx + PEN.hw, z: pen.cz }), '东边的栅栏挡路');
  assert.ok(!layout.walkable({ x: pen.cx - 2.5, z: pen.cz + PEN.hd }), '南边的栅栏挡路');
  assert.ok(layout.walkable({ x: pen.cx, z: pen.cz + PEN.hd }), '圈门能走');
  let farmers = 0, herders = 0;
  for (let seed = 1; seed < 40; seed++) {
    const f = countrySpot(layout, 'fields', seed);
    if (f) {
      farmers++;
      assert.ok(Math.abs(Math.abs(f.yaw) - Math.PI / 2) < 1e-9, '面朝垄的方向（沿 x）');
      const field = fields.find((x) => Math.abs(x.cx - f.via[0].x) < 1e-9);
      assert.ok(field, '先走到一块田的南沿');
      assert.ok(Array.from({ length: FIELD.rows }, (_, k) => field.cz + furrowZ(k)).some((z) => Math.abs(z - f.z) < 1e-9), '站在一条垄沟里');
      const blade = { x: f.x + Math.sin(f.yaw) * 0.85, z: f.z + Math.cos(f.yaw) * 0.85 };
      assert.ok(Math.abs(blade.x - field.cx) < FIELD.w / 2 && Math.abs(blade.z - field.cz) < FIELD.d / 2, '锄刃落在田里');
      assert.ok(routeClear([field.stand, ...f.via, f], layout.walkable, 0.1), '从田南沿走进去一路能走');
    }
    const h = countrySpot(layout, 'pasture', seed);
    if (h) {
      herders++;
      assert.ok(Math.abs(h.x - pen.cx) < PEN.hw && Math.abs(h.z - pen.cz) < PEN.hd, '站在圈里');
      assert.equal(h.yaw, Math.PI, '面朝圈里');
      assert.ok(routeClear([pen.stand, ...h.via, h], layout.walkable, 0.1), '从圈门走进去');
    }
  }
  assert.ok(farmers >= 30 && herders >= 30, `站位几乎总有（农夫 ${farmers}、牧人 ${herders}）`);
  // 羊：同一刻同一个样子；始终在圈里、离栅栏有距离；有走有吃草
  let walking = 0, grazing = 0;
  for (const s of world.sheep) {
    assert.deepEqual(sheepAt(s.seed, 4321), sheepAt(s.seed, 4321));
    for (let wall = 0; wall < 600_000; wall += 1777) {
      const a = sheepAt(s.seed, wall);
      assert.ok(Math.abs(a.x) <= PEN.hw - 0.5 && Math.abs(a.z) <= PEN.hd - 0.5, `羊在圈里（${a.x.toFixed(2)}, ${a.z.toFixed(2)}）`);
      if (a.walking) walking++; else if (a.graze > 0.5) grazing++;
    }
  }
  assert.ok(walking > 50 && grazing > 50, `有走有吃草（走 ${walking}、吃草 ${grazing}）`);
  world.stepSheep(1234);
  assert.ok(world.sheep.every((s) => Math.hypot(s.root.position.x - pen.cx, s.root.position.z - pen.cz) < 6), 'stepSheep 把羊摆进圈里');
});
