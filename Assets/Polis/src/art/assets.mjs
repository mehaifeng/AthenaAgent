// 资产表与资产库（设计稿第 1 节：美术全部由代码生成，保留资产清单接口，任何一件都能单独换成外部模型）。
//
// 资产表：城里画的每一件东西都有一个 Id（building.* / civic.* / prop.* / plant.* / figure.*），缺省由代码生成。
// 资产库：拿到应用推来的清单（C# 校验过的 Id → glTF 文件），把换掉的那几件按名字从回环服务的 /polis-assets/ 取来；
// 取到了就用外部模型，取不到（404、解析错误、没有网格）就退回程序化的那一件，并记一笔——缺省回退（asset-manifest.mjs）。
// 外部模型到之前一律先用程序化的，城不等模型。格式见 Docs/GameMode_AssetManifest_CN.md。
import * as THREE from '../../vendor/three.bundle.mjs';
import { GLTFLoader, cloneSkinned } from '../../vendor/three.bundle.mjs';
import { planOverrides, resolveSource, clipFor, modelUrl, accentPropsShown, ACCENT_PROPS } from './asset-manifest.mjs';
import { BUILDINGS } from './buildings.mjs';
import * as civic from './civic.mjs';
import * as props from './props.mjs';
import * as plants from './plants.mjs';
import { buildCharacter, CHARACTER_IDS } from './characters.mjs';
import { buildDeliveredItem, buildDisplayedItem } from './items.mjs';
import { drape, blobGeometry, limbGeometry, merge, keyed } from './rig.mjs';
import { rng, hashSeed } from './noise.mjs';
import { poseAt } from './poses.mjs';
import { prepareExternalBody, attachExternalBody } from './bind.mjs';
import { footprintOf, BLOCKING_HEIGHT } from '../footprint.mjs';

/**
 * 资产表：Id → { kind, label, build(params) }。label 是美术审查里的中文名。
 * params：建筑要 { building, rand }，橄榄要 { stage, seed }，市集要 { stalls }，港口要 { quayLocalZ, pierLength, plotCenterZ }。
 */
