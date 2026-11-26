'use strict';

const { app, BrowserWindow, session, ipcMain } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const readline = require('node:readline');
const { randomUUID } = require('node:crypto');

const profile = path.resolve(process.argv[2]);
let textureBridge;
if (process.platform === 'darwin' && process.argv[3] && process.argv[4]) {
  try {
    const addon = require(process.argv[3]);
    if (addon.connect(process.argv[4])) textureBridge = addon;
  } catch (error) {
    process.stderr.write(`Browser GPU bridge: ${error.message}\n`);
  }
}
fs.mkdirSync(profile, { recursive: true });
app.setName('Athena Browser');
app.setPath('userData', profile);
app.setPath('sessionData', profile);
app.enableSandbox();
if (process.platform === 'linux')
  app.commandLine.appendSwitch('disable-features', 'Vulkan');

const tabs = new Map();
const interactions = new Map();
const grants = new Set();
const texturesInFlight = new Map();
const downloads = path.join(profile, 'PendingDownloads');
const popupPaintWaitMs = 150;
let shared;
let stopping = false;
let nextInteraction = 0;
let nextTextureSequence = 0;

function send(message, pixels) {
  if (process.stdout.destroyed) return;
  const metadata = Buffer.from(JSON.stringify(message));
  const header = Buffer.alloc(8);
  header.writeUInt32LE(metadata.length, 0);
  header.writeUInt32LE(pixels?.length ?? 0, 4);
  process.stdout.cork();
  process.stdout.write(header);
  process.stdout.write(metadata);
  if (pixels) process.stdout.write(pixels);
  process.stdout.uncork();
}

function sendPreview(tab, preview) {
  if (preview.inFlight || !preview.latest) return;
  const { image, texture } = preview.latest;
  preview.latest = null;
  if (texture) {
    const { handle, codedSize, visibleRect, pixelFormat } = texture.textureInfo;
    const { width, height } = codedSize;
    const sequence = ++nextTextureSequence;
    if (texturesInFlight.size >= 8 || pixelFormat !== 'bgra' || visibleRect.x !== 0 || visibleRect.y !== 0
        || visibleRect.width !== width || visibleRect.height !== height
        || !textureBridge.send(handle.ioSurface, sequence)) {
      texture.release();
      subscribeToPreviewFrames(tab, preview);
      return;
    }
    texturesInFlight.set(sequence, { tab, texture });
    preview.inFlight = true;
    preview.textureSequence = sequence;
    send({ event: 'previewTexture', sequence, preview: preview.id, revision: preview.revision, width, height });
    return;
  }
  const { width, height } = image.getSize();
  if (!width || !height) return;
  const pixels = image.toBitmap();
  if (pixels.length !== width * height * 4) return;
  // One frame awaiting Athena and one newest paint to send when it catches up.
  preview.inFlight = true;
  send({ event: 'preview', preview: preview.id, revision: preview.revision, width, height }, pixels);
}

function discardPendingPreview(preview) {
  preview?.latest?.texture?.release();
  if (preview) preview.latest = null;
}

function queuePreview(tab, preview, frame) {
  discardPendingPreview(preview);
  preview.latest = frame;
  sendPreview(tab, preview);
}

function invalidatePreview(tab) {
  if (!tab.preview || tab.preview.subscribed) return;
  tab.contents.startPainting();
  tab.contents.invalidate();
}

function subscribeToPreviewFrames(tab, preview) {
  clearTimeout(preview.paintDeadline);
  discardPendingPreview(preview);
  tab.contents.stopPainting();
  preview.subscribed = true;
  tab.contents.beginFrameSubscription(false, image => {
    if (tab.preview !== preview || !preview.subscribed || image.isEmpty()) return;
    queuePreview(tab, preview, { image });
  });
}

function stopPreviewFallback(tab, preview) {
  clearTimeout(preview.paintDeadline);
  if (preview.subscribed) {
    preview.subscribed = false;
    tab.contents.endFrameSubscription();
  }
}

function requestPreviewPaint(tab) {
  const preview = tab.preview;
  if (!preview) return;
  stopPreviewFallback(tab, preview);
  discardPendingPreview(preview);
  if (textureBridge && !preview.sharedTextures) {
    subscribeToPreviewFrames(tab, preview);
    return;
  }
  if (tab.opener !== null || textureBridge) {
    // Electron can leave a popup's first renderer view with a no-op paint callback.
    // Subscribe to that view's compositor frames so the live page can stay loaded.
    preview.paintDeadline = setTimeout(() => {
      if (tab.preview === preview && !tab.contents.isDestroyed())
        subscribeToPreviewFrames(tab, preview);
    }, popupPaintWaitMs);
  }
  invalidatePreview(tab);
}

