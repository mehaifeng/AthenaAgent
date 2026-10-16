// 居民即会话（M-B，2026-10-13）：长相（art/citizens.mjs）与城里的日子（citizens.mjs）。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import { citizenLook, isFemale, lookSignature, CITIZEN_VOCAB, RESIDENT_SIGNATURES, MARKET_PROPS, CLOTH_SHADES_RGB, WORK_TRADES } from '../../../../Assets/Polis/src/art/citizens.mjs';
import { craftFor } from '../../../../Assets/Polis/src/art/crafts.mjs';
import { buildCitizen, CHARACTER_SPECS } from '../../../../Assets/Polis/src/art/characters.mjs';
import { HAIR_STYLES } from '../../../../Assets/Polis/src/art/rig.mjs';
import { bust, CHARACTERS } from '../../../../Assets/Polis/src/art/busts.mjs';
import {
  TOWN, DESTINATIONS, planCitizen, planTiming, routeClear, citizenAt, newScheduleClock, scheduleClock, holdSchedule, resumeSchedule, separate, assignTiers, pickChat, nextChatAfter, greetLine,
  retireTime, wakeTime, asleepAt, indoorsAt,
} from '../../../../Assets/Polis/src/citizens.mjs';
import { createLayout } from '../../../../Assets/Polis/src/layout.mjs';
import { createAssetLibrary } from '../../../../Assets/Polis/src/art/assets.mjs';
import { buildWorld } from '../../../../Assets/Polis/src/world.mjs';
import { demoAccumulation, demoCitizens } from '../../../../Assets/Polis/src/demo.mjs';
import { CITIZEN_ZH, UI_ZH, citizenInfoLines, speechAlpha, thinkingDots, SPEECH } from '../../../../Assets/Polis/src/game.mjs';
import { parseMarkdown } from '../../../../Assets/Polis/src/reader.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));
/**
 * 测试用的种子：C# 给的是会话 Id 的 SHA-256 前四个字节，在 32 位上是均匀的；这里用 murmur3 的 fmix32 把 i 打散成同样均匀的数。
 * （第一版用的 imul(i + 1, …) ^ (i * 40503) 最低位恒为 1——四百位居民全是女子，多样性只有一半的词汇可用。）
 */
const seedOf = (i) => { let h = (i + 1) >>> 0; h ^= h >>> 16; h = Math.imul(h, 0x85ebca6b); h ^= h >>> 13; h = Math.imul(h, 0xc2b2ae35); h ^= h >>> 16; return h >>> 0; };

function stubMaterials() {
  const cache = new Map();
  return new Proxy({}, {
    get: (_, key) => {
      if (typeof key !== 'string') return undefined;
      if (!cache.has(key)) cache.set(key, key.endsWith('Shades') ? Array.from({ length: 12 }, (_, i) => new THREE.MeshStandardMaterial({ name: `${key}${i}` })) : new THREE.MeshStandardMaterial({ name: key }));
      return cache.get(key);
    },
  });
}

test('look: the sex is the seed\'s lowest bit, the same seed is always the same person, and nothing outside the citizens\' vocabulary is used', () => {
  for (let i = 0; i < 200; i++) {
    const seed = seedOf(i) >>> 0;
    const age = ['young', 'adult', 'old'][i % 3];
    const a = citizenLook(seed, { age }), b = citizenLook(seed, { age });
    assert.deepEqual(a, b, '同一个种子永远同一个人');
    const { spec, bust: bs } = a;
    assert.equal(spec.face.female, (seed & 1) === 1, `${seed}: 最低位是 1 的是女子（和 C# 的 PolisCitizens.IsFemale 一致）`);
    assert.equal(isFemale(seed), spec.face.female);
    const vocab = spec.face.female ? CITIZEN_VOCAB.female : CITIZEN_VOCAB.male;
    assert.ok(vocab.garments.includes(spec.garment), `${seed}: 衣式 ${spec.garment}`);
    assert.ok(CITIZEN_VOCAB.cloth.includes(spec.cloth));
    // 2026-10-16：第二种颜色和衣色不同；披外衣的人一定有；发色在五档里；短衣长只给希顿
    if (spec.trim != null) assert.ok(CITIZEN_VOCAB.cloth.includes(spec.trim) && spec.trim !== spec.cloth, `${seed}: 第二色 ${spec.trim} 不同于衣色 ${spec.cloth}`);
    if (spec.garment === 'himation') assert.ok(spec.trim != null, `${seed}: 外衣换一种颜色`);
    assert.ok(CITIZEN_VOCAB.hairTones.includes(spec.hairTone), `${seed}: 发色 ${spec.hairTone}`);
    if (spec.hem != null) assert.ok(spec.garment === 'chiton' && Object.values(CITIZEN_VOCAB.hems).includes(spec.hem), `${seed}: 衣长 ${spec.hem} 只给希顿`);
    if (age === 'young') assert.ok(!spec.belly, `${seed}: 年轻人没有肚腩`);
    assert.ok([null, undefined, ...vocab.headgear].includes(spec.headgear), `${seed}: 头饰 ${spec.headgear}`);
    assert.ok(spec.hair in HAIR_STYLES && spec.hair.startsWith('plain'), `${seed}: 发式 ${spec.hair} 是居民的通用发式`);
    if (spec.face.female) assert.ok(!spec.beard, '女子没有胡子');
    // 规则 2、3：十一位居民的招牌一样都不用；手里是一卷素卷轴、或者他那门手艺的家什（干活时才拿出来，art/crafts.mjs）
    assert.ok(!RESIDENT_SIGNATURES.headgear.includes(spec.headgear), `${seed}: 不戴居民的帽子 / 头冠 / 面纱`);
    assert.ok(!RESIDENT_SIGNATURES.garments.includes(spec.garment), `${seed}: 不穿传令官的斗篷`);
    const craftProps = craftFor(null, seed).props;
    // 市集（2026-10-15）：人人还带着一枚钱和一包货（买卖时才拿出来）
    for (const p of spec.props) assert.ok((p === 'scroll' || craftProps.includes(p) || MARKET_PROPS.includes(p)) && !RESIDENT_SIGNATURES.props.includes(p), `${seed}: 手里只有素卷轴、他那门手艺的家什、买卖用的钱和货：${p}`);
    assert.deepEqual(spec.props.filter((p) => p !== 'scroll' && !MARKET_PROPS.includes(p)), [...craftProps], `${seed}: 他那门手艺的家什都在`);
    assert.deepEqual(spec.props.filter((p) => MARKET_PROPS.includes(p)), [...MARKET_PROPS], `${seed}: 买卖用的钱和货都在`);
    assert.ok(!spec.carry || spec.carry.prop === 'scroll', `${seed}: 走路时夹着的只有卷轴`);
    assert.ok(!spec.glow && !spec.gold && !spec.accentGift, '没有雅典娜的光、侍女的金、红色的成果');
    // 半身像的参数是 busts.mjs 认得的那几种
    assert.ok(['short', 'curly', 'receding', 'long', 'bun'].includes(bs.hair), `${seed}: 半身像发式 ${bs.hair}`);
    assert.equal(bs.female, spec.face.female);
    assert.equal(bs.age, age);
  }
});

