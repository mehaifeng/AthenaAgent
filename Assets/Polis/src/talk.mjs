// 和雅典娜说话（设计稿 7.1"说"）。游戏盖住了整个中间区域，原来的输入框也在它下面——城里的对话栏是游戏模式下说话的地方。
// 底部正中一条对话栏：平时一行（左边雅典娜的小像）；点进去、按 Enter、或者在城里点雅典娜，它就展开——半身像、她此刻在做什么、
// 能写几行的输入框。Enter 托付（Shift+Enter 换行；输入法正在选字时的 Enter 只是选字），Esc 收起，草稿留着，并且随手同步回
// 这份委托自己的输入框（切回对话模式，写了一半的话还在）。她在办事时，托付键换成"请她停下"。
//
// 写进去的字是你自己的。托付走 C# 输入框的同一条路径（SendMessageCommand），这里只发三条意图：say（托付）、draft（草稿）、
// stop-turn（请她停下），都带着这份委托的 Id，C# 只认此刻显示的那一份（PolisIntents）。整块界面的文字都用 textContent，
// 输入框的内容只经 .value 读写——没有任何 HTML 拼接（12.4）。
//
// 问话（M-C）：居民卡片上的"问他那件事"把这条栏借给那位居民——左边换成他的半身像，Enter 发 ask-citizen（他那场会话的 Id + 你的问题），
// 不是 say：问话不进任何会话、不替你对雅典娜说一个字。借用期间你写给雅典娜的草稿原样收着、不同步，Esc 或回答到了就还回去。
import { drawBust } from './art/busts.mjs';

/** 一次托付最多多少字（与 C# 的 PolisIntents.MaxSayChars 一致；超过的输入框本身就打不进去）。 */
export const MAX_SAY_CHARS = 16000;
/** 一个问题最多多少字（与 C# 的 PolisCitizenAnswers.MaxQuestionChars 一致）。 */
export const MAX_QUESTION_CHARS = 300;
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
 * 问话时这条栏显示什么（纯逻辑，Node 单测）。mode：'ask' 在写问题、'asking' 问出去了在等他答。
 */
export function askView(mode, target, question, ui) {
  const name = target?.name ?? '';
  const q = String(question ?? '').trim();
  return {
    name,
    status: mode === 'asking' ? fill(ui.askWaiting, { name }) : ui.askStatus ?? '',
    // 等他答的时候栏一直展开着（状态行写着"他在想"），占位字空着，不说两遍
    placeholder: mode === 'asking' ? '' : fill(ui.askPlaceholder, { name }),
    send: ui.askSend ?? '',
    hint: ui.askHint ?? '',
    canSend: mode === 'ask' && !!target?.conversationId && q.length > 0 && q.length <= MAX_QUESTION_CHARS,
    disabled: mode === 'asking',
  };
}

/**
 * @param {HTMLElement} root  index.html 里的 #talk（结构是静态的标记，这里只填文字、读写 .value）
 * @param {{ send: (type: string, fields?: object) => void, onOpen?: () => void, onAsk?: (event: 'sent'|'cancel', info?: object) => void }} options
 */
