// 雅典娜的动作编排器（设计稿 6.2）。纯逻辑：不碰 DOM、不碰 three.js，时间由调用方给（毫秒，回放时钟）。
//
// 输入是引擎事件（工具开始 / 结束、等模型、交付……），输出是一条呈现时间线：走、瞬移、做事、沉思、交付、待命、闲逛。
// 六条节奏规则（猫头鹰村验证过的四条 + 设计稿新增的两条）：
//   1. 工具执行永远不等动画。ingest() 只记账、立即返回；事件时间就是引擎时间，呈现落后多少都不改动它。
//   2. 动画收尾有上限。工具做完之后，一处的呈现最多是"走完这段路 + 一段动作"，两样都有上限；
//      后面有人排队时，动作最多演到"最后结束 + tailCapMs"（来晚了只演一拍）；
//      还在跑的长工具，动作演满一段后让位给排队的新地点——它在后台接着跑，画面不被它冻住。
//   3. 同一地点的连续动作合并成一次：一次走过去，一段动作覆盖全部调用。
//   4. 每次移动都要完成：走到一半的路不会被改道，闲逛打断不了赶路，新目标只能排在后面。
//   5. 画面落后超过阈值就瞬移：最旧的待演动作已经落后 lagThresholdMs 以上，就不再排队，
//      丢掉中间的站点，化作一道光直接去最新的那一处。
//   6. 等模型的时间不能出现空白：手上没有动作、这一回合还没交付的时候，她原地沉思（光一明一暗）。
//
// 时间线按"虚拟时间"推进：每一步在上一步结束的那一刻决定下一步，与帧率无关，同样的输入总是得到同样的输出。
// 开放式的步骤（沉思、待命、等工具做完的动作）在新事件到来、或者可以让位的那一刻结束。

export const DEFAULTS = Object.freeze({
  lagThresholdMs: 3000,   // 规则 5：设计稿说"阈值从 3 秒起调"
  tailCapMs: 2500,        // 规则 2：工具做完后这一处最多再演 2.5 秒
  minActMs: 1100,         // 一段动作至少演这么久才看得清
  minBeatMs: 350,         // 来晚了也至少留一拍
  settleMs: 300,          // 长工具做完后多演的一小拍，让"做完了"看得见
  teleportMs: 420,        // 化作一道光
  maxWalkMs: 5000,        // 比这更远的路不走，直接瞬移（神本来就能瞬移）；12 米 / 秒约 60 米，城里多数地方走得到
  deliverMs: 1800,        // 递上成果
  idleBeforeWanderMs: 4000,
  wanderMs: 1400,
});

/**
 * @param {object} options
 * @param {(from: string, to: string) => number} [options.travelMs] 两个地点之间走路要多久（毫秒）
 * @param {() => string} [options.wanderTarget] 闲逛去哪儿（返回一个地点名）
 * @param {() => string} [options.playerSpot] 玩家此刻所在的地点名（交付时去那里）
 * @param {string} [options.home] 起始地点
 * @param {number} [options.startAt] 时间线从哪一刻开始（毫秒）
 */
