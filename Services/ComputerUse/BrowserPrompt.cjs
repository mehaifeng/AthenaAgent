'use strict';

const { contextBridge, ipcRenderer } = require('electron');

// Electron provides alert and confirm through Chromium but disables prompt().
contextBridge.exposeInMainWorld('__athenaBrowserPrompt', (message, value) =>
  ipcRenderer.sendSync('athena-browser:prompt', message, value));
contextBridge.executeInMainWorld({
  func: () => {
    const request = window.__athenaBrowserPrompt;
    window.prompt = (message = '', value = '') => request(String(message).slice(0, 4000), String(value).slice(0, 4000));
  }
});