function allowed(url, blank = false) {
  if (blank && (url === 'about:blank' || url === '')) return true;
  try {
    const address = new URL(url);
    return ['http:', 'https:'].includes(address.protocol) && !address.username && !address.password;
  } catch { return false; }
}

function windowOptions(width, height) {
  return {
    show: false, width, height, useContentSize: true,
    enableLargerThanScreen: true,
    webPreferences: {
      session: shared, offscreen: textureBridge ? { useSharedTexture: true, sharedTexturePixelFormat: 'bgra' } : true,
      backgroundThrottling: false,
      nodeIntegration: false, contextIsolation: true, sandbox: true, webSecurity: true,
      preload: path.join(__dirname, 'BrowserPrompt.cjs')
    }
  };
}

function tabInfo(tab, opener = null) {
  return { tab: tab.id, group: tab.group, opener, url: tab.contents.getURL() || 'about:blank',
    title: tab.contents.getTitle() };
}

function ask(tab, kind, values) {
  if (stopping || tab.contents.isDestroyed()) return Promise.resolve({});
  const interaction = ++nextInteraction;
  return new Promise(resolve => {
    interactions.set(interaction, { resolve, tab });
    send({ event: 'interaction', interaction, tab: tab.id, group: tab.group, kind, ...values });
  });
}

function register(window, group, opener) {
  const contents = window.webContents;
  const id = String(contents.id);
  const tab = { id, group, window, contents, opener };
  tabs.set(id, tab);
  contents.setWindowOpenHandler(details => allowed(details.url, true)
    ? { action: 'allow', overrideBrowserWindowOptions: windowOptions(window.getContentSize()[0], window.getContentSize()[1]) }
    : { action: 'deny' });
  contents.on('did-create-window', child => register(child, group, id));
  const validateNavigation = (event, value) => {
    const url = value ?? event.url;
    if (typeof url === 'string' && !allowed(url, true)) event.preventDefault();
  };
  contents.on('will-navigate', validateNavigation);
  contents.on('will-redirect', validateNavigation);
  const report = (event, url) => {
    if (!allowed(url)) return;
    send({ ...tabInfo(tab, opener), event, url });
  };
  contents.on('did-navigate', (_event, url, status) => {
    if (opener !== null || textureBridge) requestPreviewPaint(tab);
    if (status >= 200 && status < 400) report('visit', url);
    else if (allowed(url)) report('address', url);
  });
  contents.on('did-navigate-in-page', (_event, url, main) => { if (main) report('visit', url); });
  contents.on('page-title-updated', () => report('title', contents.getURL()));
  contents.on('dom-ready', () => invalidatePreview(tab));
  contents.on('did-stop-loading', () => invalidatePreview(tab));
  contents.setZoomMode('isolated');
  contents.setFrameRate(60);
  contents.stopPainting();
  contents.on('paint', (event, _dirty, image) => {
    const preview = tab.preview;
    const texture = event.texture;
    if (texture) {
      if (!preview?.sharedTextures || preview.subscribed || texture.textureInfo.widgetType !== 'frame') {
        texture.release();
        return;
      }
      clearTimeout(preview.paintDeadline);
      queuePreview(tab, preview, { texture });
      return;
    }
    if (!preview || preview.subscribed || image.isEmpty()) return;
    clearTimeout(preview.paintDeadline);
    queuePreview(tab, preview, { image });
  });
  contents.on('destroyed', () => {
    clearTimeout(tab.preview?.paintDeadline);
    discardPendingPreview(tab.preview);
    tabs.delete(id);
    for (const [key, value] of interactions) {
      if (value.tab === tab) {
        interactions.delete(key);
        value.resolve({});
      }
    }
    send({ event: 'closed', tab: id, group, opener });
  });
  contents.on('render-process-gone', () => { if (!window.isDestroyed()) window.destroy(); });

  const debuggerApi = contents.debugger;
  debuggerApi.attach('1.3');
  debuggerApi.on('message', async (_event, method, params) => {
    try {
      if (method === 'Page.frameResized' && textureBridge && tab.preview && tab.viewport && !tab.settingViewport) {
        // Capture surfaces can retain their dimensions even when Chromium's CSS viewport drifts.
        const { preview, viewport } = tab;
        const { result } = await debuggerApi.sendCommand('Runtime.evaluate', {
          expression: '[innerWidth, innerHeight, devicePixelRatio]', returnByValue: true
        });
        const metrics = result.value;
        if (tab.preview === preview && tab.viewport === viewport && !tab.settingViewport && Array.isArray(metrics)
            && (metrics[0] !== viewport.cssWidth || metrics[1] !== viewport.cssHeight
                || Math.abs(metrics[2] - viewport.scale) > 0.0001))
          send({ event: 'previewViewportChanged', preview: preview.id });
      }
      if (method === 'Page.fileChooserOpened') {
        const reply = await ask(tab, 'file', { multiple: params.mode === 'selectMultiple', url: contents.getURL() });
        if (Array.isArray(reply.files) && reply.files.length > 0 && params.backendNodeId)
          await debuggerApi.sendCommand('DOM.setFileInputFiles', { backendNodeId: params.backendNodeId, files: reply.files });
      }
      if (method === 'Page.javascriptDialogOpening') {
        const reply = await ask(tab, 'dialog', { dialogType: params.type, message: params.message,
          defaultValue: params.defaultPrompt, url: params.url });
        await debuggerApi.sendCommand('Page.handleJavaScriptDialog', {
          accept: params.type === 'alert' || reply.accept === true, promptText: reply.text || ''
        });
      }
    } catch { /* A page can close before its pending interaction is answered. */ }
  });
  tab.ready = debuggerApi.sendCommand('Page.enable').then(() =>
    debuggerApi.sendCommand('Page.setInterceptFileChooserDialog', { enabled: true }));
  tab.ready.catch(() => { if (!window.isDestroyed()) window.destroy(); });
  send({ event: 'opened', ...tabInfo(tab, opener) });
  return tab;
}