export function createTalk(root, { send, onOpen = null, onAsk = null }) {
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
  let mode = 'talk';         // 'talk' 和雅典娜说话；'ask' 问一位居民（写问题）；'asking' 问出去了，等他答
  let askTarget = null;      // { conversationId, name, bust }
  let savedDraft = '';       // 借给居民期间收着的、你写给雅典娜的草稿
  let focusWhenShown = false;

  function drawPortrait(expression) {
    const p = open ? PORTRAIT.open : PORTRAIT.collapsed;
    const dpr = Math.min(win.devicePixelRatio || 1, 2);
    const who = mode !== 'talk' && askTarget?.bust ? askTarget.bust : 'athena';
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
    if (mode !== 'talk') return renderAsk();
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

  /** 问话时的样子：他的半身像与名字、"问"键、等他答时输入框停用。 */
  function renderAsk() {
    const view = askView(mode, askTarget, input.value, ui);
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
    drawPortrait(mode === 'asking' ? 'neutral' : 'smile');
  }

  /** 把这条栏借给一位居民（卡片上的"问他那件事"）：收着写给雅典娜的草稿，换上他，展开并聚焦。 */
  function openAsk(target) {
    if (!target?.conversationId) return false;
    if (mode === 'talk') {
      if (draftTimer != null) flushDraft();
      savedDraft = input.value;
    }
    mode = 'ask';
    askTarget = { conversationId: String(target.conversationId), name: String(target.name ?? ''), bust: target.bust ?? null };
    input.value = '';
    input.maxLength = MAX_QUESTION_CHARS;
    drawn = '';
    open = false;
    setOpen(true);
    // 还在让位（卡片这一帧才收起）：藏着的输入框拿不到焦点，等它出现再给
    if (suppressed) focusWhenShown = true;
    else input.focus();
    return true;
  }

  /** 还回来：雅典娜的草稿放回输入框（没有改过，不重新同步）。cancelled 为真时告诉城里"不问了"。 */
  function endAsk(cancelled = false) {
    if (mode === 'talk') return;
    mode = 'talk';
    askTarget = null;
    input.disabled = false;
    input.maxLength = MAX_SAY_CHARS;
    input.value = savedDraft;
    lastDraft = savedDraft;
    drawn = '';
    if (doc.activeElement === input) input.blur();
    open = true;
    setOpen(false);
    render();
    if (cancelled) onAsk?.('cancel');
  }

  /** 问出去：只发 ask-citizen（他那场会话的 Id 与问题），等他答（城里会把回答放进他的卡片）。 */
  function submitAsk() {
    const question = input.value.trim();
    if (!askView(mode, askTarget, question, ui).canSend) return false;
    send('ask-citizen', { conversationId: askTarget.conversationId, question });
    mode = 'asking';
    input.value = '';
    render();
    onAsk?.('sent', { conversationId: askTarget.conversationId, question });
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
    if (mode !== 'talk') return;   // 借给居民的这段时间，输入框里是问题，不是草稿
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
      if (focus && mode === 'ask') input.focus();
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
      if (mode === 'ask') submitAsk();
      else if (mode === 'talk') submit();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      // 写问题时、等他答时按 Esc 都是"不问了"：栏马上还给雅典娜（他的回答到了也只记在他的卡片上，不再弹出来）
      if (mode !== 'talk') endAsk(true);
      else close();
    }
  });
  // 焦点离开整条对话栏（点回城里、点了别的窗口）：草稿立刻同步，栏收成一行；正在写问题时就是不问了
  root.addEventListener('focusout', (e) => {
    if (e.relatedTarget && root.contains(e.relatedTarget)) return;
    if (mode === 'ask') { endAsk(true); return; }
    // 问出去之后输入框停用、焦点自己掉了：栏留着展开，状态行写着"他在想"
    if (mode === 'asking') return;
    flushDraft();
    setOpen(false);
  });
  // 点收起的那一行（小像、空白处）也是点进去
  root.addEventListener('click', (e) => {
    if (e.target.closest?.('button')) return;
    if (doc.activeElement !== input) openTalk({ focus: true });
  });
  sendButton.addEventListener('click', () => { if (mode === 'ask') submitAsk(); else if (mode === 'talk') submit(); });
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
        // 借给居民的时候：输入框里是问题，雅典娜的草稿先收着，还回来时再放进去
        if (mode !== 'talk') savedDraft = draft;
        else input.value = draft;
        lastDraft = draft;
        autosize();
      }
      available = !!state.conversationId;
      // 预填（委托板、居民的示范、退回）要的是和雅典娜说话：正在写的问题让位
      if (message.open && mode === 'ask') endAsk(true);
      render();
      if (message.open) openTalk({ focus: true });
    },
    open: openTalk,
    close,
    submit,
    flushDraft,
    openAsk,
    /** 回答到了（或者等太久了）：还回来。 */
    endAsk: () => endAsk(false),
    get mode() { return mode; },
    setTables(table) { ui = { ...table }; drawn = ''; render(); },
    setSuppressed(value) {
      if (suppressed === !!value) return;
      suppressed = !!value;
      if (suppressed && open && mode === 'talk') close();
      render();
      if (!suppressed && focusWhenShown && mode === 'ask') input.focus();
      focusWhenShown = false;
    },
    get isOpen() { return open; },
    get available() { return available && !suppressed; },
    get value() { return input.value; },
    set value(text) { input.value = String(text ?? ''); autosize(); render(); scheduleDraft(); },
    get state() { return { ...state, open, suppressed, available, mode, asking: askTarget?.conversationId ?? null }; },
  };
}
