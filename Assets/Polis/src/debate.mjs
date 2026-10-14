// 城里的竞技（M-E，2026-10-13）：广场讲台前的辩论。这里只有纯函数（Node 单测 debate.test.mjs）：舞台怎么站、
// 观众围成怎样的半圆、谁被叫过来、几个人为你挥手。状态机、提示词、裁判都在 C#（Services/GameMode/PolisDebate.cs）。
//
// 舞台：讲台（agora 的 userData.bema，传令官站在上面主持）朝广场中心的那一面是正面。正面 2.6 米处是舞台中心，陪审团在
// 舞台中心之外围成两圈半圆（2.5 / 3.3 米，张开 ±77°；第四轮需求 6，2026-10-15，用户：围观的 NPC 靠近一些——原来是 3.4 / 4.6），每个站位都要能站（layout.walkable）；站不了的点往外找一点，再不行就不要
// 这个位置。2026-10-13 起（用户）舞台中心是一座辩台（civic.AGORA_DAIS），你和雅典娜各站它的一端、面对面（daisInset 米往里）；
// 没有辩台的旧城还是各站舞台中心一侧 1.2 米。

export const DEBATE = Object.freeze({
  interactAhead: 1.4,       // 按 E 的那一点：讲台中心往正面走这么远（台阶前）
  stageAhead: 2.6,          // 舞台中心离讲台中心多远
  sideOffset: 1.2,          // 你和她各离舞台中心多远（面对面）
  rings: [2.5, 3.3],        // 观众的两圈（第四轮：从 3.4 / 4.6 收近，辩台半长 1.7 米，两端站着你和她，最近的观众离你们一米多）
  spread: 1.35,             // 半圆张开（± 弧度）
  perRing: [7, 9],
  maxAudience: 14,
  gatherRadius: 22,         // 判票时多远以内、看得见的居民会走过来
  walkSpeed: 1.6,           // 走过来比平时（1.3）快一点：人都想看热闹
  maxGatherMs: 9000,        // 走得再远也在这么久里到（更远的不叫）
  doneHoldMs: 45_000,       // 判完之后多久自己散场
  leaveDistance: 25,        // 你走开这么远，这一场作废
  waveEveryMs: 2600,        // 支持你的人每隔这么久挥一次手
  daisInset: 0.45,          // 站在辩台的一端：离端头这么远
  crowdStaggerMs: 900,      // 围观的人起哄，一句接一句隔这么久
});

const unit = (x, z) => { const l = Math.hypot(x, z) || 1; return { x: x / l, z: z / l }; };

/** 一个能站的点：先试 p 本身，再在它周围 0.3 / 0.6 / 0.9 米的一圈上找（八个方向）。都不行返回 null。 */
export function nearestStandable(p, walkable) {
  if (walkable(p)) return { x: p.x, z: p.z };
  for (const r of [0.3, 0.6, 0.9]) {
    for (let k = 0; k < 8; k++) {
      const a = (k / 8) * Math.PI * 2;
      const q = { x: p.x + Math.cos(a) * r, z: p.z + Math.sin(a) * r };
      if (walkable(q)) return q;
    }
  }
  return null;
}

/**
 * 舞台的站位。bema、center：世界坐标的讲台中心与广场中心；walkable：layout.walkable。
 * 返回 { interact, stage, you, athena, audience: [{ x, z, yaw }], front }——yaw 是面朝讲台（观众）或面朝对手（你和她）。
 */
