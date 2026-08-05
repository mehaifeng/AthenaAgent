// Office 预览前端入口（ES module）。
// 由 index.html 加载，按 ?type=pdf|docx|xlsx|pptx 分派到对应渲染库；
// 文件经 /file/{sessionId}?t={token} 由本地回环服务器只读提供（支持 Range）。
// 主题由 App 侧在导航完成后经 setTheme(name) 推送（name: 'dark'|'light'）。

const params = new URLSearchParams(location.search);
const type = params.get('type') || '';
const fileId = params.get('file');
const token = params.get('t');
const themeParam = params.get('theme');
const langParam = params.get('lang');

const theme = themeParam === 'dark' ? 'dark' : 'light';
const lang = langParam === 'en-US' ? 'en-US' : 'zh-CN';
const fileUrl = `/file/${encodeURIComponent(fileId || '')}?t=${encodeURIComponent(token || '')}`;
const fileName = decodeURIComponent(params.get('name') || '');

const I18N = {
  'zh-CN': {
    loading: '正在加载预览…',
    failed: '预览加载失败，请重试',
    retry: '重试',
    pages: '{cur} / {total} 页',
    slides: '{n} 张幻灯片',
    invalidFile: '文件不存在或已删除',
    emptySheet: '（空工作表）'
  },
  'en-US': {
    loading: 'Loading preview…',
    failed: 'Preview failed to load',
    retry: 'Retry',
    pages: 'Page {cur} / {total}',
    slides: '{n} slides',
    invalidFile: 'File not found or removed',
    emptySheet: '(empty sheet)'
  }
};
const t = I18N[lang];

const container = document.getElementById('container');
const overlay = document.getElementById('overlay');
const msgEl = document.getElementById('msg');
const retryBtn = document.getElementById('retryBtn');
const progressEl = document.getElementById('progress');
const fileNameEl = document.getElementById('fileName');
const metaEl = document.getElementById('fileMeta');

retryBtn.textContent = t.retry;
retryBtn.addEventListener('click', () => location.reload());

function showLoading(message) {
  overlay.classList.remove('hidden');
  document.getElementById('spinner').style.display = '';
  retryBtn.classList.add('hidden');
  msgEl.textContent = message || t.loading;
}

function showError(message) {
  overlay.classList.remove('hidden');
  document.getElementById('spinner').style.display = 'none';
  retryBtn.classList.remove('hidden');
  msgEl.textContent = message || t.failed;
}

function hideOverlay() {
  overlay.classList.add('hidden');
}

function setMeta(text) {
  metaEl.textContent = text || '';
}

// ---------------------------------------------------------------- 缩放
// Ctrl+滚轮与触控板双指捏合（macOS 会转为 ctrlKey 的 wheel 事件）统一在此处理：
// 嵌入式 WKWebView/WebView2 没有浏览器壳的页面缩放 UI，必须由页面自行实现。
// capture 阶段拦截并阻止传播，避免渲染库（如 pptx-viewer）重复处理。
// 各渲染器在就绪后覆盖 __applyZoom 提供各自的缩放实现。
let zoomLevel = 1;
const ZOOM_MIN = 0.25;
const ZOOM_MAX = 4;
const ZOOM_STEP = 1.1;

// 默认实现：容器 CSS zoom（docx/xlsx 的 DOM 渲染直接受益）；pdf/pptx 覆盖为各自实现。
window.__applyZoom = (zoom) => { container.style.zoom = zoom; };

function applyZoom(next) {
  zoomLevel = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, next));
  const pct = Math.round(zoomLevel * 100);
  progressEl.textContent = pct + '%';
  progressEl.title = pct + '%（点击重置为 100%）';
  if (window.__applyZoom) window.__applyZoom(zoomLevel);
}

document.addEventListener('wheel', (e) => {
  if (!e.ctrlKey) return;
  e.preventDefault();
  e.stopPropagation();
  applyZoom(zoomLevel * (e.deltaY < 0 ? ZOOM_STEP : 1 / ZOOM_STEP));
}, { capture: true, passive: false });

progressEl.addEventListener('click', () => applyZoom(1));

// ---------------------------------------------------------------- 主题
// 初始值取 URL 的 theme 参数（打开文件时的主题）；运行时由 App 侧经
// window.setTheme(name) 推送（OfficePreviewBridge 在导航完成与 App.ThemeChanged 时调用）。
// 默认实现切换 body 的 CSS 变量（pdf/docx/xlsx 的 chrome 即时生效）；
// pptx 等渲染器在就绪后覆盖 __applyTheme 提供各自的主题切换。
function applyTheme(name) {
  const value = name === 'dark' ? 'dark' : 'light';
  document.body.dataset.theme = value;
  if (window.__applyTheme) window.__applyTheme(value);
}

window.setTheme = applyTheme;
applyTheme(theme);

async function loadScript(src) {
  await new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = src;
    s.onload = resolve;
    s.onerror = () => reject(new Error(`failed to load ${src}`));
    document.head.appendChild(s);
  });
}

