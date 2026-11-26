'use strict';

const { app, BrowserWindow, session, ipcMain } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const readline = require('node:readline');
const { randomUUID } = require('node:crypto');

const profile = path.resolve(process.argv[2]);
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
const downloads = path.join(profile, 'PendingDownloads');
let shared;
let stopping = false;
let nextInteraction = 0;

function send(message) {
  if (!process.stdout.destroyed) process.stdout.write(JSON.stringify(message) + '\n');
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
    webPreferences: {
      session: shared, offscreen: true, backgroundThrottling: false,
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
    if (status >= 200 && status < 400) report('visit', url);
    else if (allowed(url)) report('address', url);
  });
  contents.on('did-navigate-in-page', (_event, url, main) => { if (main) report('visit', url); });
  contents.on('page-title-updated', () => report('title', contents.getURL()));
  contents.on('destroyed', () => {
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
      await tab.ready;
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
  send({ event: 'ready', version: process.versions.electron });
}).catch(error => {
  send({ event: 'startup-error', error: String(error.message || error) });
  app.exit(1);
});
