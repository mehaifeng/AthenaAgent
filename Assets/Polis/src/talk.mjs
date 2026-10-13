// 和雅典娜说话（设计稿 7.1"说"）。游戏盖住了整个中间区域，原来的输入框也在它下面——城里的对话栏是游戏模式下说话的地方。
// 底部正中一条对话栏：平时一行（左边雅典娜的小像）；点进去、按 Enter、或者在城里点雅典娜，它就展开——半身像、她此刻在做什么、
// 能写几行的输入框。Enter 托付（Shift+Enter 换行；输入法正在选字时的 Enter 只是选字），Esc 收起，草稿留着，并且随手同步回
// 这份委托自己的输入框（切回对话模式，写了一半的话还在）。她在办事时，托付键换成"请她停下"。
//
// 写进去的字是你自己的。托付走 C# 输入框的同一条路径（SendMessageCommand），这里只发三条意图：say（托付）、draft（草稿）、
// stop-turn（请她停下），都带着这份委托的 Id，C# 只认此刻显示的那一份（PolisIntents）。整块界面的文字都用 textContent，
// 输入框的内容只经 .value 读写——没有任何 HTML 拼接（12.4）。
//
// 借用（和居民聊天、M-E 辩论）：这条栏可以借出去——和一位居民聊几句（左边换成他的半身像，Enter 发 chat；他答完栏回到"你在写"，
// 可以接着说，Esc 告辞）、在讲台前辩论（先提议题发 debate-start，再一轮轮发 debate-say）。借出去的时候 Enter 发的是那一种意图，
// 不是 say：聊天和辩论都不进任何会话、不替你对雅典娜托付一个字。借用期间你写给雅典娜的草稿原样收着、不同步，Esc 或者事情办完就还回去。
import { drawBust } from './art/busts.mjs';

/** 一次托付最多多少字（与 C# 的 PolisIntents.MaxSayChars 一致；超过的输入框本身就打不进去）。 */
export const MAX_SAY_CHARS = 16000;
/** 对一位居民说的一句话最多多少字（与 C# 的 PolisTalk.MaxLineChars 一致）。 */
export const MAX_LINE_CHARS = 300;
/** 辩论的议题与一轮论点最多多少字（与 C# 的 PolisDebate.MaxMotionChars / MaxArgumentChars 一致）。 */
export const MAX_MOTION_CHARS = 200;
export const MAX_ARGUMENT_CHARS = 600;
const DRAFT_DEBOUNCE_MS = 400;

/** 小像：收起时是一枚圆章（只露脸），展开时是整张半身像。 */
const PORTRAIT = Object.freeze({
  collapsed: { w: 36, h: 36, bustH: 80, dx: -16, dy: -13 },
  open: { w: 74, h: 92, bustH: 92, dx: 0, dy: 0 },
});

const fill = (template, vars) => String(template ?? '').replace(/\{(\w+)\}/g, (_, k) => vars[k] ?? '');

/**
 * 对话栏这一刻该显示什么（纯逻辑，Node 单测）：C# 推来的 composer 状态 + 当前草稿。
 * 能托付 = C# 说能（不在回答、不在压缩上下文）、她不在办事、草稿不是空的、知道是哪一份委托。
 */
export function talkView(state, draft, ui, open = false) {
  const busy = !!state.busy;
  const status = state.compressing ? ui.talkCompressing
    : state.waiting ? ui.talkWaiting
      : state.queued ? ui.talkQueued
        : busy ? ui.talkBusy
          : ui.talkIdle;
  return {
    status,
    expression: state.waiting ? 'neutral' : busy ? 'speak' : 'smile',
    busy,
    // 等你点头（审批）：栏上一枚红封印（点缀色只有一个意思——"轮到你了"）。2026-10-13 起这是城里唯一显示审批的地方：
    // 她不再停在门槛前（直播拿掉了），审批本身仍在原生审批窗口里
    seal: !!state.waiting,
    canSend: !!state.canSend && !busy && !!state.conversationId && String(draft ?? '').trim().length > 0,
    showStop: busy,
    // 收起时状态行看不见，"她在等你点头 / 她在办事"写在占位字里；展开时状态行已经说了，占位字只是邀请
    placeholder: !open && state.waiting ? ui.talkWaiting : busy && !open ? ui.talkBusy : ui.talkPlaceholder,
  };
}