export function createAssetRegistry(M) {
  const statue = props.statueGeometry(drape, blobGeometry, limbGeometry, merge, keyed);
  statue.userData.shared = true;
  const registry = new Map();
  // 碰撞（M3 决定 1）：缺省每件东西按自己的网格在人身高度带里推足迹（footprint.mjs）；人物与成果不挡路——
  // 成果是"轮到你了"的那样东西，要走过去点它；陈列的藏品站在底座上，底座已经挡了
  const add = (id, kind, label, build, collision = kind === 'figure' ? 'none' : 'band') => registry.set(id, { id, kind, label, build, collision });
  const buildingLabels = { stoaLibrary: '柱廊书库', house: '民居', workshop: '作坊', sculptureGarden: '雕塑园', treasury: '金库（文件夹）', warehouse: '仓库' };
  for (const [kind, fn] of Object.entries(BUILDINGS)) {
    add(`building.${kind}`, 'building', buildingLabels[kind], (p = {}) => {
      const b = p.building ?? { key: kind, kind, sizeClass: 2, state: 'lived', incomplete: false };
      return fn(M, b, p.rand ?? rng(hashSeed(b.key)), { statue });
    });
  }
  add('civic.temple', 'civic', '神庙', (p = {}) => civic.buildTemple(M, p));
  add('civic.tholos', 'civic', '图书馆（圆形）', () => civic.buildTholos(M));
  add('civic.forge', 'civic', '锻炉', () => civic.buildForge(M));
  add('civic.market', 'civic', '市集', (p = {}) => civic.buildMarket(M, p.stalls ?? 0));
  add('civic.gate', 'civic', '城门', () => civic.buildGate(M));
  add('civic.agora', 'civic', '广场（委托板、日晷）', () => civic.buildAgora(M));
  add('civic.harbor', 'civic', '港口', (p = {}) => civic.buildHarbor(M, p.quayLocalZ ?? 9, p.pierLength ?? 14, p.plotCenterZ ?? 0, statue));
  add('civic.boat', 'civic', '船', () => civic.buildBoat(M), 'none');
  add('civic.paintedStoa', 'civic', '彩绘柱廊', () => civic.buildPaintedStoa(M));
  add('civic.archive', 'civic', '书库', () => civic.buildArchive(M));
  add('civic.vault', 'civic', '金库（城邦）', () => civic.buildVault(M));
  add('civic.terrace', 'civic', '港前石台', (p = {}) => civic.buildTerrace(M, p));
  add('civic.groveMarker', 'civic', '橄榄林界碑', () => civic.buildGroveMarker(M));
  const propTable = {
    amphora: ['双耳瓶', props.buildAmphora], hydria: ['水罐', props.buildHydria], krater: ['调酒缸', props.buildKrater],
    kylix: ['酒杯', props.buildKylix], oinochoe: ['酒壶', props.buildOinochoe], lekythos: ['白底油壶', props.buildLekythos],
    lamp: ['油灯', props.buildLamp], brazier: ['火盆', props.buildBrazier], tripod: ['三足鼎', props.buildTripod],
    stool: ['凳子', props.buildStool], chest: ['箱子', props.buildChest], scrollCase: ['卷轴筒', props.buildScrollCase],
    easel: ['画架', props.buildEasel], herm: ['方柱头像', props.buildHerm], stele: ['石碑', props.buildStele],
    crate: ['木箱', props.buildCrate], basket: ['篮子', props.buildBasket], pedestal: ['底座', props.buildPedestal],
  };
  for (const [name, [label, fn]] of Object.entries(propTable)) add(`prop.${name}`, 'prop', label, () => fn(M));
  add('prop.statue', 'prop', '立像', () => props.buildStatue(M, statue));
  for (const kind of ['scroll', 'ledger', 'painting', 'answer']) {
    const label = { scroll: '卷轴', ledger: '账册', painting: '彩绘板', answer: '回答' }[kind];
    add(`prop.item-${kind}`, 'prop', `成果：${label}（待收下）`, () => buildDeliveredItem(M, kind), 'none');
    add(`prop.display-${kind}`, 'prop', `藏品：${label}（陈列）`, () => buildDisplayedItem(M, kind), 'none');
  }
  add('plant.olive', 'plant', '橄榄', (p = {}) => plants.buildOlive(M, { stage: p.stage ?? 'grown', seed: p.seed ?? 1 }));
  add('plant.cypress', 'plant', '柏树', (p = {}) => plants.buildCypress(M, p));
  add('plant.laurel', 'plant', '月桂', (p = {}) => plants.buildLaurel(M, p));
  add('plant.potted', 'plant', '盆栽', (p = {}) => plants.buildPottedPlant(M, p));
  add('plant.acanthus', 'plant', '莨苕', (p = {}) => plants.buildAcanthus(M, p));
  const figureLabels = {
    athena: '雅典娜', player: '你（玩家）', surveyor: '测绘员', scribe: '抄写员', accountant: '账房', smith: '铁匠', sailor: '水手',
    librarian: '图书管理员', herald: '传令官', painter: '画师', artisan: '工匠', priestess: '女祭司',
  };
  for (const id of CHARACTER_IDS) add(`figure.${id}`, 'figure', figureLabels[id] ?? id, (p = {}) => buildCharacter(M, id, p.spec ?? {}));
  return { registry, statue };
}

/** 外部模型包一层：按清单缩放、转向、偏移，开阴影，量出高度给名牌用。 */
function wrapExternal(gltf, entry) {
  const wrapper = new THREE.Group();
  const model = cloneSkinned(gltf.scene);
  model.scale.setScalar(entry.scale);
  model.rotation.y = THREE.MathUtils.degToRad(entry.rotateY);
  model.position.set(...entry.offset);
  model.traverse((o) => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } });
  wrapper.add(model);
  const box = new THREE.Box3().setFromObject(wrapper);
  wrapper.userData.height = Number.isFinite(box.max.y) ? box.max.y + 0.4 : 3;
  wrapper.userData.external = true;
  return { wrapper, model };
}