export function createOrchestrator(options = {}) {
  const p = { ...DEFAULTS, ...options };
  const travelMs = options.travelMs ?? (() => 1500);
  const wanderTarget = options.wanderTarget ?? (() => p.home ?? 'agora');
  const playerSpot = options.playerSpot ?? (() => 'player');

  const inbox = [];          // 已收到、时间线还没走到的事件（按 t 排序，同一时刻按到达顺序）
  const tools = new Map();   // id → { id, site, category, start, end, ok, meta }
  const queue = [];          // 待演的拜访：{ site, kind, toolIds, categories, firstT, turn }
  const steps = [];          // 已经排定的步骤（只增不减）
  const ingested = [];       // 规则 1 的旁证：每个事件进来时带的时间，原样记下
  let clock = 0;
  let position = p.home ?? 'agora';
  let current = null;        // 当前步骤
  let currentVisit = null;   // 当前步骤所属的拜访
  let thinking = false;
  let reasoning = 0;
  let turnActive = false;
  startStep({ kind: 'idle', at: position, start: p.startAt ?? 0, end: null });   // 一开始她在广场待命

  function ingest(event) {
    if (event == null || typeof event.t !== 'number' || !Number.isFinite(event.t)) {
      throw new TypeError('orchestrator: every event needs a finite numeric t');
    }
    ingested.push({ type: event.type, t: event.t, id: event.id });
    let i = inbox.length;
    while (i > 0 && inbox[i - 1].t > event.t) i--;
    inbox.splice(i, 0, { ...event });
  }

  /** 把时间线推进到 now，返回当前状态。时钟不倒走：回放的"后退"由调用方重建编排器。 */
  function update(now) {
    if (now < clock) return state(now);
    while (inbox.length > 0 && inbox[0].t <= now) {
      const e = inbox.shift();
      advanceTo(e.t);
      apply(e);
      decide(e.t);
    }
    advanceTo(now);
    return state(now);
  }

  // 让已排定的步骤自然走完，在每个结束点决定下一步
  function advanceTo(t) {
    for (let guard = 0; ; guard++) {
      if (guard > 100000) throw new Error('orchestrator: runaway timeline');
      if (current && current.end != null && current.end <= t) {
        const ended = current;
        current = null;
        complete(ended);                     // 走完了就在目的地开始做事
        if (!current) decide(ended.end);
        continue;
      }
      // 规则 2 的后一半：还在跑的长工具，动作演满 minActMs 后让位给排队的新地点
      if (current && current.kind === 'act' && current.end == null && queue.length > 0
        && t >= current.start + p.minActMs) {
        const at = Math.max(current.start + p.minActMs, queue[0].firstT);
        current.end = at;
        current.yielded = true;
        continue;
      }
      // 待命够久就出去走一小圈；闲逛是一次有终点的移动，同样要走完。
      // 时钟一下跳过很久（页面在后台）时不补演看不见的闲逛。
      if (current && current.kind === 'idle' && queue.length === 0
        && t >= current.start + p.idleBeforeWanderMs) {
        const at = current.start + p.idleBeforeWanderMs;
        if (t >= at + p.wanderMs) {
          endOpen(current, t);
          startStep({ kind: 'idle', at: position, start: t, end: null });
        } else {
          endOpen(current, at);
          const to = wanderTarget();
          startStep({ kind: 'wander', from: position, to, start: at, end: at + p.wanderMs });
          position = to;
        }
        continue;
      }
      break;
    }
    clock = Math.max(clock, t);
  }

  function complete(step) {
    if ((step.kind === 'walk' || step.kind === 'teleport') && step.visit) {
      startAct(step.visit, step.end);
    } else if (step.kind === 'act' || step.kind === 'deliver') {
      currentVisit = null;
    }
  }

  function apply(e) {
    switch (e.type) {
      case 'turn':
        turnActive = true;
        thinking = true;
        break;
      case 'think':
        thinking = true;
        reasoning = e.reasoning ?? 0;
        break;
      case 'tool-start': {
        thinking = false;
        const tool = { id: e.id, site: e.site, category: e.category, start: e.t, end: null, ok: null, meta: e };
        tools.set(e.id, tool);
        enqueueTool(tool, e.t);
        break;
      }
      case 'tool-end': {
        const tool = tools.get(e.id);
        if (tool) {
          tool.end = e.t;
          tool.ok = e.ok !== false;
          if (current && current.kind === 'act' && current.end == null && current.visit.toolIds.includes(e.id)) {
            scheduleActEnd(current);
          }
        }
        break;
      }
      case 'deliver':
        thinking = false;
        turnActive = false;
        queue.push({ site: 'player', kind: 'deliver', toolIds: [], categories: [], firstT: e.t, turn: e.turn });
        break;
      default:
        break;   // 未知事件忽略：编排器只认这几种
    }
  }

  // 规则 3：同一地点的连续动作合并。先看队尾；队列空着，就看正在去 / 正在做的那一处。
  function enqueueTool(tool, t) {
    const tail = queue[queue.length - 1];
    if (tail && tail.kind === 'work' && tail.site === tool.site) {
      tail.toolIds.push(tool.id);
      tail.categories.push(tool.category);
      return;
    }
    if (queue.length === 0 && currentVisit && currentVisit.kind === 'work' && currentVisit.site === tool.site
      && current && (current.kind === 'walk' || current.kind === 'teleport' || current.kind === 'act')
      && !(current.kind === 'act' && current.end != null && current.end <= t)) {
      currentVisit.toolIds.push(tool.id);
      currentVisit.categories.push(tool.category);
      if (current.kind === 'act') scheduleActEnd(current);
      return;
    }
    queue.push({ site: tool.site, kind: 'work', toolIds: [tool.id], categories: [tool.category], firstT: t, turn: null });
  }

  // 在时刻 t 决定下一步。只有开放式步骤可以被打断；走、瞬移、闲逛、有终点的动作都要演完（规则 4）。
  function decide(t) {
    if (current && current.end != null) return;
    if (current && current.kind === 'act') return;      // 等工具的动作：由 tool-end 定终点，或在 advanceTo 里让位
    if (queue.length > 0) {
      if (current) endOpen(current, t);
      startVisit(t);
      return;
    }
    const want = thinking || turnActive ? 'meditate' : 'idle';
    if (current && current.kind === want) {
      if (want === 'meditate') current.reasoning = reasoning;
      return;
    }
    if (current) endOpen(current, t);
    startStep({ kind: want, at: position, start: t, end: null, reasoning });
  }

  function startVisit(t) {
    // 规则 5：最旧的待演动作落后超过阈值，就不排队了——只留最新的一处（交付永远保留），瞬移过去
    const lag = t - queue[0].firstT;
    const behind = lag > p.lagThresholdMs;
    let skipped = 0;
    if (behind && queue.length > 1) {
      const keep = queue.filter((v, i) => v.kind === 'deliver' || i === queue.length - 1);
      skipped = queue.length - keep.length;
      queue.splice(0, queue.length, ...keep);
    }
    const visit = queue.shift();
    if (visit.kind === 'deliver') visit.site = playerSpot();
    currentVisit = visit;
    if (visit.site === position) {
      startAct(visit, t, { lag, skipped });
      return;
    }
    const walk = travelMs(position, visit.site);
    const teleport = behind || walk > p.maxWalkMs;
    startStep({
      kind: teleport ? 'teleport' : 'walk',
      from: position,
      to: visit.site,
      start: t,
      end: t + (teleport ? p.teleportMs : walk),
      visit,
      lag,
      skipped,
      reason: teleport ? (behind ? 'lag' : 'far') : null,
    });
    position = visit.site;   // 移动一旦开始，目的地就定了
  }

  function startAct(visit, t, extra = {}) {
    if (visit.kind === 'deliver') {
      startStep({ kind: 'deliver', at: visit.site, start: t, end: t + p.deliverMs, visit, turn: visit.turn, ...extra });
      return;
    }
    const step = { kind: 'act', at: visit.site, start: t, end: null, visit, category: null, ...extra };
    startStep(step);
    scheduleActEnd(step);
  }

  // 一段动作的终点（规则 2）：工具还在跑就开放着；都做完了，演满 minActMs（长工具做完后多演 settleMs）。
  // 后面有人排队时才压缩：不超过"最后结束 + tailCapMs"，来晚了至少一拍。
  // 第一版不分有没有人排队一律压缩，结果走 3.5 秒到场时工具早已结束，几乎每次到场都只剩一闪（M0 时间线审查）。
  function scheduleActEnd(step) {
    const visit = step.visit;
    step.category = visit.categories[visit.categories.length - 1];
    step.failed = visit.toolIds.some((id) => tools.get(id)?.ok === false);
    const done = visitDoneAt(visit);
    if (done == null) {
      step.end = null;
      return;
    }
    const full = Math.max(step.start + p.minActMs, done + p.settleMs);
    if (queue.length === 0) {
      step.end = full;
      return;
    }
    step.end = Math.max(step.start + p.minBeatMs, Math.min(full, done + p.tailCapMs));
    step.hurried = step.end < full;
  }

  function visitDoneAt(visit) {
    let last = -Infinity;
    for (const id of visit.toolIds) {
      const tool = tools.get(id);
      if (!tool || tool.end == null) return null;
      last = Math.max(last, tool.end);
    }
    return visit.toolIds.length === 0 ? null : last;
  }

  function startStep(step) {
    current = step;
    steps.push(step);
  }

  function endOpen(step, t) {
    if (step.end == null) step.end = Math.max(step.start, t);
  }

  function state(now) {
    return {
      now,
      step: current,
      position,
      queueLength: queue.length,
      lagMs: queue.length > 0 ? Math.max(0, now - queue[0].firstT) : 0,
      thinking,
      turnActive,
      reasoning,
    };
  }

  return {
    ingest,
    update,
    state,
    get steps() { return steps; },
    get ingested() { return ingested; },
    get tools() { return tools; },
    get params() { return p; },
  };
}

/** 步骤在时刻 now 的进度（0..1）。开放式步骤返回 null。 */
export function stepProgress(step, now) {
  if (!step || step.end == null) return null;
  if (step.end <= step.start) return 1;
  return Math.min(1, Math.max(0, (now - step.start) / (step.end - step.start)));
}