/**
 * 借出去的栏这一刻显示什么（纯逻辑，Node 单测）。mode：'lent' 在写、'waiting' 发出去了在等。lent 是 chatLend / debateLend 给的那一份。
 */
export function lentView(mode, lent, text) {
  const t = String(text ?? '').trim();
  return {
    name: lent?.name ?? '',
    status: mode === 'waiting' ? lent?.waitingStatus ?? '' : lent?.status ?? '',
    // 等的时候栏一直展开着（状态行写着在等什么），占位字空着，不说两遍
    placeholder: mode === 'waiting' ? '' : lent?.placeholder ?? '',
    send: lent?.sendLabel ?? '',
    hint: lent?.hint ?? '',
    canSend: mode === 'lent' && !!lent?.id && t.length > 0 && t.length <= (lent.maxChars ?? MAX_SAY_CHARS),
    disabled: mode === 'waiting',
  };
}

/** 借给一位居民聊天：他的半身像与名字，Enter 发 chat（who 是他的会话 Id）；他答完了栏回到"你在写"（resumeLent）。 */
export function chatLend(target, ui) {
  const name = String(target?.name ?? '');
  const id = target?.conversationId ? String(target.conversationId) : null;
  return {
    kind: 'chat', waitingKind: 'chatting', id, name, bust: target?.bust ?? null,
    status: fill(ui.chatStatus, { name }), waitingStatus: fill(ui.chatWaiting, { name }), placeholder: fill(ui.chatPlaceholder, { name }),
    sendLabel: ui.chatSend ?? '', hint: ui.chatHint ?? '', maxChars: MAX_LINE_CHARS,
    intent: (text) => ['chat', { who: id, text }],
  };
}

/** 借给讲台前的辩论（M-E）：stage 'motion' 提议题（debate-start），'argue' 第 round 轮的论点（debate-say）。左边是雅典娜——她是你的对手。 */
export function debateLend({ stage, round = 1, rounds = 3, motion = '', id = 'debate' }, ui) {
  const argue = stage === 'argue';
  return {
    kind: 'debate', waitingKind: 'debating', id: String(id), name: ui.debateName ?? '', bust: 'athena',
    status: argue ? fill(ui.debateArgueStatus, { motion }) : ui.debateMotionStatus ?? '',
    waitingStatus: argue ? ui.debateAthenaThinking ?? '' : '',
    placeholder: argue ? fill(ui.debateArguePlaceholder, { round, rounds }) : ui.debateMotionPlaceholder ?? '',
    sendLabel: ui.debateSend ?? '', hint: ui.debateHint ?? '', maxChars: argue ? MAX_ARGUMENT_CHARS : MAX_MOTION_CHARS,
    intent: (text) => (argue ? ['debate-say', { text }] : ['debate-start', { motion: text }]),
  };
}

/** 聊天时这条栏显示什么（测试用）：mode 'chat' 在写、'chatting' 说出去了在等他答。 */
export function chatView(mode, target, text, ui) {
  return lentView(mode === 'chatting' ? 'waiting' : mode === 'chat' ? 'lent' : mode, chatLend(target, ui), text);
}

/**
 * @param {HTMLElement} root  index.html 里的 #talk（结构是静态的标记，这里只填文字、读写 .value）
 * @param {{ send: (type: string, fields?: object) => void, onOpen?: () => void, onLent?: (event: 'sent'|'cancel', info: { kind: string, id: string, text?: string }) => void }} options
 */
