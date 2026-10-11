// 和雅典娜说话（设计稿 7.1"说"）。游戏盖住了整个中间区域，原来的输入框也在它下面——城里的对话栏是游戏模式下说话的地方。
// 底部正中一条对话栏：平时一行（左边雅典娜的小像）；点进去、按 Enter、或者在城里点雅典娜，它就展开——半身像、她此刻在做什么、
// 能写几行的输入框。Enter 托付（Shift+Enter 换行；输入法正在选字时的 Enter 只是选字），Esc 收起，草稿留着，并且随手同步回
// 这份委托自己的输入框（切回对话模式，写了一半的话还在）。她在办事时，托付键换成"请她停下"。
//
// 写进去的字是你自己的。托付走 C# 输入框的同一条路径（SendMessageCommand），这里只发三条意图：say（托付）、draft（草稿）、
// stop-turn（请她停下），都带着这份委托的 Id，C# 只认此刻显示的那一份（PolisIntents）。整块界面的文字都用 textContent，
// 输入框的内容只经 .value 读写——没有任何 HTML 拼接（12.4）。
import { drawBust } from './art/busts.mjs';

/** 一次托付最多多少字（与 C# 的 PolisIntents.MaxSayChars 一致；超过的输入框本身就打不进去）。 */
export const MAX_SAY_CHARS = 16000;
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
    canSend: !!state.canSend && !busy && !!state.conversationId && String(draft ?? '').trim().length > 0,
    showStop: busy,
    // 收起时状态行看不见，"她在办事"写在占位字里；展开时状态行已经说了，占位字只是邀请
    placeholder: busy && !open ? ui.talkBusy : ui.talkPlaceholder,
  };
}

/**
 * @param {HTMLElement} root  index.html 里的 #talk（结构是静态的标记，这里只填文字、读写 .value）
 * @param {{ send: (type: string, fields?: object) => void, onOpen?: () => void }} options
 */
export function createTalk(root, { send, onOpen = null }) {
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

  function drawPortrait(expression) {
    const p = open ? PORTRAIT.open : PORTRAIT.collapsed;
    const dpr = Math.min(win.devicePixelRatio || 1, 2);
    const key = `${expression}:${open}:${dpr}`;
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
    drawBust(ctx, 'athena', expression, p.dx, p.dy, p.bustH);
  }

  /** 输入框随字数长高（收起时最多三行、展开时最多八行）。藏着的时候量不出高度（scrollHeight 为 0），等它出现再量。 */
  function autosize() {
    if (root.hidden) return;
    input.style.height = 'auto';
    const natural = input.scrollHeight;   // 内容 + 内边距（输入框是 border-box，再加上边框）
    if (natural > 0) input.style.height = `${Math.min(natural + input.offsetHeight - input.clientHeight, open ? 168 : 60)}px`;
  }

  function render() {
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
    root.classList.toggle('waiting', !!state.waiting);
    const hidden = !available || suppressed;
    const appearing = root.hidden && !hidden;
    root.hidden = hidden;
    if (appearing) autosize();
    drawPortrait(view.expression);
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
  input.addEventListener('input', () => { autosize(); render(); scheduleDraft(); });
  input.addEventListener('keydown', (e) => {
    if (e.isComposing || e.keyCode === 229) return;   // 输入法正在选字：这个 Enter 是确认候选词
    if (e.key === 'Enter' && !e.shiftKey && !e.altKey && !e.ctrlKey && !e.metaKey) {
      e.preventDefault();
      submit();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      close();
    }
  });
  // 焦点离开整条对话栏（点回城里、点了别的窗口）：草稿立刻同步，栏收成一行
  root.addEventListener('focusout', (e) => {
    if (e.relatedTarget && root.contains(e.relatedTarget)) return;
    flushDraft();
    setOpen(false);
  });
  // 点收起的那一行（小像、空白处）也是点进去
  root.addEventListener('click', (e) => {
    if (e.target.closest?.('button')) return;
    if (doc.activeElement !== input) openTalk({ focus: true });
  });
  sendButton.addEventListener('click', () => submit());
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
        input.value = typeof message.draft === 'string' ? message.draft : '';
        lastDraft = input.value;
        autosize();
      }
      available = !!state.conversationId;
      render();
      if (message.open) openTalk({ focus: true });
    },
    open: openTalk,
    close,
    submit,
    flushDraft,
    setTables(table) { ui = { ...table }; drawn = ''; render(); },
    setSuppressed(value) {
      if (suppressed === !!value) return;
      suppressed = !!value;
      if (suppressed && open) close();
      render();
    },
    get isOpen() { return open; },
    get available() { return available && !suppressed; },
    get value() { return input.value; },
    set value(text) { input.value = String(text ?? ''); autosize(); render(); scheduleDraft(); },
    get state() { return { ...state, open, suppressed, available }; },
  };
}