test('look: any 32 consecutive seeds give at least 30 different looks — nobody in a city shares a face', () => {
  const sigs = Array.from({ length: 400 }, (_, i) => lookSignature(citizenLook(seedOf(i) >>> 0)));
  let worst = Infinity;
  for (let i = 0; i + 32 <= sigs.length; i++) worst = Math.min(worst, new Set(sigs.slice(i, i + 32)).size);
  assert.ok(worst >= 30, `最差的一段 32 位里有 ${worst} 种长相`);
});

test('look: a citizen builds on the real rig and draws as a bust, and residents keep their own hairstyles', () => {
  const M = stubMaterials();
  for (let i = 0; i < 12; i++) {
    const look = citizenLook(seedOf(i) >>> 0, { age: ['young', 'adult', 'old'][i % 3] });
    const figure = buildCitizen(M, look.spec);
    assert.ok(figure.root.userData.headTop, '人物有头顶的锚点（头顶的话按它投影）');
    figure.pose('walk', 200, { clock: 1000, cloth: false });
    assert.equal(figure.clothFrozen, true, 'cloth: false 时布料停着（远处的居民）');
    const hidden = figure.setDetail(false);
    assert.ok(hidden >= 20, `远处藏起手指和脚趾：${hidden} 个网格`);
    assert.equal(figure.setDetail(true), 0);
    figure.pose('walk', 216, { clock: 1016 });
    assert.equal(figure.clothFrozen, false, '走近了布料接着推进');
    const b = bust(look.bust, 'speak');
    assert.ok(b.paths.length > 20, '半身像画得出来');
  }
  for (const id of Object.keys(CHARACTER_SPECS)) assert.ok(!CHARACTER_SPECS[id].hair.startsWith('plain'), `${id} 用自己的发式`);
  assert.ok(Object.keys(CHARACTERS).every((id) => !id.startsWith('citizen')));
});

