// pptx-vanilla-viewer 打包：esbuild 单文件 ESM。
// three 必须打进 bundle：bundle 顶层静态引用 three（模块加载即解析），
// external 会导致浏览器 "Failed to resolve module specifier 'three'"。
// + 许可证合规文件（Apache-2.0 LICENSE + NOTICE，内含 MPL-2.0 mtx-decompressor 声明）。
import { cpSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const outDir = join(root, 'Assets', 'Preview', 'lib');
const nodeModules = join(process.cwd(), 'node_modules');

const esbuild = spawnSync(
  join(nodeModules, '.bin', process.platform === 'win32' ? 'esbuild.cmd' : 'esbuild'),
  [
    'entry-pptx.mjs', '--bundle', '--format=esm', '--minify',
    `--outfile=${join(outDir, 'pptx-viewer.bundle.mjs')}`
  ],
  { stdio: 'inherit' }
);
if (esbuild.status !== 0) process.exit(esbuild.status ?? 1);

cpSync(join(nodeModules, 'pptx-vanilla-viewer', 'LICENSE'), join(outDir, 'LICENSE'));
cpSync(join(nodeModules, 'pptx-vanilla-viewer', 'NOTICE'), join(outDir, 'NOTICE'));
console.log('[vendor:pptx] pptx-viewer.bundle.mjs + LICENSE + NOTICE');