async function ensureFileExists() {
  const resp = await fetch(fileUrl, { method: 'HEAD' });
  if (!resp.ok) throw new Error('file-missing');
}

// ---------------------------------------------------------------- PDF.js
async function renderPdf() {
  showLoading();
  const { getDocument, GlobalWorkerOptions } = await import('/libs/pdf.min.mjs');
  GlobalWorkerOptions.workerSrc = '/libs/pdf.worker.min.mjs';
  const pdf = await getDocument({ url: fileUrl, rangeChunkSize: 1 << 16, isEvalSupported: false }).promise;
  const pagesHost = document.createElement('div');
  pagesHost.id = 'pdfPages';
  container.appendChild(pagesHost);
  setMeta(t.pages.replace('{total}', pdf.numPages).replace('{cur}', '1'));

  // 懒渲染：进入视口才绘制（大 PDF 分块加载，翻页流畅）。
  // 注意：未渲染的页面 div 高度为 0，IntersectionObserver 对 0 尺寸元素
  // 不会判定为相交——因此第一页必须立即渲染（有了高度后其余页面滚动
  // 进入视口才能被观察器正常触发）。
  const pageEls = [];
  for (let i = 1; i <= pdf.numPages; i++) {
    const page = document.createElement('div');
    page.className = 'pdf-page';
    const canvas = document.createElement('canvas');
    page.appendChild(canvas);
    page.dataset.page = i;
    pagesHost.appendChild(page);
    pageEls.push({ page, canvas, rendered: false });
  }

  const renderOne = async (item) => {
    if (item.rendered) return;
    item.rendered = true;
    try {
      // dataset 值是字符串，pdf.js 6 的 getPage 用 Number.isInteger 严格校验，
      // 传字符串会抛 "Invalid page request" 导致 canvas 空白。
      const pdfPage = await pdf.getPage(Number(item.page.dataset.page));
      const base = pdfPage.getViewport({ scale: 1.6 });
      const hostWidth = container.clientWidth || document.documentElement.clientWidth || 800;
      const ratio = Math.min(1, Math.max(0.2, hostWidth / base.width));
      const viewport = pdfPage.getViewport({ scale: 1.6 * ratio * zoomLevel });
      item.canvas.width = viewport.width;
      item.canvas.height = viewport.height;
      await pdfPage.render({ canvasContext: item.canvas.getContext('2d'), viewport }).promise;
    } catch (err) {
      item.rendered = false; // 允许重试（如 Range 分块瞬时失败）
      throw err;
    }
  };

  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      const item = pageEls[entry.target.dataset.page - 1];
      if (!item || item.rendered) continue;
      observer.unobserve(entry.target);
      // 滚动到该页时更新"当前页"显示
      setMeta(t.pages.replace('{total}', pdf.numPages).replace('{cur}', String(Number(entry.target.dataset.page))));
      renderOne(item).catch(() => observer.observe(entry.target));
    }
  }, { root: container, rootMargin: '400px' });

  // 缩放：canvas 按像素渲染，放大时重渲染以保持清晰（防抖）
  let zoomTimer = null;
  window.__applyZoom = () => {
    clearTimeout(zoomTimer);
    zoomTimer = setTimeout(() => {
      pageEls.forEach(item => { item.rendered = false; });
      renderOne(pageEls[0]).catch(err => console.error('re-render failed:', err));
      pageEls.slice(1).forEach(item => observer.observe(item.page));
    }, 120);
  };

  await renderOne(pageEls[0]).catch(err => console.error('first page render failed:', err));
  pageEls.slice(1).forEach(item => observer.observe(item.page));
  hideOverlay();
}

// ---------------------------------------------------------------- docx-preview（esbuild 单文件，已含 jszip）
async function renderDocx() {
  showLoading();
  await ensureFileExists();
  const { renderAsync } = await import('/libs/docx-viewer.bundle.mjs');
  const buffer = await (await fetch(fileUrl)).arrayBuffer();
  await renderAsync(buffer, container, container, {
    ignoreLastRenderedPageBreak: true,
    breakPages: true,
    renderHeaders: false,
    renderFooters: false,
    renderFootnotes: false,
    inWrapper: false,
    experimental: true
  });
  hideOverlay();
}