test('look: clothes follow the trade, bodies vary, and every colour, hairstyle and hair colour turns up (2026-10-16)', () => {
  // 用户：扩充服装多样性、发型多样性、身形多样性。第一版一排十六位里十位是灰蓝、六位是米色，人人一身长衣
  const men = (trade) => Array.from({ length: 600 }, (_, i) => citizenLook((seedOf(i) & ~1) >>> 0, { age: ['young', 'adult', 'old'][i % 3], trade }).spec);
  const share = (list, ok) => list.filter(ok).length / list.length;
  const farmers = men('farmer'), merchants = men('merchant');
  const shortCoat = (sp) => sp.garment === 'exomis' || sp.hem === CITIZEN_VOCAB.hems.short;
  assert.ok(share(farmers, shortCoat) > 0.7, `下地的男子多穿短衣（${share(farmers, shortCoat).toFixed(2)}）`);
  assert.ok(share(merchants, (sp) => !shortCoat(sp)) > 0.6, `商贩多穿长衣、披外衣（${share(merchants, (sp) => !shortCoat(sp)).toFixed(2)}）`);
  assert.ok(share(merchants, (sp) => sp.garment === 'himation') > 0.3, '商贩里披外衣的多');
  assert.ok(WORK_TRADES.includes('farmer') && !WORK_TRADES.includes('merchant'));
  // 身高、体格、肚腩、仪态的范围（规则 6）
  const all = Array.from({ length: 900 }, (_, i) => { const age = ['young', 'adult', 'old'][i % 3]; return { age, spec: citizenLook(seedOf(i), { age }).spec }; });
  for (const { age, spec } of all) {
    const f = spec.face.female;
    assert.ok(spec.height >= (f ? 1.48 : 1.56) && spec.height <= (f ? 1.76 : 1.9), `身高 ${spec.height}`);
    assert.ok(spec.build >= (f ? 0.86 : 0.88) && spec.build <= (f ? 1.06 : 1.16), `体格 ${spec.build}`);
    if (spec.belly) assert.ok(spec.belly >= 0.35 && spec.belly <= 1 && age !== 'young');
    if (age === 'old') assert.ok(spec.bearing.spine >= 0.04 && spec.bearing.head >= 0.1, '老人弯腰低头');
    else assert.ok(Math.abs(spec.bearing.spine) <= 0.025, '别人站得直');
  }
  const heights = all.filter((x) => !x.spec.face.female).map((x) => x.spec.height);
  assert.ok(Math.max(...heights) - Math.min(...heights) > 0.22, `男子最高和最矮差 ${(Math.max(...heights) - Math.min(...heights)).toFixed(2)} 米`);
  const grown = all.filter((x) => x.age !== 'young');
  const bellied = share(grown, (x) => !!x.spec.belly);
  assert.ok(bellied > 0.18 && bellied < 0.45, `成年人和老人里有肚腩的 ${bellied.toFixed(2)}`);
  // 词汇里的每一样都用得上
  const seen = (key) => new Set(all.map((x) => x.spec[key]));
  for (const c of CITIZEN_VOCAB.cloth) assert.ok(seen('cloth').has(c), `衣色 ${c} 有人穿`);
  for (const h of [...CITIZEN_VOCAB.female.hair, ...CITIZEN_VOCAB.male.hair, CITIZEN_VOCAB.oldMaleHair]) assert.ok(seen('hair').has(h), `发式 ${h} 有人留`);
  for (const t of new Set(CITIZEN_VOCAB.hairTones)) assert.ok(seen('hairTone').has(t), `发色 ${t}`);
  for (const g of ['headband', 'wreath']) assert.ok(seen('headgear').has(g), `头饰 ${g}`);
  assert.ok(seen('hairMat').has('pepperHair') && seen('hairMat').has('greyHair'), '老人有花白的也有灰白的');
  assert.ok(seen('hem').has(CITIZEN_VOCAB.hems.calf) && seen('hem').has(CITIZEN_VOCAB.hems.short), '短希顿与到小腿的希顿都有人穿');
  // 衣色都不是红（红只属于雅典娜）：栗色、赭色这些褐色可以，红分量比绿、蓝都高出 0.2 的不行
  for (const [r, g, b] of CLOTH_SHADES_RGB) assert.ok(r - Math.max(g, b) < 0.2, `衣色 ${[r, g, b]} 不是红（红比绿、蓝都高出 0.2 以上）`);
  // 最亮的一档仍比雅典娜的白衣暗（明度不过 0.76）
  for (const [r, g, b] of CLOTH_SHADES_RGB) assert.ok(0.2126 * r + 0.7152 * g + 0.0722 * b <= 0.76, `衣色 ${[r, g, b]} 比雅典娜暗`);
  // 半身像与城里的人物同一身衣服：职业传进来，衣式一样；发色有墨色
  for (let i = 0; i < 60; i++) {
    const look = citizenLook(seedOf(i), { age: 'adult', trade: i % 2 ? 'farmer' : 'merchant' });
    assert.equal(look.bust.garment, look.spec.garment);
    assert.ok(/^#[0-9a-f]{6}$/.test(look.bust.hairInk), '半身像的发色');
    assert.ok(bust(look.bust, 'speak').paths.length > 20);
  }
  assert.ok(bust({ ...citizenLook(seedOf(3), { age: 'adult' }).bust, headgear: 'wreath' }, 'speak').paths.length > 30, '半身像画得出花环');
});

test('look: a carried scroll hangs at the hip, angled down, not held up in front like a torch (2026-10-16)', () => {
  // 第一版前臂端在胸前、卷轴竖着立在拳上：顶到肩高、离身子 0.44 米，远看像举着一支火把
  const M = stubMaterials();
  const people = [];
  for (let i = 0; people.length < 6 && i < 200; i++) {
    const spec = citizenLook(seedOf(i), { age: ['young', 'adult', 'old'][i % 3] }).spec;
    if (spec.carry) people.push(spec);
  }
  people.push(CHARACTER_SPECS.librarian);
  assert.ok(people.length >= 5);
  const w = (o) => o.getWorldPosition(new THREE.Vector3());
  for (const spec of people) {
    const f = buildCitizen(M, spec);
    for (const action of ['idle', 'walk']) {
      for (let c = 0; c < 30; c++) f.pose(action, 1200 + c * 16, { clock: 1e6 + c * 16 });
      f.root.updateMatrixWorld(true);
      const pts = [];
      f.props.get('scroll').traverse((o) => {
        if (!o.isMesh) return;
        const a = o.geometry.attributes.position;
        for (let k = 0; k < a.count; k++) pts.push(new THREE.Vector3().fromBufferAttribute(a, k).applyMatrix4(o.matrixWorld));
      });
      let ends = [pts[0], pts[0]], far = 0;
      for (const a of pts) for (const b of pts) { const d = a.distanceToSquared(b); if (d > far) { far = d; ends = [a, b]; } }
      const top = Math.max(...pts.map((q) => q.y)), front = Math.max(...pts.map((q) => q.z));
      const hips = w(f.joints.hips).y, hand = w(f.joints.handR);
      const axis = ends[1].clone().sub(ends[0]).normalize();
      const who = `${spec.id ?? spec.hair} ${action}`;
      assert.ok(hand.y < hips + 0.1, `${who}: the arm hangs — the fist is at the hip (${hand.y.toFixed(2)} vs hips ${hips.toFixed(2)})`);
      assert.ok(top < hips + 0.15, `${who}: no part of the scroll rises toward the chest (${top.toFixed(2)})`);
      assert.ok(front < 0.3, `${who}: it is not thrust out in front (${front.toFixed(2)})`);
      assert.ok(Math.abs(axis.y) < 0.8, `${who}: the scroll is not stood upright in the fist (|y| ${Math.abs(axis.y).toFixed(2)})`);
      assert.ok(hand.x < -0.15, `${who}: the fist is beside the body, not in front of it (${hand.x.toFixed(2)})`);
    }
  }
});

test('look: on the real rig the belly bulges forward over the belt, not into the arms; the second colour is the mantle and the belt; the stoop straightens for work', () => {
  const M = stubMaterials();
  const base = { ...citizenLook((seedOf(5) & ~1) >>> 0, { age: 'adult', trade: 'merchant' }).spec, garment: 'himation', trim: 7, build: 1, height: 1.75 };
  const extent = (spec) => {
    const f = buildCitizen(M, spec);
    f.root.updateMatrixWorld(true);
    const g = f.joints.spine.getObjectByName('upperGarment');
    const p = g.geometry.attributes.position, v = new THREE.Vector3();
    let front = 0, side = 0;
    for (let i = 0; i < p.count; i++) {
      v.fromBufferAttribute(p, i);   // 脊柱的局部坐标（腰带在 1.095 − 0.98）
      if (Math.abs(v.y - (1.095 - 0.98)) > 0.06) continue;
      front = Math.max(front, v.z);
      if (Math.abs(v.z) < 0.03) side = Math.max(side, Math.abs(v.x));
    }
    const beltBox = new THREE.Box3().setFromBufferAttribute(f.joints.spine.getObjectByName('belt').geometry.attributes.position);
    return { f, front, side, beltFront: beltBox.max.z };
  };
  const flat = extent({ ...base, belly: undefined }), round = extent({ ...base, belly: 1 });
  assert.ok(round.front - flat.front > 0.035, `肚腩往前鼓 ${(round.front - flat.front).toFixed(3)} 米`);
  assert.ok(round.side - flat.side < 0.014, `两侧只多 ${(round.side - flat.side).toFixed(3)} 米（胳膊垂在那里）`);
  assert.ok(round.beltFront > round.front - 0.02 && round.beltFront - flat.beltFront > 0.03, '腰带跟着肚腩往前绕，不埋进衣服里');
  const sash = round.f.joints.spine.getObjectByName('sash');
  assert.equal(sash.material, M.clothShades[7], '外衣是第二种颜色');
  assert.equal(round.f.joints.spine.getObjectByName('belt').material, M.clothShades[7], '腰带也是');
  assert.notEqual(round.f.joints.spine.getObjectByName('upperGarment').material, M.clothShades[7]);
  // 外衣跟着肚腩往外让：埋进上衣 2.5 厘米以上的点（管子半粗 4.2 厘米，里侧本来就贴着衣服）不比没有肚腩时多
  const buried = (f) => {
    const sp = f.joints.spine.getObjectByName('sash').geometry.attributes.position, up = f.joints.spine.getObjectByName('upperGarment').geometry.attributes.position;
    const v = new THREE.Vector3(), w = new THREE.Vector3();
    let n = 0;
    for (let i = 0; i < sp.count; i += 3) {
      v.fromBufferAttribute(sp, i);
      if (v.z < 0.05) continue;
      let r = 0;
      for (let k = 0; k < up.count; k += 2) {
        w.fromBufferAttribute(up, k);
        if (Math.abs(w.y - v.y) < 0.012 && Math.abs(Math.atan2(w.x, w.z) - Math.atan2(v.x, v.z)) < 0.08) r = Math.max(r, Math.hypot(w.x, w.z));
      }
      if (r > 0 && Math.hypot(v.x, v.z) < r - 0.025) n++;
    }
    return n;
  };
  assert.ok(buried(round.f) <= buried(flat.f), `外衣埋进肚腩里的点 ${buried(round.f)}（没有肚腩时 ${buried(flat.f)}）`);
  // 弯腰（仪态）只在走和站时有：干活时上身是挺直的参照人（家什、柜台的接触点按它解）
  const old = { ...citizenLook((seedOf(5) & ~1) >>> 0, { age: 'old', trade: 'miller' }).spec, height: 1.75, build: 1 };
  const spineX = (spec, action, variant) => {
    const f = buildCitizen(M, spec);
    let clock = 0;
    for (let k = 0; k < 40; k++) { clock += 16; f.pose(action, clock, { variant, clock }); }
    return f.joints.spine.rotation.x;
  };
  const straight = { ...old, bearing: { head: old.bearing.head, spine: 0 } };
  assert.ok(spineX(old, 'idle') - spineX(straight, 'idle') > 0.035, '站着时老人上身往前倾');
  assert.ok(Math.abs(spineX(old, 'work', 'grind') - spineX(straight, 'work', 'grind')) < 0.002, '干活时直起来（杵头落在钵底）');
});

test('look: a citizen first posed far from where it was built wears its skirt on its own hips — the cloth is resettled, not dragged in from the origin', () => {
  const M = stubMaterials();
  let worst = 0;
  // 挪一两米最糟：挪得很远时布料自己按"瞬移"重新安放（cloth.mjs），挪一点点却会被一帧拽过来。旧规则下这一排里
  // 四位的裙摆甩到 0.6–1.1 米开外（审查图里看到的），新规则下都在 0.4 米以内
  for (let i = 0; i < 10; i++) {
    const look = citizenLook(((Math.imul(i + 1, 2654435761) & ~1) | (i & 1)) >>> 0, { age: ['young', 'adult', 'old'][i % 3] });
    const figure = buildCitizen(M, look.spec);
    if (figure.garments.length === 0) continue;
    // 和城里一样：建好之后才挪到他在城里的位置、转向，然后头一回摆姿势（全套，布料推进）
    figure.root.position.set((i - 4.5) * 0.95, 0, 0);
    figure.root.rotation.y = 0.35;
    figure.pose('idle', 1200 + i * 777, { clock: 1_000_000 + i * 777 });
    figure.root.updateMatrixWorld(true);
    const v = new THREE.Vector3();
    for (const g of figure.garments) {
      const attr = g.mesh.geometry.attributes.position;
      for (let k = 0; k < attr.count; k++) {
        v.fromBufferAttribute(attr, k).applyMatrix4(g.mesh.matrixWorld);
        worst = Math.max(worst, Math.hypot(v.x - figure.root.position.x, v.z - figure.root.position.z));
      }
    }
  }
  assert.ok(worst < 0.5, `裙摆离身体最远 ${worst.toFixed(2)} 米（甩出去的裙摆到一米开外）`);
});

function buildWorldFor(layout) {
  const M = stubMaterials();
  const world = buildWorld(new THREE.Scene(), fixture, layout, M, { assets: createAssetLibrary(M) });
  return { world };
}

function buildCity() {
  const M = stubMaterials();
  const assets = createAssetLibrary(M);
  const acc = demoAccumulation(fixture);
  const layout = createLayout({ ...fixture, groveTrees: acc.trees.length });
  const world = buildWorld(new THREE.Scene(), fixture, layout, M, { assets });
  world.setGrove(acc.trees);
  world.setResidents(acc.residents, (k) => k);
  const hasSite = (s) => s === 'agora' || s.startsWith('agora:') || s === 'harbor' || s === 'gate' || layout.plots.has(s);
  return { world, layout, hasSite };
}

test('day: every citizen of the synthetic city stands and walks only where you can stand (the whole route, not just its ends), fits each trip in his own period (no distance cap since 2026-10-15), and fewer than half are on the road at once', () => {
  const { layout, hasSite } = buildCity();
  const { street } = demoCitizens(fixture, 32);
  const neighbours = [...layout.plots.keys()].filter((k) => k.startsWith('b:'));
  const plans = street.map((c) => planCitizen(c, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours }));
  assert.ok(plans.filter((p) => p.dest).length >= 24, `大多数居民有一处去处：${plans.filter((p) => p.dest).length}/32`);
  for (const p of plans) {
    assert.ok(layout.walkable(p.home.point), `${p.id} 的家门口能站`);
    if (p.dest) {
      assert.ok(DESTINATIONS.includes(p.dest.site) || neighbours.includes(p.dest.site), p.dest.site);
      assert.notEqual(p.dest.site, p.home.site, '不去自己家');
      assert.ok(layout.walkable(p.dest.point), `${p.id} 的去处能站`);
      // 规则 1：路多远都去，周期跟着这一趟定——一趟装得进他自己的周期，一来一回不超过 walkShare
      assert.deepEqual({ periodMs: p.periodMs, stayMs: p.stayMs }, planTiming(p.walkMs), `${p.id}：周期按这一程定`);
      assert.ok(TOWN.homeMs + 2 * p.walkMs + p.stayMs + TOWN.tailMs <= p.periodMs + 1e-6, `${p.id}：一趟装得进周期（${Math.round(p.walkMs)} 毫秒一程，周期 ${p.periodMs}）`);
      assert.ok(2 * p.walkMs <= TOWN.walkShare * p.periodMs + 1e-6, `${p.id}：一来一回不超过周期的 ${TOWN.walkShare}`);
      assert.ok(routeClear(p.out, layout.walkable, 0.1), `${p.id} 去 ${p.dest.site} 的整条路每 0.1 米都能站`);
    }
  }
  let worstRoad = 0, blocked = 0, samples = 0;
  const longest = Math.max(...plans.map((p) => p.periodMs));
  for (let wall = 0; wall < longest * 2; wall += 997) {
    let road = 0;
    for (const p of plans) {
      const s = citizenAt(p, wall);
      samples++;
      if (!layout.walkable(s)) blocked++;
      if (s.action === 'walk') road++;
    }
    worstRoad = Math.max(worstRoad, road);
  }
  assert.equal(blocked, 0, `每一刻都站在能站的地方（${samples} 个取样里有 ${blocked} 个不能站）`);
  assert.ok(worstRoad <= Math.ceil(plans.length / 2), `同一刻走着的最多 ${worstRoad} 位（不超过一半）`);
  // 同一刻永远是同一个位置；日程与墙钟对齐、按种子错开
  assert.deepEqual(citizenAt(plans[0], 12345), citizenAt(plans[0], 12345));
  assert.ok(new Set(plans.map((p) => p.offset)).size > 24, '节拍按种子错开');
});

