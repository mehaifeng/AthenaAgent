# PreviewVendor — Office 预览与游戏模式前端库

开发期打包工具。Office 预览库写入 `Assets/Preview/lib/`，游戏模式的 Three.js 写入 `Assets/Polis/vendor/`。
两类产物均提交到仓库，并随应用发布以支持离线使用。

## 用法

```bash
cd Tools/PreviewVendor
npm install          # 一次性（锁定于 package-lock.json）
npm run vendor       # 产出 Office 预览库到 ../../Assets/Preview/lib/
npm run vendor:polis # 产出游戏模式的 Three.js 到 ../../Assets/Polis/vendor/
```

## 库清单（2026-08 锁定）

| 库 | npm 包 | 版本 | 产出 | 许可证 |
|---|---|---|---|---|
| PDF | `pdfjs-dist` | 6.2.108 | `pdf.min.mjs`, `pdf.worker.min.mjs` | Apache-2.0 |
| DOCX | `docx-preview` | 0.4.0 | `docx-viewer.bundle.mjs`（esbuild 单文件 ESM，含 jszip） | Apache-2.0 |
| PPTX | `pptx-vanilla-viewer` | 1.14.0 | `pptx-viewer.bundle.mjs`（esbuild 单文件 ESM，**必须包含 three**——bundle 顶层静态引用 three，external 会导致浏览器 "Failed to resolve module specifier"） | Apache-2.0（内含 MPL-2.0 `mtx-decompressor`，见 NOTICE） |
| XLSX | `xlsx`（SheetJS CE） | 0.18.5 | `xlsx.full.min.js`（UMD）+ viewer.js 内手写只读表格渲染 | Apache-2.0 |
| 游戏模式 | `three`（含 OrbitControls、后期处理、RoomEnvironment、BufferGeometryUtils） | 0.185.1 | `../../Assets/Polis/vendor/three.bundle.mjs`（esbuild 单文件 ESM） | MIT（`LICENSE-three.txt` 同目录） |

> XLSX 为何不用 Univer：Univer 0.25 开源版（preset-sheets-core）**不含 xlsx 导入**，导入功能已迁入依赖商业许可的 preset-sheets-advanced。SheetJS CE + 只读表格渲染对预览场景足够且无许可顾虑。

升级库版本时，更新 `package.json` 后运行 `npm install` 和对应的打包命令，再核对预览或游戏页面使用的 API。

## 冒烟验证（可选）

应用启动预览后，浏览器直接打开预览 URL 亦可：
- `curl -i http://127.0.0.1:{port}/` 应返回 HTML
- `curl -i http://127.0.0.1:{port}/libs/pdf.worker.min.mjs` 的 Content-Type 应为 `application/javascript`（ES module 必需）
- `curl -i -H "Range: bytes=0-1023" http://127.0.0.1:{port}/file/{sessionId}?t={token}` 应返回 206
