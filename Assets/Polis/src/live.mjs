// 实时模式的纯逻辑（不碰 DOM、不碰 three.js，Node 单测覆盖）。应用通过 InvokeScript 约每 100 ms 推来一批事件
// （设计稿 12.3），这里把它们换成编排器认得的引擎事件：
//   - 时间：C# 给的是"这件事发生在多久以前"（ageMs，按它那一侧的单调时钟算到推送那一刻），页面按自己的时钟换算，
//     两边的时钟不必对齐；时间只进不退——编排器的输入必须有序。
//   - 地点：C# 已经算好了工作区相对路径、所在建筑、类别（它知道城里有哪些建筑），这里只按类别 / 建筑选站点。
// 规则 1（工具执行不等动画）在实时模式里是天然成立的：事件到达即入账，画面落后多少都不影响它们的时间。
import { siteForTool } from './replay.mjs';

/** 过场（航海）的节奏：最短观感时长与上限（设计稿 9.2"过场动画就是加载遮罩"）。 */
export const TRANSITION = Object.freeze({
  minMs: 1600,   // 再短读不出"出港 → 海上 → 靠岸"
  capMs: 6000,   // 再长就先靠岸，显示已经就绪的部分，其余陆续出现
});

/** 帧率策略：不可见时停；有东西在动 60 帧；空闲 30 帧（设计稿 12.6）。 */
export const FRAME = Object.freeze({
  busyFps: 60,
  idleFps: 30,
  inputHotMs: 1500,   // 刚操作过（拖镜头、点地面）的这段时间按"在动"算
});

const BUSY_STEPS = new Set(['walk', 'wander', 'teleport', 'act', 'deliver']);

/**
 * 这一帧该不该画、按多少帧画。visible = false（对话模式、窗口最小化、页面被隐藏）一律停；
 * 雅典娜在走 / 在做事 / 在交付、玩家在走、正在过场、刚刚操作过——都算"在动"，60 帧；其余（待命、沉思）30 帧。
 * @returns {0|30|60}
 */
export function framePolicy({ visible, step, playerMoving = false, transitioning = false, now = 0, lastInputAt = -Infinity }) {
  if (!visible) return 0;
  if (transitioning || playerMoving || now - lastInputAt < FRAME.inputHotMs) return FRAME.busyFps;
  if (step && BUSY_STEPS.has(step)) return FRAME.busyFps;
  return FRAME.idleFps;
}

/**
 * 过场什么时候靠岸：不早于"开始 + 最短时长"，城邦就绪后就可以靠；到了上限还没就绪，先靠岸（partial = true），
 * 已经就绪的部分（顶层建筑）先显示。readyAt 为 null 表示还没就绪。
 * @returns {{ arriveAt: number|null, partial: boolean }}
 */
export function transitionArrival(startedAt, readyAt, now, { minMs = TRANSITION.minMs, capMs = TRANSITION.capMs } = {}) {
  const earliest = startedAt + minMs;
  if (readyAt != null) return { arriveAt: Math.max(earliest, readyAt), partial: false };
  if (now >= startedAt + capMs) return { arriveAt: startedAt + capMs, partial: true };
  return { arriveAt: null, partial: false };
}

/**
 * 实时事件流：把一批批的宿主事件换成编排器事件。
 * @param {object} options
 * @param {{ buildingKeys: Set<string>, marketKeys: Set<string> }} options.city 城里的建筑（replay.cityIndex 的形状）
 */
export function createLiveFeed({ city } = {}) {
  let index = city ?? { buildingKeys: new Set(), marketKeys: new Set() };
  let lastT = -Infinity;
  const pending = [];

  function stamp(now, ageMs) {
    const age = Number.isFinite(ageMs) && ageMs > 0 ? ageMs : 0;
    const t = Math.max(lastT, now - age);
    lastT = t;
    return t;
  }

  /**
   * 收下一批宿主事件。顺序以 C# 给的为准（它按发生的先后排好）；时间只进不退。
   * 认得的类型：turn / think / tool-start / tool-end / deliver / approval；其余忽略（返回值里不计）。
   */
  function push(events, now) {
    let accepted = 0;
    for (const e of events ?? []) {
      if (!e || typeof e.type !== 'string') continue;
      const t = stamp(now, e.ageMs);
      switch (e.type) {
        case 'turn':
          pending.push({ t, type: 'turn', turn: e.turn ?? 0 });
          break;
        case 'think':
          pending.push({ t, type: 'think', reasoning: Math.max(0, Number(e.reasoning) || 0) });
          break;
        case 'tool-start':
          pending.push({
            t, type: 'tool-start', id: String(e.id), tool: e.tool ?? '', category: e.category ?? 'workshop',
            site: siteForTool(e, index), place: e.place ?? 'none', path: e.path ?? null, building: e.building ?? null,
            agents: e.agents ?? null,
          });
          break;
        case 'tool-end':
          pending.push({ t, type: 'tool-end', id: String(e.id), ok: e.ok !== false });
          break;
        case 'deliver':
          pending.push({ t, type: 'deliver', turn: e.turn ?? 0, itemId: e.itemId ?? null });
          break;
        case 'approval':
          pending.push({ t, type: e.waiting ? 'approval-start' : 'approval-end' });
          break;
        default:
          continue;
      }
      accepted++;
    }
    return accepted;
  }

  /** 把攒下的事件交给编排器（或任何有 ingest 的东西）。 */
  function drain(sink) {
    const n = pending.length;
    for (const e of pending.splice(0, n)) sink.ingest(e);
    return n;
  }

  return {
    push,
    drain,
    setCity(next) { index = next; },
    get lastT() { return lastT; },
    get pendingCount() { return pending.length; },
  };
}

/** 页面 → C# 的意图消息（封闭集合，C# 逐条校验；这里只是把形状写对）。网页没有"批准"这种意图。 */
export const INTENT_TYPES = Object.freeze([
  'ready', 'failed', 'select-conversation', 'new-commission', 'open-file', 'attach-file', 'prefill-input',
  'stop-turn', 'accept-delivery', 'return-delivery', 'read-scroll', 'relocate', 'save-player',
]);

export function intent(type, fields = {}) {
  if (!INTENT_TYPES.includes(type)) throw new Error(`polis: '${type}' is not a page intent`);
  return { v: 1, type, ...fields };
}