test('held (2026-10-13): a walker stopped for a talk resumes from where he stopped — the time he stood is taken off his own schedule, so he never races to where the wall clock says he should be', () => {
  const { layout, hasSite } = buildCity();
  const { street } = demoCitizens(fixture, 32);
  const neighbours = [...layout.plots.keys()].filter((k) => k.startsWith('b:'));
  const plan = street.map((c) => planCitizen(c, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours })).find((p) => p.dest);
  // 找一刻他正走在去处的半路上
  let wall = 0;
  while (citizenAt(plan, wall).action !== 'walk' || citizenAt(plan, wall + 3000).action !== 'walk') wall += 250;
  const clock = newScheduleClock();
  assert.equal(scheduleClock(clock, wall), wall, '没站定过：日程钟就是墙钟');
  const before = citizenAt(plan, scheduleClock(clock, wall));
  holdSchedule(clock, wall);
  holdSchedule(clock, wall + 5000);   // 已经停着：不重新计时
  const heldFor = 20_000;
  assert.deepEqual(citizenAt(plan, scheduleClock(clock, wall + heldFor)), before, '站定期间日程不走');
  resumeSchedule(clock, wall + heldFor);
  const after = citizenAt(plan, scheduleClock(clock, wall + heldFor));
  assert.ok(Math.hypot(after.x - before.x, after.z - before.z) < 1e-9, '松开的那一刻还在站定的地方');
  // 松开之后一帧（16 ms）挪的距离是走路的速度，不是去追墙钟（旧做法：墙钟上的位置在 20 秒后，差出十几米）
  const next = citizenAt(plan, scheduleClock(clock, wall + heldFor + 16));
  assert.ok(Math.hypot(next.x - after.x, next.z - after.z) <= TOWN.speed * 0.016 * 1.6 + 1e-6, '松开后按走路的速度接着走');
  const wallClockSpot = citizenAt(plan, wall + heldFor);
  assert.ok(Math.hypot(wallClockSpot.x - before.x, wallClockSpot.z - before.z) > 2, `这个用例真的测到了差别：墙钟上的位置离站定处 ${Math.hypot(wallClockSpot.x - before.x, wallClockSpot.z - before.z).toFixed(1)} 米`);
  resumeSchedule(clock, wall + heldFor + 1000);   // 没站定：不动
  assert.equal(clock.shift, heldFor);
});

