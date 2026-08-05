// pdfjs-dist 官方单文件构建直接拷贝（不打包，worker 需单独 URL）。
// 版本锁定于 package.json 的 pdfjs-dist。
import { cpSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const outDir = join(root, 'Assets', 'Preview', 'lib');
const src = join(process.cwd(), 'node_modules', 'pdfjs-dist', 'build');

cpSync(join(src, 'pdf.min.mjs'), join(outDir, 'pdf.min.mjs'));
cpSync(join(src, 'pdf.worker.min.mjs'), join(outDir, 'pdf.worker.min.mjs'));
console.log('[vendor:pdf] pdf.min.mjs + pdf.worker.min.mjs copied');
