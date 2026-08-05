// xlsx（SheetJS CE）UMD 单文件直接拷贝：解析 xlsx 字节流，渲染由 viewer.js 内
// 自带的只读表格渲染完成（Univer 开源版无 xlsx 导入能力、完整版需商业许可，
// 故选 SheetJS + 手写表格这一最小最稳路径）。版本锁定于 package.json 的 xlsx@0.18.5。
import { cpSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const outDir = join(root, 'Assets', 'Preview', 'lib');
const src = join(process.cwd(), 'node_modules', 'xlsx', 'dist');

cpSync(join(src, 'xlsx.full.min.js'), join(outDir, 'xlsx.full.min.js'));
console.log('[vendor:xlsx] xlsx.full.min.js copied (SheetJS CE)');