/**
 * 资产库。
 * @param {object} M 材质
 * @param {{ manifest?: object|null, fetchModel?: (url: string) => Promise<object> }} [options]
 *   manifest：应用推来的覆盖（Id → 条目），没有就是全部程序化；fetchModel 可替换（测试用），缺省用 GLTFLoader。
 */
export function createAssetLibrary(M, { manifest = null, fetchModel = null } = {}) {
  const { registry, statue } = createAssetRegistry(M);
  const { overrides, ignored } = planOverrides(registry.keys(), manifest);
  const loads = new Map();
  const models = new Map();
  /** rig: procedural 的人物：模型搬进人物坐标、找好关节、算好权重的那一份（bind.prepareExternalBody），每个实例共用。 */
  const bodies = new Map();
  const report = {
    requested: [...overrides.keys()],
    ignored: ignored.map((i) => `${i.id}: ${i.reason}`),
    fallbacks: [],
    external: [],
  };
  for (const line of report.ignored) console.warn(`[polis assets] ignored ${line}`);
  const loader = new GLTFLoader();
  const load = fetchModel ?? ((url) => loader.loadAsync(url));

  /** 取全部外部模型（并行），每一件的结果记进 loads；返回取到了几件。永不抛：失败就是回退。 */
  async function prepare() {
    await Promise.all([...overrides].map(async ([id, entry]) => {
      try {
        const gltf = await load(modelUrl(entry));
        let meshes = 0;
        gltf?.scene?.traverse((o) => { if (o.isMesh) meshes++; });
        if (!gltf?.scene || meshes === 0) throw new Error('the model has no meshes');
        // 蒙到程序化骨架上的人物：找关节、算权重放在这里做，失败（没站成 A-pose、手贴着身体……）和加载失败一样退回程序化的
        if (entry.rig === 'procedural') bodies.set(id, prepareExternalBody(gltf.scene, entry));
        models.set(id, gltf);
        loads.set(id, { ok: true });
        report.external.push(id);
      } catch (e) {
        const reason = String(e?.message ?? e).slice(0, 200);
        loads.set(id, { ok: false, reason });
        report.fallbacks.push(`${id}: ${reason}`);
        console.warn(`[polis assets] ${id} falls back to the procedural asset: ${reason}`);
      }
    }));
    return report.external.length;
  }

  function source(id) {
    if (!registry.has(id)) throw new Error(`assets: unknown asset id '${id}'`);
    return resolveSource(id, overrides, loads).source;
  }

  /** 一件东西（建筑、器物、植物、公共建筑）：外部模型到了就用它，否则程序化。根节点记着它是哪件、怎样参与碰撞。 */
  function object(id, params = {}) {
    const entry = registry.get(id);
    if (!entry) throw new Error(`assets: unknown asset id '${id}'`);
    if (entry.kind === 'figure') throw new Error(`assets: '${id}' is a figure; use figure()`);
    const group = source(id) === 'external' ? wrapExternal(models.get(id), overrides.get(id)).wrapper : entry.build(params);
    group.userData.asset = { id, kind: entry.kind, collision: entry.collision };
    return group;
  }

  /**
   * 一件东西的碰撞足迹（按它自己的网格推，算一次记在根上；外部模型同样按网格推）。矮于 BLOCKING_HEIGHT 的器物与植物、
   * 以及登记为不参与碰撞的（人物、成果、船）返回 null。
   */
  function footprint(group) {
    const info = group.userData.asset;
    if (!info || info.collision === 'none') return null;
    if (group.userData.footprint === undefined) {
      // 器物与植物：矮于 0.4 米不挡，树冠不是屋顶；建筑与公共建筑：屋檐下能走进 COVER_REACH。嵌在里面的别的资产各算各的
      const small = info.kind === 'prop' || info.kind === 'plant';
      group.userData.footprint = footprintOf(group, { minHeight: small ? BLOCKING_HEIGHT : 0, coverReach: small ? null : undefined, stopAt: (o) => !!o.userData.asset });
    }
    return group.userData.footprint;
  }

  /**
   * 一个人物：返回 { root, pose(action, t, style), anchors in root.userData }。外部模型按清单把动作映射到它的动画片段，
   * 没映射的动作退回 idle、连 idle 都没有就静止（asset-manifest.clipFor）。外部人物没有手里的小件，
   * 所以"轮到你了"的两样东西（交付的红卷轴、等审批的红封印）浮在胸前——颜色的规则不能因为换了模型就失效。
   */
  function figure(id, params = {}) {
    const entry = registry.get(id);
    if (!entry || entry.kind !== 'figure') throw new Error(`assets: '${id}' is not a figure`);
    if (source(id) !== 'external') return entry.build(params);
    // 蒙在程序化骨架上：先建这个人物的程序化版本（骨架、姿势、手里的东西），再换上外部的身体（bind.mjs）
    if (bodies.has(id)) return attachExternalBody(entry.build(params), bodies.get(id));
    const gltf = models.get(id);
    const over = overrides.get(id);
    const { wrapper, model } = wrapExternal(gltf, over);
    const mixer = new THREE.AnimationMixer(model);
    const clips = new Map((gltf.animations ?? []).map((c) => [c.name, c]));
    let current = null;
    const H = Math.max(1, wrapper.userData.height - 0.4);
    wrapper.userData.chest = new THREE.Vector3(0, H * 0.62, 0);
    wrapper.userData.headTop = new THREE.Vector3(0, H + 0.05, 0);
    wrapper.userData.height = H;
    const accentProps = {};
    for (const name of ACCENT_PROPS) {
      // 封印是一颗扁圆的珠子：从哪个角度看都是一块红，不像一张平放的圆片那样被俯视压扁
      const p = new THREE.Mesh(name === 'gift' ? new THREE.CylinderGeometry(0.05, 0.05, 0.36, 14) : new THREE.SphereGeometry(0.075, 18, 12), M.accent);
      if (name === 'gift') p.rotation.z = Math.PI / 2;
      else p.scale.set(1, 0.6, 1);
      p.position.set(0, H * 0.55, 0.32);
      p.userData.accent = true;
      p.visible = false;
      wrapper.add(p);
      accentProps[name] = p;
    }
    if (id === 'figure.athena') {
      wrapper.traverse((o) => { if (o.isMesh && !o.userData.accent) o.userData.athena = true; });
      const light = new THREE.PointLight(0xfff6e8, 1.1, 4.5, 2);
      light.position.set(0, 0.45, 0.3);
      wrapper.add(light);
      wrapper.userData.light = light;
    }
    let lastClock = null;
    return {
      root: wrapper,
      external: true,
      props: new Map(),
      cloth: [],
      /**
       * 外部人物：按清单把动作映射到它的片段（没有的按 clipFor 退），换片段时交叉淡入 0.22 秒（M3），
       * 一次性的动作（挥手、接过）只播一遍。时间由 style.clock（毫秒，缺省 performance.now()）推进。
       */
      pose(action, t, style = {}) {
        const now = style.clock ?? performance.now();
        const dt = lastClock == null ? 1 / 60 : Math.min(0.1, Math.max(0, (now - lastClock) / 1000));
        lastClock = now;
        const name = clipFor(over, action);
        const clip = name ? clips.get(name) : null;
        if (clip && current?.getClip() !== clip) {
          const next = mixer.clipAction(clip);
          next.reset();
          next.setLoop(action === 'wave' || action === 'receive' ? THREE.LoopOnce : THREE.LoopRepeat, Infinity);
          next.clampWhenFinished = true;
          next.enabled = true;
          next.setEffectiveWeight(1);
          next.play();
          if (current) current.crossFadeTo(next, 0.22, false);
          current = next;
        } else if (!clip && current) {
          mixer.stopAllAction();
          current = null;
        }
        if (current) mixer.update(dt);
        const shown = accentPropsShown(poseAt(action === 'walk' || action === 'run' ? 'idle' : action, t, style));
        for (const [n, mesh] of Object.entries(accentProps)) mesh.visible = shown.includes(n);
      },
    };
  }

  return {
    registry,
    statue,
    report,
    prepare,
    source,
    object,
    footprint,
    figure,
    get overrides() { return overrides; },
    ids(kind = null) { return [...registry.values()].filter((e) => !kind || e.kind === kind).map((e) => e.id); },
  };
}