test('cottages (fourth round; M-D\'s houses): a cottage carries its family\'s name, not its key, and a citizen whose home is that cottage is planned from it', () => {
  // 一块没人用的地：在合成城邦的第一、二圈里找（不是文件夹的、不是公共建筑的、不是空地、不在海里）
  const used = new Set([...fixture.buildings.map((b) => `${b.plot.x},${b.plot.z}`), ...Object.values(fixture.publicSites).map((p) => `${p.x},${p.z}`), ...(fixture.vacant ?? []).map((v) => `${v.plot.x},${v.plot.z}`)]);
  let plot = null;
  for (let r = 1; r <= 3 && !plot; r++) {
    for (let x = -r; x <= r && !plot; x++) {
      for (let z = -r; z <= r && !plot; z++) {
        if (Math.max(Math.abs(x), Math.abs(z)) !== r || z >= fixture.seaStartsAtZ || used.has(`${x},${z}`)) continue;
        plot = { x, z };
      }
    }
  }
  assert.ok(plot, '找得到一块空地');
  const house = { key: 'household:abcd1234', label: '菲莉丝一家', kind: 'house', files: 3, bytes: 0, kinds: {}, lastModified: '2026-10-13T00:00:00Z', state: 'lived', sizeClass: 2, incomplete: false, plot, order: 999 };
  const city = { ...fixture, buildings: [...fixture.buildings, house] };
  const M = stubMaterials();
  const assets = createAssetLibrary(M);
  const acc = demoAccumulation(city);
  const layout = createLayout({ ...city, groveTrees: acc.trees.length });
  const world = buildWorld(new THREE.Scene(), city, layout, M, { assets });
  const label = world.labels.find((l) => l.key === 'b:household:abcd1234');
  assert.ok(label, '民居有名牌');
  assert.equal(label.text, '菲莉丝一家', '名牌写户名，不写钥匙');
  assert.ok(world.labels.filter((l) => l.kind === 'building').every((l) => !l.text.startsWith('household:')), '没有一块名牌露出钥匙');
  assert.ok(layout.plots.has('b:household:abcd1234'), '民居是一块地，站点 b:household:…');
  const hasSite = (site) => site === 'agora' || site.startsWith('agora:') || site === 'harbor' || site === 'gate' || layout.plots.has(site);
  const plan = planCitizen({ id: 'conv-x', seed: 12345, home: 'b:household:abcd1234' }, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours: [] });
  assert.equal(plan.home.site, 'b:household:abcd1234', '住在这一户的人从民居门前出门');
  assert.ok(layout.walkable(plan.home.point), '民居门前能站');
  // 民居还没盖起来（城还没推到）时只能先住广场：日程记下的家与他真正的家不同——城一变，页面据此重排（game.replanCitizens）
  const early = planCitizen({ id: 'conv-x', seed: 12345, home: 'b:household:ffff0000' }, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours: [] });
  assert.notEqual(early.home.site, 'b:household:ffff0000', '还没有那座民居：先住广场');
});

