// 玩家与镜头的操作（纯逻辑，不碰 DOM、不碰 three.js，Node 单测覆盖）。
//   走：WASD 或方向键，相对镜头——W / ↑ 朝画面上方（离开镜头）走，D / → 朝画面右边走；按住 Shift 跑。
//   看：三种视角——自由（缺省）、跟着你、跟着雅典娜。左键拖动绕着转、右键（或 Shift + 左键）拖动平移、滚轮缩放；
//       自由视角下镜头不跟任何人，只在你走到快出画面时跟一下。
//   点：鼠标点物件互动。点地面不再走路——走路交给键盘（2026-10-11 实测之后的决定）。

export const VIEW_MODES = Object.freeze(['free', 'player', 'athena']);

export const VIEW = Object.freeze({
  minElevation: 18,     // 再低就成了平视：街道和地块读不出来
  maxElevation: 72,     // 再高就成了俯视图：建筑只剩屋顶
  degPerPx: 0.32,       // 横向拖动：每像素转多少度
  tiltDegPerPx: 0.2,    // 纵向拖动：每像素俯仰多少度
  dragPx: 6,            // 按下之后移动超过这么多像素才算拖动，之内仍然是一次点击——点击不会顺手把镜头拧歪
  deadZone: 0.55,       // 自由视角：你走到画面中心这个方框（NDC）之外，镜头才跟一下
});

/** 按住 Shift 跑起来是走的几倍。 */
export const RUN_FACTOR = 1.75;

/** 键位 → 方向。按物理键位（KeyboardEvent.code）认：换了键盘布局、开着中文输入法，W 还是那个位置上的键。 */
export const MOVE_KEYS = Object.freeze({
  KeyW: 'up', ArrowUp: 'up', KeyS: 'down', ArrowDown: 'down',
  KeyA: 'left', ArrowLeft: 'left', KeyD: 'right', ArrowRight: 'right',
});

/** 视角按 自由 → 跟着你 → 跟着雅典娜 的顺序轮换（V 键）。 */
export function nextView(mode) {
  const i = VIEW_MODES.indexOf(mode);
  return VIEW_MODES[(i + 1) % VIEW_MODES.length];
}

/**
 * 按住的方向 → 地面上的单位方向（相对镜头）。azimuth 是镜头在目标点的哪个方位（弧度；0 = 镜头在正南 +z、朝北看，
 * 与 three.js 的 Spherical.theta 是同一个量）。都没按、或者相反的两个键互相抵消时返回 null。
 * @param {Set<string>} pressed 'up' | 'down' | 'left' | 'right'
 */
export function moveDirection(pressed, azimuth) {
  const f = (pressed.has('up') ? 1 : 0) - (pressed.has('down') ? 1 : 0);
  const r = (pressed.has('right') ? 1 : 0) - (pressed.has('left') ? 1 : 0);
  if (f === 0 && r === 0) return null;
  // 画面"上方"是视线投到地面上的方向（从镜头指向目标）；画面右边是它顺时针转 90°
  const fx = -Math.sin(azimuth), fz = -Math.cos(azimuth);
  const rx = -fz, rz = fx;
  const x = fx * f + rx * r, z = fz * f + rz * r;
  const len = Math.hypot(x, z);
  return { x: x / len, z: z / len };
}

/** 撞上东西时往两边偏着试的角度（度）。到 60° 为止：再偏，朝前的分量就小过贴墙的余量，正对着墙按键会一点点蹭着横移。 */
export const SLIDE_ANGLES = Object.freeze([20, 40, 60]);

/**
 * 走一步，撞上了就沿着滑：先试整步；不行只走 x、再不行只走 z（房子都朝南，墙贴着坐标轴，这两步正好沿墙滑）；
 * 还不行就朝着原方向走半步、四分之一步（先贴到墙上，而不是隔着一道缝横移）；最后把这一步往左、往右各偏 20°、40°、60°
 * 再试（步长乘以 cos，速度不会因为贴墙而变快），哪个先能走就走哪个；都不行原地不动。
 * M3 之前只有前两种：房子是方的时够用，树干、柱子、器物都挡路之后，斜着撞上一根柱子就卡住了。
 * 正对着一面墙按方向键：贴上去就不再动——偏 60° 的一步朝墙的分量（len/4）不小于四分之一步留下的缝，全被挡住。
 * 站在不能走的地方（城变了，存档里的位置落进了一座新建筑）时任何一步都放行，好让人走出来。
 * @param {(p: {x: number, z: number}) => boolean} walkable
 */
export function slide(pos, delta, walkable) {
  const next = { x: pos.x + delta.x, z: pos.z + delta.z };
  if (!walkable(pos)) return next;
  if (walkable(next)) return next;
  if (delta.x !== 0) {
    const alongX = { x: pos.x + delta.x, z: pos.z };
    if (walkable(alongX)) return alongX;
  }
  if (delta.z !== 0) {
    const alongZ = { x: pos.x, z: pos.z + delta.z };
    if (walkable(alongZ)) return alongZ;
  }
  for (const f of [0.5, 0.25]) {
    const p = { x: pos.x + delta.x * f, z: pos.z + delta.z * f };
    if (walkable(p)) return p;
  }
  const len = Math.hypot(delta.x, delta.z);
  if (len === 0) return { x: pos.x, z: pos.z };
  const heading = Math.atan2(delta.x, delta.z);
  for (const deg of SLIDE_ANGLES) {
    const rad = (deg * Math.PI) / 180;
    const step = len * Math.cos(rad);
    for (const side of [1, -1]) {
      const a = heading + side * rad;
      const p = { x: pos.x + Math.sin(a) * step, z: pos.z + Math.cos(a) * step };
      if (walkable(p)) return p;
    }
  }
  return { x: pos.x, z: pos.z };
}

/**
 * 拖动 → 新的方位角与仰角（度）。向右拖，景物跟着手向右转（镜头向左绕）；向下拖，看得更"俯"——与 OrbitControls 的手感一致。
 * 仰角夹在 VIEW 的上下限之间，方位角不限。
 */
export function orbitAngles(azimuthDeg, elevationDeg, dxPx, dyPx) {
  return {
    azimuth: azimuthDeg - dxPx * VIEW.degPerPx,
    elevation: Math.min(VIEW.maxElevation, Math.max(VIEW.minElevation, elevationDeg + dyPx * VIEW.tiltDegPerPx)),
  };
}

/**
 * 自由视角的"别让人走出画面"：给出人在画面上的位置（NDC，-1..1），返回画面需要挪多少（NDC）才能让人回到中心
 * margin 的方框之内；人在方框里时返回 null。
 */
export function deadZoneShift(ndc, margin = VIEW.deadZone) {
  const clamp = (v) => Math.max(-margin, Math.min(margin, v));
  const dx = ndc.x - clamp(ndc.x), dy = ndc.y - clamp(ndc.y);
  return dx === 0 && dy === 0 ? null : { x: dx, y: dy };
}

/** 朝向平滑地转过去，走较短的那一边。k：这一帧转过去的比例（0..1）。 */
export function turnToward(current, target, k) {
  const d = Math.atan2(Math.sin(target - current), Math.cos(target - current));
  return current + d * k;
}

/** 键盘事件是不是落在一个能打字的地方（对话栏）：那时 W、A、S、D 是字，不是脚步；Enter 是换行或发送，不是"开口"。 */
export function isTypingTarget(target) {
  if (!target) return false;
  const tag = String(target.tagName ?? '').toUpperCase();
  return tag === 'TEXTAREA' || tag === 'INPUT' || tag === 'SELECT' || target.isContentEditable === true;
}
