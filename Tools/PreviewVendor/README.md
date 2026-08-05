# PreviewVendor — Office 预览前端库 vendoring

开发期工具：下载并打包 Office 预览所需的 JS 库，产出提交进 `Assets/Preview/lib/`。
这些文件随应用发布（离线可用），**本目录与 node_modules 不进入运行时**。

## 用法

```bash
cd Tools/PreviewVendor
npm install     # 一次性（锁定于 package-lock.json）
npm run vendor  # 产出全部库到 ../../Assets/Preview/lib/
```

## 库清单（2026-08 锁定）

| 库 | npm 包 | 版本 | 产出 | 许可证 |
|---|---|---|---|---|
| PDF | `pdfjs-dist` | 6.2.108 | `pdf.min.mjs`, `pdf.worker.min.mjs` | Apache-2.0 |
| DOCX | `docx-preview` | 0.4.0 | `docx-viewer.bundle.mjs`（esbuild 单文件 ESM，含 jszip） | Apache-2.0 |
| PPTX | `pptx-vanilla-viewer` | 1.14.0 | `pptx-viewer.bundle.mjs`（esbuild 单文件 ESM，**必须包含 three**——bundle 顶层静态引用 three，external 会导致浏览器 "Failed to resolve module specifier"） | Apache-2.0（内含 MPL-2.0 `mtx-decompressor`，见 NOTICE） |
| XLSX | `xlsx`（SheetJS CE） | 0.18.5 | `xlsx.full.min.js`（UMD）+ viewer.js 内手写只读表格渲染 | Apache-2.0 |

> XLSX 为何不用 Univer：Univer 0.25 开源版（preset-sheets-core）**不含 xlsx 导入**，导入功能已迁入依赖商业许可的 preset-sheets-advanced。SheetJS CE + 只读表格渲染对预览场景足够且无许可顾虑。

升级库版本时：改 package.json → `npm install` → `npm run vendor` → 核对 `viewer.js` 中对应 API 是否变化（pptx-viewer 代际 API 漂移最大，其次 docx-preview）。

## 冒烟验证（可选）

应用启动预览后，浏览器直接打开预览 URL 亦可：
- `curl -i http://127.0.0.1:{port}/` 应返回 HTML
- `curl -i http://127.0.0.1:{port}/libs/pdf.worker.min.mjs` 的 Content-Type 应为 `application/javascript`（ES module 必需）
- `curl -i -H "Range: bytes=0-1023" http://127.0.0.1:{port}/file/{sessionId}?t={token}` 应返回 206