test('avoidance: overlapping people step apart, nobody is pushed off walkable ground, you are the stone they step around, and the nudge fades when no one is near', () => {
  const walkable = (p) => p.x > -10 && p.x < 10 && p.z > -10 && p.z < 10 && !(p.x > 2 && p.x < 3);
  let people = [
    { id: 'a', x: 0, z: 0, nudge: { x: 0, z: 0 }, movable: true },
    { id: 'b', x: 0.2, z: 0, nudge: { x: 0, z: 0 }, movable: true },
    { id: 'r', x: 0, z: 0.3, nudge: { x: 0, z: 0 }, movable: false },
  ];
  for (let k = 0; k < 40; k++) {
    const n = separate(people, null, walkable);
    people = people.map((p) => (p.movable ? { ...p, nudge: n.get(p.id) } : p));
  }
  const pos = (p) => ({ x: p.x + p.nudge.x, z: p.z + p.nudge.z });
  const [a, b, r] = people.map(pos);
  assert.ok(Math.hypot(a.x - b.x, a.z - b.z) >= TOWN.separation - 0.05, `两人让开：${Math.hypot(a.x - b.x, a.z - b.z).toFixed(2)} 米`);
  assert.deepEqual(people[2].nudge, { x: 0, z: 0 }, '站着不动的人（雅典娜、十一位居民）不被推');
  assert.ok(Math.hypot(a.x - r.x, a.z - r.z) > 0.3);
  // 墙边：往墙里推的那一步不走
  const atWall = [{ id: 'w', x: 1.95, z: 0, nudge: { x: 0, z: 0 }, movable: true }, { id: 'v', x: 1.7, z: 0, nudge: { x: 0, z: 0 }, movable: true }];
  for (let k = 0; k < 30; k++) {
    const n = separate(atWall, null, walkable);
    for (const p of atWall) p.nudge = n.get(p.id);
    for (const p of atWall) assert.ok(walkable(pos(p)), '让开的地方能站');
  }
  // 你：推不动，挡在你身边的人让开
  const crowd = [{ id: 'c', x: 0.1, z: 0, nudge: { x: 0, z: 0 }, movable: true }];
  for (let k = 0; k < 40; k++) crowd[0].nudge = separate(crowd, { x: 0, z: 0 }, walkable).get('c');
  assert.ok(Math.hypot(pos(crowd[0]).x, pos(crowd[0]).z) >= TOWN.playerRadius - 0.05, '你身边让出一圈');
  // 没人挤了：偏移慢慢回到 0
  const alone = [{ id: 'd', x: 5, z: 5, nudge: { x: 0.5, z: 0 }, movable: true }];
  for (let k = 0; k < 120; k++) alone[0].nudge = separate(alone, null, walkable).get('d');
  assert.ok(Math.hypot(alone[0].nudge.x, alone[0].nudge.z) < 0.01, '没人挤就回到原路');
});

