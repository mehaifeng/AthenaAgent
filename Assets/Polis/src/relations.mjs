// 人物关系在页面上的那一半（第三轮需求 1，2026-10-13，用户：NPC 对玩家、NPC 对 NPC，好恶直接影响对话和行为）。纯逻辑，Node 单测 relations.test.mjs。
// 关系值、五档、耐心、叫不叫得出来、原因都是 C# 算的（Services/GameMode/PolisRelations.cs，推来 relations）；这里只把它读成好用的样子，
// 再按它决定动作：打招呼的样子、闲聊挑谁、几个人一起接你的话时谁开口、谁转身谁瞥一眼。
//
// 规则（每一条都有断言）：
//   1. 五档的界与 C# 一致：亲近 ≥ 60、友好 ≥ 25、寻常 > −25、冷淡 > −60、其余厌恶。不认识的人按寻常（关系还没推来、换城的那一刻）。
//   2. 打招呼按档：亲近——转身挥手、说热络的话；友好——挥手、说一句；寻常——点头、说一句；冷淡——只看你一眼，不说话；厌恶——转过身去。
//   3. 闲聊挑人：两人互相厌恶（两个方向都 ≤ −60）不凑一对；合得来的优先（同样近时，关系好的那一对先）。
//   4. 你自己说话（第三轮需求 6）：6 米内、画面里的人最多六位都有反应——亲近的转身挥手、友好的点头、寻常的转过来听、冷淡的瞥一眼、
//      厌恶的背过身去；最多三位依次开口：越近、越喜欢你、越健谈的越先，厌恶你的不开口。
//   5. 怎么看雅典娜（第四轮需求 8，2026-10-15，用户：不是所有 NPC 对雅典娜都是尊敬的）：C# 推来 athena（id → 分数、档、原因），
//      总览里每人多一行；闲聊挑雅典娜时谁都可以和她说（不服她的人说话带刺，那是 C# 的提示词管的）。

export const RELATION = Object.freeze({ close: 60, friendly: 25, coolBelow: -25, hostile: -60 });
export const BANDS = Object.freeze(['hostile', 'cool', 'neutral', 'friendly', 'close']);

/** 规则 1：分数 → 档。 */
export function bandOf(score) {
  const s = Number(score) || 0;
  if (s >= RELATION.close) return 'close';
  if (s >= RELATION.friendly) return 'friendly';
  if (s > RELATION.coolBelow) return 'neutral';
  if (s > RELATION.hostile) return 'cool';
  return 'hostile';
}

/** 不认识的人（规则 1）。 */
export const NEUTRAL = Object.freeze({ score: 0, band: 'neutral', patience: 3, opens: true, comesOut: false, why: Object.freeze([]), talk: 0.5 });

/**
 * C# 推来的 relations → 好查的样子：you（id → 他怎么看你）、bond（"a>b" → a 怎么看 b）、ties（id → 朋友、不和的人）。
 * 不认得的字段一律丢掉（页面不信任何推来的东西去做别的用）。
 */
export function readRelations(payload) {
  const you = new Map();
  for (const [id, v] of Object.entries(payload?.you ?? {})) {
    if (!v || typeof v !== 'object') continue;
    const score = Math.max(-100, Math.min(100, Math.round(Number(v.score) || 0)));
    you.set(id, {
      score,
      band: BANDS.includes(v.band) ? v.band : bandOf(score),
      patience: Math.max(1, Math.min(5, Math.round(Number(v.patience) || 3))),
      opens: v.opens !== false,
      comesOut: !!v.comesOut,
      why: Array.isArray(v.why) ? v.why.filter((w) => typeof w === 'string').slice(0, 2) : [],
      talk: Number.isFinite(Number(v.talk)) ? Math.max(0, Math.min(1, Number(v.talk))) : 0.5,
    });
  }
  const bond = new Map();
  for (const p of Array.isArray(payload?.pairs) ? payload.pairs : []) {
    if (!Array.isArray(p) || p.length < 4) continue;
    const [a, b, ab, ba] = p;
    if (typeof a !== 'string' || typeof b !== 'string') continue;
    bond.set(`${a}>${b}`, Number(ab) || 0);
    bond.set(`${b}>${a}`, Number(ba) || 0);
  }
  const ties = new Map();
  const list = (x) => (Array.isArray(x) ? x.filter((t) => Array.isArray(t) && typeof t[0] === 'string').map(([id, s]) => ({ id, score: Number(s) || 0 })) : []);
  for (const [id, t] of Object.entries(payload?.ties ?? {})) ties.set(id, { friends: list(t?.friends), rivals: list(t?.rivals) });
  const athena = new Map();
  for (const [id, v] of Object.entries(payload?.athena ?? {})) {
    if (!v || typeof v !== 'object') continue;
    const score = Math.max(-100, Math.min(100, Math.round(Number(v.score) || 0)));
    athena.set(id, { score, band: BANDS.includes(v.band) ? v.band : bandOf(score), why: Array.isArray(v.why) ? v.why.filter((w) => typeof w === 'string').slice(0, 2) : [] });
  }
  return { you, bond, ties, athena };
}

