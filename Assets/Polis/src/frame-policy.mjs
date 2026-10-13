// 页面的节奏（纯逻辑，不碰 DOM、不碰 three.js，Node 单测 page.test.mjs）：什么时候画、画多快（设计稿 12.6），
// 以及换城的航海过场什么时候靠岸（9.2）。原来和实时事件流一起在 live.mjs 里；直播拿掉之后（2026-10-13）只剩这两样。

/** 过场（航海）的节奏：最短观感时长与上限（设计稿 9.2"过场动画就是加载遮罩"）。 */
export const TRANSITION = Object.freeze({
  minMs: 1600,   // 再短读不出"出港 → 海上 → 靠岸"
  capMs: 6000,   // 再长就先靠岸，显示已经就绪的部分，其余陆续出现
});

/** 帧率策略：不可见时停；有东西在动 60 帧；空闲 30 帧（设计稿 12.6）。 */
export const FRAME = Object.freeze({
  busyFps: 60,
  idleFps: 30,
  inputHotMs: 1500,   // 刚操作过（拖镜头、按方向键）的这段时间按"在动"算
});

/** 雅典娜这两种步骤算"在动"（athena.mjs）：走路、化作一道光。待着、和你说话是空闲。 */
const BUSY_STEPS = new Set(['walk', 'teleport']);

/**
 * 这一帧该不该画、按多少帧画。visible = false（对话模式、窗口最小化、页面被隐藏）一律停；
 * 雅典娜在走 / 在瞬移、玩家在走、正在过场、刚刚操作过——都算"在动"，60 帧；其余 30 帧。
 * @param {{ visible: boolean, step?: string|null, playerMoving?: boolean, transitioning?: boolean, now?: number, lastInputAt?: number }} state
 *   step：雅典娜此刻那一步的种类（athena.mjs 的 idle / walk / teleport）
 * @returns {0|30|60}
 */
export function framePolicy({ visible, step = null, playerMoving = false, transitioning = false, now = 0, lastInputAt = -Infinity }) {
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