test('tiers: out of view is C, the nearest four within 6 m are A, the rest B, and an A at the edge does not flicker', () => {
  const list = [
    { id: 'p1', distance: 1, inView: true }, { id: 'p2', distance: 2, inView: true }, { id: 'p3', distance: 3, inView: true },
    { id: 'p4', distance: 4, inView: true }, { id: 'p5', distance: 5, inView: true }, { id: 'far', distance: 20, inView: true },
    { id: 'off', distance: 0.5, inView: false },
  ];
  const t = assignTiers(list);
  assert.deepEqual(['p1', 'p2', 'p3', 'p4'].map((id) => t.get(id)), ['A', 'A', 'A', 'A']);
  assert.equal(t.get('p5'), 'B');
  assert.equal(t.get('far'), 'B');
  assert.equal(t.get('off'), 'C', '画面外的不摆，哪怕就在你身边');
  const lone = assignTiers([{ id: 'x', distance: 9, inView: true }]);
  assert.equal(lone.get('x'), 'B', '离你远过 6 米的，就算只有他一个也不是全套');
  // 滞回：p4 稍远一点（4.6）、p5 稍近一点（4.5），p4 仍然留在 A
  const t2 = assignTiers([...list.slice(0, 3), { id: 'p4', distance: 4.6, inView: true }, { id: 'p5', distance: 4.5, inView: true }], t);
  assert.equal(t2.get('p4'), 'A');
  assert.equal(t2.get('p5'), 'B');
});

test('small talk (2026-10-13: model only, no preset lines): one pair at a time, idle people near you and each other, a cooldown per pair, and a back-off when the model is missing or busy', () => {
  const people = [
    { id: 'a', x: 0, z: 0, idleLeft: 30_000, inView: true },
    { id: 'b', x: 1.5, z: 0, idleLeft: 30_000, inView: true },
    { id: 'c', x: 1.6, z: 0.3, idleLeft: 1000, inView: true },           // 马上要动身：不挑
    { id: 'd', x: 40, z: 0, idleLeft: 30_000, inView: true },            // 离你太远
  ];
  const cooldown = new Map();
  const talk = pickChat(people, { x: 0, z: 2 }, cooldown, 10_000);
  assert.deepEqual(talk, { a: 'a', b: 'b', key: 'a|b', start: 10_000 }, '挑中离得最近、都闲着的一对；只挑人，没有台词');
  cooldown.set(talk.key, 10_000);
  assert.equal(pickChat(people, { x: 0, z: 2 }, cooldown, 10_000 + 60_000), null, '这一对三分钟里不再说');
  assert.ok(pickChat(people, { x: 0, z: 2 }, cooldown, 10_000 + TOWN.chatPairCooldownMs + 1), '过了三分钟又能说');
  assert.equal(pickChat(people.map((p) => ({ ...p, idleLeft: TOWN.chatNeedMs - 1 })), { x: 0, z: 2 }, new Map(), 0), null, '正要动身的人不挑');
  // 结束之后多久再挑：没配模型就不再挑（没有本地台词可退）；超限五分钟；正忙半分钟；失败一分钟；说完照常
  assert.equal(nextChatAfter('nomodel', 0), Number.POSITIVE_INFINITY);
  assert.equal(nextChatAfter('limited', 0), 5 * 60_000);
  assert.equal(nextChatAfter('busy', 0), 30_000);
  assert.equal(nextChatAfter('failed', 0), 60_000);
  assert.equal(nextChatAfter('ok', 0), 500);
  assert.ok(TOWN.chatMaxMs >= 2 * 25_000, '一对最多聊这么久，够两句各 25 秒的超时');
  // 打招呼仍是本地的
  assert.ok(CITIZEN_ZH.greet.split('|').includes(greetLine(42, CITIZEN_ZH.greet, 0)), '打招呼的话来自表里');
  // 第三轮（人物关系）：喜欢你的人打招呼说热络的那一组；仍然只有打招呼的话，没有闲聊的本地台词
  // 市集（2026-10-15）：摊前的买卖是本地台词（market 规则 5）——只有 market 开头的那些，两位居民之间的闲聊仍然只用模型
  assert.deepEqual(Object.keys(CITIZEN_ZH).filter((k) => !k.startsWith('market')).sort(), ['greet', 'greetWarm'], '没有闲聊的本地台词了');
});

test('card (fourth round, 2026-10-15): the citizen\'s card says who he is — trade, age, temper, where he lives and whether he goes out at night; nothing of any conversation', () => {
  const lines = citizenInfoLines({ tradeName: '陶工', age: 'old', temper: '爽朗', homeName: 'src', stall: false, cottage: false, nightOwl: true }, UI_ZH);
  assert.deepEqual(lines, ['陶工 · 年长 · 性子爽朗', '住在「src」', UI_ZH.citizenInfoNightOwl]);
  assert.deepEqual(citizenInfoLines({ tradeName: '商贩', age: 'young', temper: '腼腆', homeName: 'notes', stall: true }, UI_ZH), ['商贩 · 年轻 · 性子腼腆', '在市集守着「notes」的摊位']);
  assert.deepEqual(citizenInfoLines({ tradeName: '牧人', age: 'adult', temper: '', homeName: '菲莉丝一家', cottage: true }, UI_ZH), ['牧人 · 壮年', '住在城郊的村舍（菲莉丝一家）']);
  assert.deepEqual(citizenInfoLines({}, UI_ZH), [], '没带的就不写');
  for (const key of ['outcome', 'turns', 'folders']) assert.ok(!Object.values(UI_ZH).some((v) => v.includes(`{${key}}`)), `卡片上再没有会话的 ${key}`);
});