export const EMPTY_RELATIONS = Object.freeze({ you: new Map(), bond: new Map(), ties: new Map(), athena: new Map() });

/** 规则 5：他怎么看雅典娜（没推来的按寻常）。 */
export function regardOf(rel, id) {
  return rel?.athena?.get(id) ?? { score: 0, band: 'neutral', why: [] };
}

/** 他怎么看你（不认识的按寻常）。 */
export function attitudeOf(rel, id) {
  return rel?.you?.get(id) ?? NEUTRAL;
}

/** a 怎么看 b（没推来的按 0）。 */
export function bondOf(rel, a, b) {
  return rel?.bond?.get(`${a}>${b}`) ?? 0;
}

/**
 * 规则 2：他跟你打招呼的样子。greets：会不会转过来；wave：挥不挥手；line：说哪一组话（'warm' | 'plain' | null）；
 * turnAway：转过身去；glance：只看一眼。
 */
export function greetingStyle(band) {
  switch (band) {
    case 'close': return { greets: true, wave: true, line: 'warm', turnAway: false, glance: false };
    case 'friendly': return { greets: true, wave: true, line: 'plain', turnAway: false, glance: false };
    case 'cool': return { greets: false, wave: false, line: null, turnAway: false, glance: true };
    case 'hostile': return { greets: false, wave: false, line: null, turnAway: true, glance: false };
    default: return { greets: true, wave: false, line: 'plain', turnAway: false, glance: false };
  }
}

/** 规则 3：这一对能不能凑一起闲聊（两个方向都厌恶就不行）。 */
export function canChat(rel, a, b) {
  return !(bondOf(rel, a, b) <= RELATION.hostile && bondOf(rel, b, a) <= RELATION.hostile);
}

/** 规则 3：同样近的两对里，关系好的那一对先（分越小越先：距离减去关系的一点加成）。 */
export function chatPreference(rel, a, b, distance) {
  const mean = (bondOf(rel, a, b) + bondOf(rel, b, a)) / 2;
  return distance - 1.2 * (mean / 100);
}

/**
 * 你自己说话时附近几个人的反应（第三轮需求 6，2026-10-13，用户：不指定 NPC 说话时，附近的多个 NPC 都对你做出反应，而不是只有一个）。
 * reactors：最多几个人有动作；speakers：最多几个人开口（C# 的 PolisTalk.MaxChorusSpeakers）；holdMs：有人开口时他们站定多久（C# 那边说完了推
 * chorus-end，再留 afterMs）；briefMs：没人开口时动作留多久。
 */
export const CHORUS = Object.freeze({ reactors: 6, speakers: 3, holdMs: 30_000, afterMs: 5000, briefMs: 4000 });

/**
 * 规则 4：一个人听见你说话的样子——亲近的转身挥手、友好的转身点头、寻常的转过来听、冷淡的只转头瞥一眼、厌恶的转过身去。
 * 要开口的人一律转过来（冷淡的也是：他要接你的话）。'wave' | 'nod' | 'listen' | 'glance' | 'turnAway'。
 */
export function reactionOf(band, speaks = false) {
  if (speaks) return band === 'close' ? 'wave' : band === 'friendly' ? 'nod' : 'listen';
  switch (band) {
    case 'close': return 'wave';
    case 'friendly': return 'nod';
    case 'cool': return 'glance';
    case 'hostile': return 'turnAway';
    default: return 'listen';
  }
}

/** 这几种反应要转过身来面朝你（瞥一眼、转过身去的不转）。 */
export const FACING_REACTIONS = Object.freeze(['wave', 'nod', 'listen']);

