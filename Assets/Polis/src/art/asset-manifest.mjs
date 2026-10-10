// 资产清单的纯逻辑（不碰 three.js，Node 单测覆盖）。每件资产有一个 Id，缺省由代码生成（assets.mjs 的资产表）；
// 应用可以按清单把其中任何一件换成外部 glTF（设计稿第 1 节"保留资产清单接口，任何一件都能单独换成外部模型"）。
// 格式见 Docs/GameMode_AssetManifest_CN.md。
//
// 清单先经 C# 校验（PolisAssetManifest.cs：文件名、数字范围、模型文件真的在资产目录里），页面这里再核一遍形状——
// 页面不信任何输入。缺省回退有三层，任何一层出问题，那一件都照常用程序化的：
//   1. 清单里的 Id 不在资产表里 → 忽略，记一笔；
//   2. 条目形状不对 → 忽略，记一笔；
//   3. 模型加载失败（404、解析错误、没有网格）→ 回退，记一笔（status.assets.fallbacks）。

export const ASSET_KINDS = Object.freeze(['building', 'prop', 'plant', 'figure', 'civic']);
export const MODEL_ACTIONS = Object.freeze(['walk', 'idle', 'work', 'deliver']);
const ID = /^(building|prop|plant|figure|civic)(\.[a-z][a-zA-Z0-9-]{0,31}){1,3}$/;
const MODEL = /^[A-Za-z0-9_-][A-Za-z0-9._-]{0,63}$/;

/** 外部模型的地址：应用的回环服务按一段文件名从资产目录里取（C# 再核一遍）。 */
export const MODEL_ROUTE = '/polis-assets/';

export function isAssetId(id) {
  return typeof id === 'string' && id.length <= 64 && ID.test(id);
}

function validEntry(entry) {
  if (!entry || typeof entry !== 'object' || Array.isArray(entry)) return 'entry is not an object';
  const model = entry.model;
  if (typeof model !== 'string' || !MODEL.test(model) || !/\.(glb|gltf)$/i.test(model)) return 'model must be a plain .glb / .gltf file name';
  const num = (v, lo, hi, def, open = false) => (v === undefined ? def : (typeof v === 'number' && Number.isFinite(v) && (open ? v > lo : v >= lo) && v <= hi ? v : NaN));
  const scale = num(entry.scale, 0.01, 100, 1, true);
  const rotateY = num(entry.rotateY, -360, 360, 0);
  if (Number.isNaN(scale) || Number.isNaN(rotateY)) return 'scale / rotateY out of range';
  const offset = entry.offset === undefined ? [0, 0, 0] : entry.offset;
  if (!Array.isArray(offset) || offset.length !== 3 || offset.some((v) => typeof v !== 'number' || !Number.isFinite(v) || Math.abs(v) > 50)) return 'offset must be [x, y, z] within ±50';
  const animations = entry.animations ?? {};
  if (typeof animations !== 'object' || Array.isArray(animations)) return 'animations must map actions to clip names';
  for (const [action, clip] of Object.entries(animations)) {
    if (!MODEL_ACTIONS.includes(action) || typeof clip !== 'string' || clip.length === 0 || clip.length > 64) return `bad animation '${action}'`;
  }
  return { model, scale, rotateY, offset: offset.slice(), animations: { ...animations } };
}

/**
 * 把一份清单（C# 推来的 <c>assets</c>：Id → 条目）和资产表核对，得出这一次要换的那几件。
 * @param {Iterable<string>} knownIds 资产表里的 Id
 * @param {object|null|undefined} manifest Id → { model, scale?, rotateY?, offset?, animations? }
 * @returns {{ overrides: Map<string, object>, ignored: Array<{ id: string, reason: string }> }}
 */
export function planOverrides(knownIds, manifest) {
  const known = new Set(knownIds);
  const overrides = new Map();
  const ignored = [];
  if (manifest == null) return { overrides, ignored };
  if (typeof manifest !== 'object' || Array.isArray(manifest)) {
    ignored.push({ id: '*', reason: 'manifest is not an object' });
    return { overrides, ignored };
  }
  for (const [id, entry] of Object.entries(manifest)) {
    if (!isAssetId(id)) { ignored.push({ id, reason: 'not an asset id' }); continue; }
    if (!known.has(id)) { ignored.push({ id, reason: 'unknown asset id' }); continue; }
    const checked = validEntry(entry);
    if (typeof checked === 'string') { ignored.push({ id, reason: checked }); continue; }
    overrides.set(id, checked);
  }
  return { overrides, ignored };
}

export function modelUrl(entry) {
  return MODEL_ROUTE + encodeURIComponent(entry.model);
}

/** 一个动作用模型里的哪段动画：映射了的用映射；没映射的退回 idle；连 idle 都没有就是静止（null）。 */
export function clipFor(entry, action) {
  const map = entry?.animations ?? {};
  return map[action] ?? map.idle ?? null;
}

/**
 * 这一件此刻用什么画：没有覆盖 → 程序化；覆盖了但模型还没到 → 先用程序化；加载失败 → 程序化（回退）；加载好了 → 外部。
 * @param {string} id
 * @param {Map<string, object>} overrides planOverrides 的结果
 * @param {Map<string, { ok: boolean, reason?: string }>} loads 每件外部模型的加载结果
 */
export function resolveSource(id, overrides, loads) {
  if (!overrides.has(id)) return { source: 'procedural', reason: null };
  const load = loads.get(id);
  if (!load) return { source: 'procedural', reason: 'loading' };
  if (!load.ok) return { source: 'procedural', reason: load.reason ?? 'load failed' };
  return { source: 'external', reason: null };
}