export function debateStage({ bema, center, walkable, dais = null }) {
  const front = unit(center.x - bema.x, center.z - bema.z);
  const side = { x: -front.z, z: front.x };
  const at = (d, s = 0) => ({ x: bema.x + front.x * d + side.x * s, z: bema.z + front.z * d + side.z * s });
  const stage = dais ? { x: dais.x, z: dais.z } : at(DEBATE.stageAhead);
  const interact = nearestStandable(at(DEBATE.interactAhead), walkable) ?? at(DEBATE.interactAhead);
  // 辩台两端（沿 x）：你在离广场中心近的那一端，她在讲台那一端
  const end = dais ? dais.len / 2 - DEBATE.daisInset : 0;
  const nearCenter = dais && Math.abs(dais.x + end - center.x) < Math.abs(dais.x - end - center.x) ? 1 : -1;
  const you = dais ? { x: dais.x + nearCenter * end, z: dais.z } : nearestStandable(at(DEBATE.stageAhead, DEBATE.sideOffset), walkable) ?? stage;
  const athena = dais ? { x: dais.x - nearCenter * end, z: dais.z } : nearestStandable(at(DEBATE.stageAhead, -DEBATE.sideOffset), walkable) ?? stage;
  const yawTo = (from, to) => Math.atan2(to.x - from.x, to.z - from.z);
  const audience = [];
  const taken = [you, athena];
  DEBATE.rings.forEach((r, ring) => {
    const n = DEBATE.perRing[ring];
    for (let k = 0; k < n; k++) {
      // 半圆朝外（背对讲台的那一侧），正中对着讲台
      const a = -DEBATE.spread + (2 * DEBATE.spread * k) / (n - 1);
      const dir = { x: front.x * Math.cos(a) + side.x * Math.sin(a), z: front.z * Math.cos(a) + side.z * Math.sin(a) };
      const p = nearestStandable({ x: stage.x + dir.x * r, z: stage.z + dir.z * r }, walkable);
      if (!p) continue;
      if (taken.some((q) => Math.hypot(q.x - p.x, q.z - p.z) < 0.7)) continue;
      taken.push(p);
      audience.push({ x: p.x, z: p.z, yaw: yawTo(p, stage), ring });
    }
  });
  return {
    front, stage, interact, dais: dais ? { ...dais } : null,
    you: { ...you, yaw: yawTo(you, athena) },
    athena: { ...athena, yaw: yawTo(athena, you) },
    audience: audience.slice(0, DEBATE.maxAudience),
  };
}

/**
 * 判票时谁走过来：candidates [{ id, x, z }]（看得见、在 gatherRadius 以内的居民），spots 是舞台的观众站位。
 * 离舞台近的人先挑离他最近的空位；走过去的直线每 0.25 米都要能站（walkable），走不过去的不叫；超过 maxGatherMs 的也不叫。
 * 返回 Map(id → { index, from, to, ms })。
 */
export function assignAudience(candidates, spots, walkable, { speed = DEBATE.walkSpeed, stage = null } = {}) {
  const free = spots.map((s, index) => ({ ...s, index }));
  const out = new Map();
  const clear = (a, b) => {
    const len = Math.hypot(b.x - a.x, b.z - a.z);
    const n = Math.max(1, Math.ceil(len / 0.25));
    for (let i = 0; i <= n; i++) if (!walkable({ x: a.x + ((b.x - a.x) * i) / n, z: a.z + ((b.z - a.z) * i) / n })) return false;
    return true;
  };
  // 离舞台近的先挑（没给舞台就按离第一个站位的远近）
  const ref = stage ?? free[0] ?? { x: 0, z: 0 };
  const near = [...candidates].sort((p, q) => Math.hypot(p.x - ref.x, p.z - ref.z) - Math.hypot(q.x - ref.x, q.z - ref.z));
  for (const c of near) {
    if (free.length === 0) break;
    const ranked = free
      .map((s) => ({ s, d: Math.hypot(s.x - c.x, s.z - c.z) }))
      .sort((p, q) => p.d - q.d);
    const pick = ranked.find(({ s, d }) => (d / speed) * 1000 <= DEBATE.maxGatherMs && clear(c, s));
    if (!pick) continue;
    free.splice(free.indexOf(pick.s), 1);
    out.set(c.id, { index: pick.s.index, from: { x: c.x, z: c.z }, to: { x: pick.s.x, z: pick.s.z, yaw: pick.s.yaw }, ms: (pick.d / speed) * 1000 });
  }
  return out;
}

/** 几个人为你挥手：audience 人里按票数的比例（十票里 votesForYou 票），四舍五入。 */
export function supportersCount(audience, votesForYou, jury = 10) {
  if (audience <= 0) return 0;
  return Math.max(0, Math.min(audience, Math.round((audience * Math.max(0, Math.min(jury, votesForYou))) / jury)));
}

/** 她那一轮的话太长放不进头顶的气泡：取第一句（≤ 36 字），后面省略。整句在顶上的辩论牌里。 */
export function bubbleExcerpt(text, max = 36) {
  const t = String(text ?? '').trim();
  // 第一句：到第一个句末标点为止（不用正则的后行断言：老的 WebKitGTK 不认这种写法，整个页面会加载失败）
  let end = t.length;
  for (let i = 0; i < t.length; i++) {
    if ('。！？!?'.includes(t[i]) || (t[i] === '.' && (i + 1 === t.length || t[i + 1] === ' '))) { end = i + 1; break; }
  }
  const first = t.slice(0, end);
  const s = first.length <= max ? first : `${first.slice(0, max - 1)}…`;
  return s.length < t.length && !s.endsWith('…') ? `${s}…` : s;
}