function permissionKey(permission, origin) { return JSON.stringify([permission, origin]); }

async function setup() {
  shared = session.fromPartition('persist:athena-browser');
  ipcMain.on('athena-browser:prompt', (event, message, defaultValue) => {
    const tab = tabs.get(String(event.sender.id));
    const url = event.senderFrame?.url || '';
    if (!tab || !allowed(url, true) || typeof message !== 'string' || typeof defaultValue !== 'string') {
      event.returnValue = null;
      return;
    }
    ask(tab, 'dialog', { dialogType: 'prompt', message: message.slice(0, 4000),
      defaultValue: defaultValue.slice(0, 4000), url }).then(reply => {
      event.returnValue = reply.accept === true && typeof reply.text === 'string' ? reply.text : null;
    });
  });
  fs.rmSync(downloads, { recursive: true, force: true });
  fs.mkdirSync(downloads, { recursive: true });
  shared.setPermissionCheckHandler((_contents, permission, origin) => grants.has(permissionKey(permission, origin)));
  shared.setPermissionRequestHandler((contents, permission, callback, details) => {
    const tab = tabs.get(String(contents?.id));
    if (!tab) { callback(false); return; }
    const origin = new URL(details.requestingUrl || contents.getURL()).origin;
    ask(tab, 'permission', { permission, url: origin }).then(reply => {
      if (reply.accept) grants.add(permissionKey(permission, origin));
      callback(reply.accept === true);
    }, () => callback(false));
  });
  app.on('login', (event, contents, details, _authInfo, callback) => {
    const tab = tabs.get(String(contents?.id));
    if (!tab) return;
    event.preventDefault();
    ask(tab, 'authentication', { url: details.url }).then(reply => {
      if (reply.accept) callback(reply.user || '', reply.password || '');
      else callback();
    });
  });
  shared.on('will-download', (_event, item, contents) => {
    const tab = tabs.get(String(contents?.id));
    if (!tab) { item.cancel(); return; }
    const name = item.getFilename();
    const temporary = path.join(downloads, randomUUID());
    item.setSavePath(temporary);
    item.pause();
    const choice = ask(tab, 'download', { name, url: contents.getURL(), directory: app.getPath('downloads') });
    item.once('done', async (_event, state) => {
      try {
        const reply = await choice;
        if (state === 'completed' && typeof reply.path === 'string' && path.isAbsolute(reply.path)) {
          await fs.promises.copyFile(temporary, reply.path);
          send({ event: 'download', tab: tab.id, group: tab.group, name, ok: true });
        } else if (state !== 'cancelled') {
          send({ event: 'download', tab: tab.id, group: tab.group, name, ok: false });
        }
      } catch { send({ event: 'download', tab: tab.id, group: tab.group, name, ok: false }); }
      finally { await fs.promises.rm(temporary, { force: true }).catch(() => {}); }
    });
    choice.then(reply => { if (reply.path) item.resume(); else item.cancel(); }).catch(() => {});
  });
}

async function shutdown() {
  if (stopping) return;
  stopping = true;
  for (const { resolve } of interactions.values()) resolve({});
  interactions.clear();
  for (const { texture } of texturesInFlight.values()) texture.release();
  texturesInFlight.clear();
  if (shared) {
    shared.flushStorageData();
    await shared.cookies.flushStore();
  }
  app.exit(0);
}