// ---------------------------------------------------------------- xlsx（SheetJS 解析 + 只读表格渲染）
async function renderXlsx() {
  showLoading();
  await ensureFileExists();
  await loadScript('/libs/xlsx.full.min.js');
  const XLSX = window.XLSX;
  if (!XLSX) throw new Error('xlsx missing');
  const buffer = await (await fetch(fileUrl)).arrayBuffer();
  const workbook = XLSX.read(buffer, { type: 'array' });
  if (!workbook.SheetNames.length) throw new Error('xlsx empty');

  const tabs = document.createElement('div');
  tabs.className = 'sheet-tabs';
  const tableHost = document.createElement('div');
  tableHost.className = 'sheet-table-host';
  container.append(tabs, tableHost);
  setMeta(`${workbook.SheetNames.length} sheets`);

  const colLetters = [];
  const MAX_COLS = 256;
  for (let c = 0; c < MAX_COLS; c++) colLetters.push(XLSX.utils.encode_col(c));

  function renderSheet(index) {
    const name = workbook.SheetNames[index];
    const sheet = workbook.Sheets[name];
    tableHost.innerHTML = '';
    const table = document.createElement('table');
    const ref = sheet['!ref'];
    if (!ref) {
      tableHost.textContent = t.emptySheet;
      return;
    }
    const range = XLSX.utils.decode_range(ref);
    const lastRow = Math.min(range.e.r, 20000); // 防御：超长工作表只渲染前 2 万行
    const lastCol = Math.min(range.e.c, MAX_COLS - 1);

    // 合并区域：左上角画 span，其余格子跳过
    const merges = sheet['!merges'] || [];
    const covered = new Set();
    const byTopLeft = new Map();
    for (const m of merges) {
      if (m.s.r > lastRow || m.s.c > lastCol) continue;
      covered.add(`${m.s.r}:${m.s.c}`);
      byTopLeft.set(`${m.s.r}:${m.s.c}`, m);
    }
    const isCovered = (r, c) => covered.has(`${r}:${c}`) && !byTopLeft.has(`${r}:${c}`);

    const colWidths = sheet['!cols'] || [];
    const colWidth = (c) => {
      const w = colWidths[c] && colWidths[c].wch ? colWidths[c].wch : 10;
      return Math.max(6, Math.min(60, w));
    };

    const header = document.createElement('tr');
    const corner = document.createElement('th');
    corner.className = 'corner';
    header.appendChild(corner);
    for (let c = 0; c <= lastCol; c++) {
      const th = document.createElement('th');
      th.textContent = colLetters[c];
      th.style.minWidth = `${colWidth(c) * 8}px`;
      header.appendChild(th);
    }
    table.appendChild(header);

    for (let r = range.s.r; r <= lastRow; r++) {
      const tr = document.createElement('tr');
      const rowHead = document.createElement('th');
      rowHead.className = 'rowhead';
      rowHead.textContent = String(r + 1);
      tr.appendChild(rowHead);
      for (let c = 0; c <= lastCol; c++) {
        if (isCovered(r, c)) continue; // 合并区域非左上角
        const td = document.createElement('td');
        const cell = sheet[XLSX.utils.encode_cell({ r, c })];
        if (cell) {
          td.textContent = cell.w ?? String(cell.v ?? '');
          td.style.minWidth = `${colWidth(c) * 8}px`;
          const anchor = byTopLeft.get(`${r}:${c}`);
          if (anchor) {
            td.rowSpan = Math.min(anchor.e.r, lastRow) - anchor.s.r + 1;
            td.colSpan = Math.min(anchor.e.c, lastCol) - anchor.s.c + 1;
          }
        }
        tr.appendChild(td);
      }
      table.appendChild(tr);
    }
    tableHost.appendChild(table);
  }

  workbook.SheetNames.forEach((name, i) => {
    const tab = document.createElement('button');
    tab.className = 'sheet-tab';
    tab.textContent = name;
    tab.addEventListener('click', () => {
      tabs.querySelectorAll('.sheet-tab').forEach(b => b.classList.remove('active'));
      tab.classList.add('active');
      renderSheet(i);
    });
    tabs.appendChild(tab);
  });
  tabs.firstElementChild.classList.add('active');
  renderSheet(0);
  hideOverlay();
}

// ---------------------------------------------------------------- pptx（pptx-vanilla-viewer）
async function renderPptx() {
  showLoading();
  const { createPptxViewer, vermilionDarkTheme, vermilionLightTheme } = await import('/libs/pptx-viewer.bundle.mjs');
  const viewer = createPptxViewer(container, {
    source: fileUrl,
    fileName: fileName,
    editable: false,
    showToolbar: false,
    showThumbnails: false,
    showFormatToolbar: false,
    showInspector: false,
    theme: theme === 'dark' ? vermilionDarkTheme : vermilionLightTheme,
    onLoad: (info) => {
      hideOverlay();
      setMeta(t.slides.replace('{n}', info.slideCount));
    },
    onError: (message) => {
      console.error('pptx error:', message);
      showError(t.failed);
    }
  });
  window.__applyZoom = (zoom) => { try { viewer.setZoom(zoom); } catch (err) { console.error('pptx zoom failed:', err); } };
  window.__applyTheme = (name) => viewer.setTheme(name === 'dark' ? vermilionDarkTheme : vermilionLightTheme);
}

// ---------------------------------------------------------------- 分派
try {
  fileNameEl.textContent = fileName;
  if (type === 'pdf') await renderPdf();
  else if (type === 'docx') await renderDocx();
  else if (type === 'xlsx') await renderXlsx();
  else if (type === 'pptx') await renderPptx();
  else throw new Error('unknown type');
} catch (err) {
  console.error('preview failed:', err);
  const missing = err && err.message === 'file-missing';
  showError(missing ? t.invalidFile : t.failed);
}
