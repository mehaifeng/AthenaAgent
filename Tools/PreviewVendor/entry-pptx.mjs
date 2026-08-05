// pptx-vanilla-viewer 单文件 ESM 打包入口（esbuild --bundle）。
// 连依赖（pptx-viewer-core 等）一起打进 pptx-viewer.bundle.mjs。
export { createPptxViewer, vermilionDarkTheme, vermilionLightTheme } from 'pptx-vanilla-viewer';