async function dispatch(request) {
  switch (request.method) {
    case 'open': {
      const { group, width, height } = request.parameters;
      const window = new BrowserWindow(windowOptions(width, height));
      const tab = register(window, group, null);
      await window.loadURL('about:blank');
      await tab.ready;
      return tabInfo(tab);
    }
    case 'reply': {
      const { interaction, result } = request.parameters;
      const pending = interactions.get(interaction);
      interactions.delete(interaction);
      pending?.resolve(result || {});
      return {};
    }
    case 'previewTextureAck': {
      const { sequence, fallback } = request.parameters;
      const frame = texturesInFlight.get(sequence);
      if (!frame) return {};
      texturesInFlight.delete(sequence);
      frame.texture.release();
      const { tab } = frame;
      const preview = tab.preview;
      if (preview?.textureSequence === sequence && !tab.contents.isDestroyed()) {
        preview.textureSequence = null;
        preview.inFlight = false;
        if (fallback && !preview.subscribed) subscribeToPreviewFrames(tab, preview);
        else sendPreview(tab, preview);
      }
      return {};
    }
    case 'clearData':
      await shared.clearData();
      await shared.clearCache();
      await shared.cookies.flushStore();
      grants.clear();
      return {};
    case 'stop':
      await shutdown();
      return {};
    default: {
      const tab = tabs.get(request.tab);
      if (!tab) throw new Error('The browser tab was closed.');
      if (request.method === 'close') {
        tab.window.destroy();
        return {};
      }
      if (request.method === 'previewStart') {
        const { id, revision, sharedTextures } = request.parameters;
        const previous = tab.preview;
        const samePreview = previous?.id === id;
        const inFlight = samePreview && previous.inFlight && (!previous.textureSequence || sharedTextures);
        const subscribed = samePreview && previous.subscribed;
        if (previous) stopPreviewFallback(tab, previous);
        discardPendingPreview(previous);
        tab.preview = { id, revision, inFlight, latest: null, subscribed: false, paintDeadline: null, sharedTextures,
          textureSequence: samePreview && sharedTextures ? previous.textureSequence : null };
        if (subscribed) subscribeToPreviewFrames(tab, tab.preview);
        else requestPreviewPaint(tab);
        return {};
      }
      if (request.method === 'previewAck') {
        if (tab.preview?.id === request.parameters.id) {
          tab.preview.inFlight = false;
          sendPreview(tab, tab.preview);
        }
        return {};
      }
      if (request.method === 'previewStop') {
        if (tab.preview?.id === request.parameters.id) {
          stopPreviewFallback(tab, tab.preview);
          discardPendingPreview(tab.preview);
          tab.preview = null;
          tab.contents.stopPainting();
        }
        return {};
      }
      await tab.ready;
      if (request.method === 'setViewport') {
        const { scale, pixelWidth, pixelHeight, cssWidth, cssHeight } = request.parameters;
        const debuggerApi = tab.contents.debugger;
        tab.settingViewport = true;
        try {
          // A navigation can recreate the compositor surface using the offscreen window's size.
          tab.window.setContentSize(pixelWidth, pixelHeight);
          // Page zoom rasterizes at the requested scale. An emulated DPR alone only scales screenshots.
          tab.contents.setZoomFactor(scale);
          await debuggerApi.sendCommand('Emulation.setDeviceMetricsOverride', {
            width: pixelWidth, height: pixelHeight, deviceScaleFactor: 1, mobile: false, dontSetVisibleSize: true
          });
          const result = await debuggerApi.sendCommand('Emulation.setVisibleSize', { width: pixelWidth, height: pixelHeight });
          tab.viewport = { cssWidth, cssHeight, scale };
          return result;
        } finally { tab.settingViewport = false; }
      }
      // The channel is private to Athena. Web renderers have no access to it.
      return await tab.contents.debugger.sendCommand(request.method, request.parameters);
    }
  }
}

app.on('window-all-closed', () => {});
app.whenReady().then(async () => {
  await setup();
  const pipe = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
  pipe.on('line', async line => {
    let request;
    try {
      request = JSON.parse(line);
      const result = await dispatch(request);
      send({ id: request.id, result });
    } catch (error) {
      if (request) send({ id: request.id, error: String(error.message || error) });
    }
  });
  pipe.on('close', () => shutdown().catch(() => app.exit(1)));
  send({ event: 'ready', version: process.versions.electron, sharedTextures: !!textureBridge });
}).catch(error => {
  send({ event: 'startup-error', error: String(error.message || error) });
  app.exit(1);
});