test('night (fourth round, need 4): after dusk everyone but the night owls goes indoors at his next homecoming — within one period the street is the owls\' — and at dawn each comes out at his own next exit', () => {
  const { layout, hasSite } = buildCity();
  const { world } = buildWorldFor(layout);
  const { street } = demoCitizens(fixture, 32);
  const neighbours = [...layout.plots.keys()].filter((k) => k.startsWith('b:'));
  const plans = street.map((c) => ({ c, plan: planCitizen(c, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours, doorFor: (s, sd) => world.doorFor(s, sd) }) }));
  assert.ok(plans.some(({ plan }) => plan.door) && plans.some(({ plan }) => !plan.door), '有门的、没门的家都有');
  const dusk = 1_000_000;
  const night = (owl) => ({ night: true, since: dusk, owl });
  let goingHome = 0;
  for (const { c, plan } of plans) {
    const retire = retireTime(plan, dusk);
    if (!plan.door && !plan.dest) { assert.equal(retire, Number.POSITIVE_INFINITY, `${c.id}：没门、又哪儿都去不了的人整夜站在家门前（不当场消失）`); continue; }
    assert.ok(retire >= dusk && retire <= dusk + (plan.door ? 1 : 2) * plan.periodMs, `${c.id}：有门的最多一个周期、没门的最多两个周期就回了家（${retire - dusk} 毫秒）`);
    if (!plan.door && plan.dest) assert.ok(retire > dusk, `${c.id}：没门的人入夜那一刻不当场消失，走回家门前才回屋`);
    if (retire > dusk) {
      goingHome++;
      // 回家之前照着日程走，和白天一模一样（不瞬移）
      assert.deepEqual(citizenAt(plan, dusk + 1, night(false)), citizenAt(plan, dusk + 1));
    }
    const s = citizenAt(plan, retire + 1, night(false));
    assert.equal(s.hidden, true, `${c.id}：回家之后在屋里，不画`);
    assert.equal(s.at, 'inside');
    assert.equal(citizenAt(plan, dusk + 5 * plan.periodMs, night(false)).hidden, true, '整夜都在屋里');
    if (!plan.door && plan.dest) assert.deepEqual(citizenAt(plan, retire - 1, night(false)).at, 'road', `${c.id}：回屋前的那一刻正走在回家的路上`);
    // 夜游的人照常
    assert.deepEqual(citizenAt(plan, dusk + 77_777, night(true)), citizenAt(plan, dusk + 77_777));
    // 回家的那一刻和白天的日程接得上：有门的走完进门那一段就是门里那一点
    const before = citizenAt(plan, retire - 1);
    if (plan.door && retire > dusk) assert.ok(before.hidden || before.at === 'door', `${c.id}：进门那一段之后才不见`);
  }
  assert.ok(goingHome >= 10, `入夜时多数人还在外面、走回家（${goingHome}）`);
  // 打开页面时就是夜里：不是夜游的人一开始就在屋里
  for (const { plan } of plans) if (plan.door || plan.dest) assert.equal(citizenAt(plan, 123, { night: true, since: null, owl: false }).hidden, true);
  // 天亮：入夜时回了屋的人，等到自己下一次出门的时刻
  const dawn = dusk + 3 * Math.max(...plans.map(({ plan }) => plan.periodMs));
  const sky = (owl) => ({ night: false, since: dawn, after: dusk, owl });
  const wakes = [];
  for (const { c, plan } of plans) {
    const wake = wakeTime(plan, dawn);
    wakes.push(wake - dawn);
    assert.ok(wake >= dawn && wake < dawn + plan.periodMs, `${c.id}：一个周期内出门`);
    const retired = dawn >= retireTime(plan, dusk);
    if (retired && wake > dawn) assert.equal(citizenAt(plan, wake - 1, sky(false)).hidden, true, `${c.id}：没到出门的时候还在屋里`);
    if (!retired) assert.equal(asleepAt(plan, dawn + 1, sky(false)), false, `${c.id}：入夜后没回屋的人，天亮照常`);
    assert.deepEqual(citizenAt(plan, wake + 1, sky(false)), citizenAt(plan, wake + 1), `${c.id}：出门之后就是白天的日程`);
    assert.equal(asleepAt(plan, dawn + 1, sky(true)), false, '夜游的人天亮时本来就在外面');
  }
  assert.ok(new Set(wakes.map((w) => Math.round(w / 1000))).size > 10, '各自出门的时刻错开，不一齐涌上街');
  // 天亮时打开页面（没有夜可言）：照常
  for (const { plan } of plans) assert.equal(asleepAt(plan, dawn, { night: false, since: null, owl: false }), false);
  // 被客人顶回去的人：在门里、或者家门前那一点，不画
  for (const { plan } of plans.slice(0, 6)) assert.equal(indoorsAt(plan).hidden, true);
});

test('speech over a head (2026-10-13): thinking is a looping row of dots, a streaming or kept line stays, a finished one holds by its length then fades', () => {
  assert.deepEqual([0, SPEECH.dotMs, 2 * SPEECH.dotMs, 3 * SPEECH.dotMs].map(thinkingDots), ['·', '··', '···', '·'], '一、二、三个点，循环');
  assert.equal(speechAlpha({ text: '', typing: true }, 99_999), 1, '还在想：一直在');
  assert.equal(speechAlpha({ text: '半句', typing: false, doneAt: null }, 99_999), 1, '还在说（流式）：一直在');
  const done = { text: '风是不小。', typing: false, doneAt: 1000 };
  const hold = SPEECH.holdMs + SPEECH.perCharMs * 5;
  assert.equal(speechAlpha(done, 1000 + hold - SPEECH.fadeMs), 1, '说完之后按字数留一阵');
  assert.ok(speechAlpha(done, 1000 + hold - SPEECH.fadeMs / 2) < 1, '然后淡出');
  assert.equal(speechAlpha(done, 1000 + hold + 1), 0, '淡完了');
  assert.equal(speechAlpha(done, 1000 + hold + 60_000, true), 1, '你还在和他聊：他最近那一句一直留着');
  assert.ok(SPEECH.holdMs + SPEECH.perCharMs * 80 > SPEECH.holdMs + SPEECH.perCharMs * 5, '长的话留得久');
});
