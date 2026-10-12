// 雅典娜住在城里（2026-10-13 改向）。她不再镜像任何回合：工具调用太快太密（logs.db：工具中位 0.05 秒、三成调用间隔不到半秒、
// 四成回合根本不调工具），没有一个时钟能让逐调用的动画好看。她现在只是城里的一个人——在广场上待着，在广场那一圈六个点之间
// 走一走；你走近、或者对话栏开着，她就不再走开；你在远处开口，她走过来（远了就化作一道光过来）。
// 纯逻辑：不碰 DOM、不碰 three.js，时间由调用方给（毫秒），同样的调用总是得到同样的结果（Node 单测 athena.test.mjs）。
//
// 规则（每一条都有断言）：
//   1. 每次移动都要完成：走到一半的路不改道，新目标排在后面（猫头鹰村验证过的那一条，M0 的编排器也守着它）。
//   2. 闲逛只在广场那一圈的六个点（agora:0..5）之间；每一处停多久按种子错开，不是满城同一个节拍。
//   3. 留住她（hold：你在旁边、对话栏开着）时不开始新的闲逛；正在走的那一段照样走完。
//   4. 去一个地方：走得到（≤ maxWalkMs）就走，否则化作一道光瞬移（teleportMs）——神本来就能瞬移。
//   5. 时间只进不退：比上一次更早的 now 当成上一次（回放式的后退由调用方重建）。

export const ATHENA = Object.freeze({
  maxWalkMs: 10000,     // 3.2 米 / 秒走 10 秒约 32 米：广场到周围一圈走得到，再远就瞬移（M3 定的，原来在编排器里）
  teleportMs: 420,      // 化作一道光
  idleMs: 6000,         // 每到一处至少待这么久
  idleSpreadMs: 4000,   // 再按种子多待 0–4 秒
  wanderSpots: 6,       // 广场那一圈（layout.wanderSpots）
});

const MOVING = new Set(['walk', 'teleport']);

/** 这一处是广场那一圈的第几个点（不是就 -1）。 */
export function spotIndex(site) {
  const m = /^agora:(\d+)$/.exec(String(site ?? ''));
  return m ? Number(m[1]) % ATHENA.wanderSpots : -1;
}

/**
 * @param {object} options
 * @param {(from: string, to: string) => number} options.travelMs 两个地点之间走路要多久（毫秒；layout.travelMs）
 * @param {string} [options.home] 起始地点
 * @param {number} [options.startAt] 时间线从哪一刻开始（毫秒）
 * @param {number} [options.seed] 停留时长的种子
 */
export function createAthena({ travelMs, home = 'agora:0', startAt = 0, seed = 7 } = {}) {
  if (typeof travelMs !== 'function') throw new TypeError('athena: travelMs(from, to) is required');
  let clock = startAt;
  let position = home;
  let heldAt = -Infinity;      // 最近一次被留住的时刻
  const queue = [];            // 排着的目的地：{ to, t }
  const steps = [];            // 已经开始的步骤（只增不减，测试读）
  let current = null;
  startIdle(home, startAt);

  function idleSpan(k) {
    // 一个确定的伪随机数：同一个种子、同一处，永远停一样久（截图与断言可复现）
    const h = Math.abs(Math.imul(seed * 2654435761 + k * 40503, 2246822519) >>> 0) % 10007;
    return ATHENA.idleMs + (h / 10007) * ATHENA.idleSpreadMs;
  }

  function startIdle(at, t) {
    current = { kind: 'idle', at, start: t, end: null, until: t + idleSpan(steps.length) };
    steps.push(current);
  }

  function startMove(to, t) {
    const from = position;
    const walk = travelMs(from, to);
    const teleport = !(Number.isFinite(walk) && walk <= ATHENA.maxWalkMs);
    current = {
      kind: teleport ? 'teleport' : 'walk',
      from, to, start: t,
      end: t + (teleport ? ATHENA.teleportMs : Math.max(1, walk)),
      reason: teleport ? 'far' : null,
    };
    steps.push(current);
  }

  function nextWanderTarget() {
    const k = spotIndex(position);
    return `agora:${k < 0 ? 0 : (k + 1) % ATHENA.wanderSpots}`;
  }

  /** 把时间线推进到 now：让已经开始的移动走完，在每个结束点决定下一步。 */
  function advance(now, hold) {
    if (hold) heldAt = now;
    for (let guard = 0; guard < 10000; guard++) {
      if (MOVING.has(current.kind)) {
        if (now < current.end) return;
        position = current.to;
        const arrived = current.end;
        if (queue.length) {
          const next = queue.shift();
          startMove(next.to, Math.max(arrived, next.t));
        } else {
          startIdle(position, arrived);
        }
        continue;
      }
      // 待着：先办排着的事，再看该不该走一走
      if (queue.length) {
        const next = queue.shift();
        startMove(next.to, Math.max(current.start, next.t));
        continue;
      }
      if (hold || now < current.until) return;
      // 被留住过：从放开她的那一刻（最近一次被留住时）起算，不是从本该出发的那一刻——不会一放开就"补走"一段
      startMove(nextWanderTarget(), Math.max(current.until, heldAt));
    }
    throw new Error('athena: runaway timeline');
  }

  return {
    /** 推进到 now（hold：你在旁边或对话栏开着，不开始新的闲逛）。返回当前状态。 */
    update(now, { hold = false } = {}) {
      clock = Math.max(clock, now);
      advance(clock, hold);
      return this.state();
    },
    /**
     * 去一个地方（站点名：agora:k、pt:x,z、公共建筑、b:<建筑>）。正在走就排在后面（规则 1）；
     * 已经在那儿、或者最后一个目的地就是它，什么也不做。
     */
    goTo(site, t = clock) {
      const last = queue.length ? queue[queue.length - 1].to : MOVING.has(current.kind) ? current.to : position;
      if (last === site) return false;
      queue.push({ to: site, t: Math.max(clock, t) });
      return true;
    },
    state() {
      return { step: current, position, moving: MOVING.has(current.kind), queued: queue.length };
    },
    get position() { return position; },
    get steps() { return steps; },
  };
}

/** 一步走了多少（0..1）；待着的一步为 null。 */
export function stepProgress(step, now) {
  if (!step || step.end == null) return null;
  if (step.end <= step.start) return 1;
  return Math.min(1, Math.max(0, (now - step.start) / (step.end - step.start)));
}