/**
 * 规则 4：谁开口、按什么先后（最多 CHORUS.speakers 位）。hearers：[{ id, d（离你多远，米） }]，都已经在 6 米内、画面里。
 * 越近、越喜欢你、越健谈的越先：分 = (1 − d / radius) + 分数 / 100 + 0.4 × 健谈（喜欢你的人远一点也抢着开口）；厌恶你的不开口。同分按 Id。
 */
export function pickResponders(hearers, rel, { radius = 6, max = CHORUS.speakers } = {}) {
  return hearers
    .map((h) => {
      const a = attitudeOf(rel, h.id);
      return { id: h.id, band: a.band, score: (1 - Math.min(1, h.d / radius)) + a.score / 100 + 0.4 * a.talk };
    })
    .filter((h) => h.band !== 'hostile')
    .sort((x, y) => (y.score - x.score) || (x.id < y.id ? -1 : 1))
    .slice(0, max)
    .map((h) => h.id);
}

const BAND_LABEL = Object.freeze({ close: 'bandClose', friendly: 'bandFriendly', neutral: 'bandNeutral', cool: 'bandCool', hostile: 'bandHostile' });
const WHY_LABEL = Object.freeze({
  warm: 'whyWarm', prickly: 'whyPrickly', nature: 'whyNature', chats: 'whyChats', annoyed: 'whyAnnoyed',
  busy: 'whyBusy', neglected: 'whyNeglected', idle: 'whyIdle',
  pious: 'whyPious', doubting: 'whyDoubting', reverent: 'whyReverent', defiant: 'whyDefiant', trade: 'whyTrade', age: 'whyAge',
});
const REGARD_LABEL = Object.freeze({ close: 'regardClose', friendly: 'regardFriendly', neutral: 'regardNeutral', cool: 'regardCool', hostile: 'regardHostile' });

/**
 * 人际总览（第三轮：关系在游戏界面有一个入口，点开预览所有的关系状态）要画的一份：每个人（居民、在屋里的居民、职业居民）
 * 怎么看你（档、分、原因），他合得来的、合不来的人（名字）。居民按分数从高到低，职业居民排在后面另成一组。纯函数。
 * people：[{ id, name, kind: 'citizen' | 'resident', indoors, bust }]；ui：词条表。
 */
export function relationsView(rel, people, ui) {
  const nameOf = new Map(people.map((p) => [p.id, p.name]));
  const fill = (template, vars) => String(template ?? '').replace(/\{(\w+)\}/g, (_, k) => vars[k] ?? '');
  const rows = people.map((p) => {
    const a = attitudeOf(rel, p.id);
    const tie = rel?.ties?.get(p.id) ?? { friends: [], rivals: [] };
    const names = (list) => list.filter((t) => nameOf.has(t.id)).map((t) => ({ id: t.id, name: nameOf.get(t.id), score: t.score }));
    const why = a.why.map((w) => ui[WHY_LABEL[w]]).filter(Boolean);
    const g = regardOf(rel, p.id);
    const gWhy = g.why.map((w) => ui[WHY_LABEL[w]]).filter(Boolean);
    return {
      athena: { score: g.score, band: g.band, label: fill(ui.relationsAthena, { band: ui[REGARD_LABEL[g.band]] ?? g.band }),
        why: gWhy.length >= 2 ? fill(ui.relationsWhyTwo, { a: gWhy[0], b: gWhy[1] }) : gWhy.length ? fill(ui.relationsWhy, { why: gWhy[0] }) : '' },
      id: p.id, name: p.name, kind: p.kind, indoors: !!p.indoors, bust: p.bust,
      score: a.score, band: a.band, bandLabel: ui[BAND_LABEL[a.band]] ?? a.band,
      why: why.length >= 2 ? fill(ui.relationsWhyTwo, { a: why[0], b: why[1] }) : why.length ? fill(ui.relationsWhy, { why: why[0] }) : '',
      friends: names(tie.friends), rivals: names(tie.rivals),
    };
  });
  const byScore = (x, y) => (y.score - x.score) || (x.name < y.name ? -1 : x.name > y.name ? 1 : 0);
  return {
    citizens: rows.filter((r) => r.kind !== 'resident').sort(byScore),
    residents: rows.filter((r) => r.kind === 'resident').sort(byScore),
  };
}