export function createTalk(root, { send, onOpen = null, onLent = null }) {
  const doc = root.ownerDocument;
  const win = doc.defaultView;
  const q = (selector) => root.querySelector(selector);
  const portrait = q('.talk-bust');
  const nameEl = q('.talk-name');
  const statusEl = q('.talk-status');
  const attachmentsEl = q('.talk-attachments');
  const input = q('.talk-input');
  const sendButton = q('.talk-send');
  const stopButton = q('.talk-stop');
  const hintEl = q('.talk-hint');
  input.maxLength = MAX_SAY_CHARS;

  let ui = {};
  let state = { conversationId: null, busy: false, queued: false, waiting: false, compressing: false, canSend: false, attachments: [] };
  let available = false;     // 收到第一条 composer 之前不出现（还不知道是哪一份委托）
  let suppressed = false;    // 画面中间被卡片、面板、海图、雾、过场占着
  let open = false;
  let draftTimer = null;
  let lastDraft = null;      // 最近一次同步给 C# 的草稿（相同的不再发）
  let drawn = '';
  let mode = 'talk';         // 'talk' 和雅典娜说话；'lent' 借出去了（在写）；'waiting' 发出去了，在等
  let lent = null;           // chatLend / debateLend 给的那一份
  let savedDraft = '';       // 借出去期间收着的、你写给雅典娜的草稿
  let focusWhenShown = false;

  function drawPortrait(expression) {
    const p = open ? PORTRAIT.open : PORTRAIT.collapsed;
    const dpr = Math.min(win.devicePixelRatio || 1, 2);
    const who = mode !== 'talk' && lent?.bust ? lent.bust : 'athena';
    const key = `${typeof who === 'object' ? who.key ?? 'citizen' : who}:${expression}:${open}:${dpr}`;
    if (key === drawn) return;
    drawn = key;
    portrait.width = Math.round(p.w * dpr);
    portrait.height = Math.round(p.h * dpr);
    portrait.style.width = `${p.w}px`;
    portrait.style.height = `${p.h}px`;
    const ctx = portrait.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, p.w, p.h);
    drawBust(ctx, who, expression, p.dx, p.dy, p.bustH);
  }

  /** 输入框随字数长高（收起时最多三行、展开时最多八行）。藏着的时候量不出高度（scrollHeight 为 0），等它出现再量。 */
  function autosize() {
    if (root.hidden) return;
    input.style.height = 'auto';
    const natural = input.scrollHeight;   // 内容 + 内边距（输入框是 border-box，再加上边框）
    if (natural > 0) input.style.height = `${Math.min(natural + input.offsetHeight - input.clientHeight, open ? 168 : 60)}px`;
  }

  function render() {
    if (mode !== 'talk') return renderLent();
    input.disabled = false;
    root.classList.remove('asking');
    const view = talkView(state, input.value, ui, open);
    nameEl.textContent = ui.talkName ?? '';
    statusEl.textContent = view.status ?? '';
    input.placeholder = view.placeholder ?? '';
    sendButton.textContent = ui.talkSend ?? '';
    sendButton.disabled = !view.canSend;
    sendButton.hidden = view.showStop;
    stopButton.textContent = ui.talkStop ?? '';
    stopButton.hidden = !view.showStop;
    hintEl.textContent = ui.talkHint ?? '';
    const names = state.attachments ?? [];
    attachmentsEl.hidden = names.length === 0;
    attachmentsEl.textContent = names.length ? fill(ui.talkAttachments, { names: names.join(' · ') }) : '';
    root.classList.toggle('open', open);
    root.classList.toggle('busy', view.busy);
    root.classList.toggle('waiting', view.seal);
    const hidden = !available || suppressed;
    const appearing = root.hidden && !hidden;
    root.hidden = hidden;
    if (appearing) autosize();
    drawPortrait(view.expression);
  }

  /** 借出去时的样子：借用者的半身像与名字、它自己的按键与提示、等的时候输入框停用。 */
  function renderLent() {
    const view = lentView(mode, lent, input.value);
    nameEl.textContent = view.name;
    statusEl.textContent = view.status ?? '';
    input.placeholder = view.placeholder ?? '';
    input.disabled = view.disabled;
    sendButton.textContent = view.send;
    sendButton.disabled = !view.canSend;
    sendButton.hidden = false;
    stopButton.hidden = true;
    hintEl.textContent = view.hint;
    attachmentsEl.hidden = true;
    attachmentsEl.textContent = '';
    root.classList.toggle('open', open);
    root.classList.remove('busy');
    root.classList.remove('waiting');
    root.classList.add('asking');
    const hidden = suppressed;
    const appearing = root.hidden && !hidden;
    root.hidden = hidden;
    if (appearing) autosize();
    drawPortrait(mode === 'waiting' ? 'neutral' : 'smile');
  }

  /**
   * 把这条栏借出去（问一位居民、讲台前辩论）：第一次借时收着写给雅典娜的草稿；已经借着的（辩论的下一轮）只换一份说法。
   * 展开并聚焦，输入框清空。
   */
  function lend(config) {
    if (!config?.id) return false;
    if (mode === 'talk') {
      if (draftTimer != null) flushDraft();
      savedDraft = input.value;
    }
    mode = 'lent';
    lent = config;
    input.value = '';
    input.disabled = false;
    input.maxLength = config.maxChars ?? MAX_SAY_CHARS;
    drawn = '';
    open = false;
    setOpen(true);
    // 还在让位（卡片这一帧才收起）：藏着的输入框拿不到焦点，等它出现再给
    if (suppressed) focusWhenShown = true;
    else input.focus();
    return true;
  }

  /** 还回来：雅典娜的草稿放回输入框（没有改过，不重新同步）。cancelled 为真时告诉城里"不问了 / 不辩了"。 */
  function endLent(cancelled = false) {
    if (mode === 'talk') return;
    const was = lent;
    mode = 'talk';
    lent = null;
    input.disabled = false;
    input.maxLength = MAX_SAY_CHARS;
    input.value = savedDraft;
    lastDraft = savedDraft;
    drawn = '';
    if (doc.activeElement === input) input.blur();
    open = true;
    setOpen(false);
    render();
    if (cancelled && was) onLent?.('cancel', { kind: was.kind, id: was.id });
  }

  /** 他答完了（聊天）：栏回到"你在写"，输入框聚焦，接着说。不是在等的时候什么也不做。 */
  function resumeLent() {
    if (mode !== 'waiting') return false;
    mode = 'lent';
    input.disabled = false;
    render();
    if (suppressed) focusWhenShown = true;
    else input.focus();
    return true;
  }

  /** 发出去：只发借用者那一种意图（chat / debate-start / debate-say），然后等。 */
  function submitLent() {
    const text = input.value.trim();
    if (!lentView(mode, lent, text).canSend) return false;
    const [type, fields] = lent.intent(text);
    send(type, fields);
    mode = 'waiting';
    input.value = '';
    render();
    onLent?.('sent', { kind: lent.kind, id: lent.id, text });
    return true;
  }

  function setOpen(value) {
    if (open === value) return;
    open = value;
    render();
    autosize();
    if (open) onOpen?.();
  }

  function flushDraft() {
    if (draftTimer != null) { win.clearTimeout(draftTimer); draftTimer = null; }
    if (mode !== 'talk') return;   // 借出去的这段时间，输入框里是问题或论点，不是草稿
    if (!state.conversationId || input.value === lastDraft) return;
    lastDraft = input.value;
    send('draft', { conversationId: state.conversationId, text: input.value });
  }

  function scheduleDraft() {
    if (draftTimer != null) win.clearTimeout(draftTimer);
    draftTimer = win.setTimeout(flushDraft, DRAFT_DEBOUNCE_MS);
  }

  /** 托付：把草稿交给 C#（它走输入框的发送命令），清空、收起，把键盘还给城（马上就能走动、看她出发）。 */
  function submit() {
    const text = input.value;
    if (!talkView(state, text, ui).canSend) return false;
    if (draftTimer != null) { win.clearTimeout(draftTimer); draftTimer = null; }
    send('say', { conversationId: state.conversationId, text });
    input.value = '';
    lastDraft = '';
    // 先按"她接手了"显示；C# 马上推来真实状态——办事中，或者被拒绝时连同草稿一起退回来
    state = { ...state, busy: true, canSend: false };
    close();
    return true;
  }

  function openTalk({ focus = true } = {}) {
    if (mode !== 'talk') {
      if (suppressed) return false;
      setOpen(true);
      if (focus && mode === 'lent') input.focus();
      return true;
    }
    if (!available || suppressed) return false;
    setOpen(true);
    if (focus) {
      input.focus();
      const end = input.value.length;
      input.setSelectionRange(end, end);
    }
    return true;
  }

  function close() {
    flushDraft();
    if (doc.activeElement === input) input.blur();
    setOpen(false);
    render();
  }

  // —— 事件 ——
  input.addEventListener('focus', () => setOpen(true));
  input.addEventListener('input', () => { autosize(); render(); if (mode === 'talk') scheduleDraft(); });
  input.addEventListener('keydown', (e) => {
    if (e.isComposing || e.keyCode === 229) return;   // 输入法正在选字：这个 Enter 是确认候选词
    if (e.key === 'Enter' && !e.shiftKey && !e.altKey && !e.ctrlKey && !e.metaKey) {
      e.preventDefault();
      if (mode === 'lent') submitLent();
      else if (mode === 'talk') submit();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      // 借出去时按 Esc（写着、等着都算）是"不问了 / 不辩了"：栏马上还给雅典娜
      if (mode !== 'talk') endLent(true);
      else close();
    }
  });
  // 焦点离开整条对话栏（点回城里、点了别的窗口）：草稿立刻同步，栏收成一行；正在写问题时就是不问了
  root.addEventListener('focusout', (e) => {
    if (e.relatedTarget && root.contains(e.relatedTarget)) return;
    // 聊天时点回城里（去走动了）：就是告辞了。辩论不这样——你会转头看一眼陪审团，焦点回来还接着辩
    if (mode === 'lent' && lent?.kind === 'chat') { endLent(true); return; }
    // 借出去时（尤其发出去之后输入框停用、焦点自己掉了）：栏留着展开，状态行写着在等什么
    if (mode !== 'talk') return;
    flushDraft();
    setOpen(false);
  });
  // 点收起的那一行（小像、空白处）也是点进去
  root.addEventListener('click', (e) => {
    if (e.target.closest?.('button')) return;
    if (doc.activeElement !== input) openTalk({ focus: true });
  });
  sendButton.addEventListener('click', () => { if (mode === 'lent') submitLent(); else if (mode === 'talk') submit(); });
  stopButton.addEventListener('click', () => { if (state.busy) send('stop-turn'); });
  win.addEventListener('blur', flushDraft);

  return {
    /**
     * C# 推来的 composer：{ conversationId, busy, queued, waiting, compressing, canSend, attachments, draft?, open? }。
     * 换了一份委托、或者带着 draft（进场、预填）时换掉输入框里的字；带着 open 时展开并聚焦（预填之后由你看一眼再托付）。
     */
    apply(message) {
      const switched = message.conversationId !== state.conversationId;
      if (switched && draftTimer != null) flushDraft();   // 上一份委托没同步完的草稿先送走
      state = {
        conversationId: message.conversationId ?? null,
        busy: !!message.busy,
        queued: !!message.queued,
        waiting: !!message.waiting,
        compressing: !!message.compressing,
        canSend: !!message.canSend,
        attachments: Array.isArray(message.attachments) ? message.attachments.map(String) : [],
      };
      if (switched || typeof message.draft === 'string') {
        const draft = typeof message.draft === 'string' ? message.draft : '';
        // 借出去的时候：输入框里是问题或论点，雅典娜的草稿先收着，还回来时再放进去
        if (mode !== 'talk') savedDraft = draft;
        else input.value = draft;
        lastDraft = draft;
        autosize();
      }
      available = !!state.conversationId;
      // 预填（委托板、居民的示范、退回）要的是和雅典娜说话：正在聊的那一位让位
      if (message.open && mode !== 'talk') endLent(true);
      render();
      if (message.open) openTalk({ focus: true });
    },
    open: openTalk,
    close,
    submit,
    flushDraft,
    /** 借给一位居民聊天。 */
    openChat: (target) => lend(chatLend(target, ui)),
    /** 他答完了：回到"你在写"。 */
    resumeLent,
    /** 借给讲台前的辩论（M-E）：{ stage: 'motion' | 'argue', round, rounds, motion, id }。 */
    openDebate: (options) => lend(debateLend(options, ui)),
    /** 事情办完（辩论进了裁决、换了城）：还回来。 */
    endLent: () => endLent(false),
    /** 'talk'，或者借出去时借用者的说法：'chat' / 'chatting'、'debate' / 'debating'。 */
    get mode() { return mode === 'talk' ? 'talk' : mode === 'lent' ? lent.kind : lent.waitingKind; },
    get lentTo() { return lent ? { kind: lent.kind, id: lent.id } : null; },
    setTables(table) { ui = { ...table }; drawn = ''; render(); },
    setSuppressed(value) {
      if (suppressed === !!value) return;
      suppressed = !!value;
      if (suppressed && open && mode === 'talk') close();
      render();
      if (!suppressed && focusWhenShown && mode === 'lent') input.focus();
      focusWhenShown = false;
    },
    get isOpen() { return open; },
    get available() { return available && !suppressed; },
    get value() { return input.value; },
    set value(text) { input.value = String(text ?? ''); autosize(); render(); scheduleDraft(); },
    get state() {
      const exposed = mode === 'talk' ? 'talk' : mode === 'lent' ? lent.kind : lent.waitingKind;
      return { ...state, open, suppressed, available, mode: exposed, chatting: lent?.kind === 'chat' ? lent.id : null, lentTo: lent?.kind ?? null };
    },
  };
}
