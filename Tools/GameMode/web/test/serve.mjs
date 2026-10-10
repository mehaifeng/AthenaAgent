// 静态服务：只用 Node 自带模块，只绑 127.0.0.1。网页在 /polis/，合成夹具在 /fixtures/，
// 由真实数据导出的夹具在被忽略的 /.local/ 下（?fixture=../.local/real-fixture.json）。
// 单独运行：node Tools/GameMode/web/test/serve.mjs [端口]   → http://127.0.0.1:<端口>/polis/index.html
import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// 页面在 Assets/Polis（应用把它打进资源、由 OfficePreviewHost 的 /polis/ 路由提供）；夹具与测试在 Tools/GameMode。
// 这里按同样的前缀提供：/polis/ → Assets/Polis，/fixtures/ → Tools/GameMode/web/fixtures，/.local/ → 被忽略的真实夹具。
const REPO = path.resolve(fileURLToPath(new URL('../../../../', import.meta.url)));
const MOUNTS = [
  ['/polis/', path.join(REPO, 'Assets', 'Polis')],
  ['/fixtures/', path.join(REPO, 'Tools', 'GameMode', 'web', 'fixtures')],
  ['/.local/', path.join(REPO, 'Tools', 'GameMode', '.local')],
];
const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.txt': 'text/plain; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
};

export function startServer(port = 0) {
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://127.0.0.1');
      const pathname = decodeURIComponent(url.pathname);
      const mount = MOUNTS.find(([prefix]) => pathname.startsWith(prefix));
      if (!mount) {
        res.writeHead(404).end('not found');
        return;
      }
      const [prefix, base] = mount;
      const file = path.resolve(base, '.' + pathname.slice(prefix.length - 1));
      if (!file.startsWith(base + path.sep)) {
        res.writeHead(403).end('forbidden');
        return;
      }
      const info = await stat(file).catch(() => null);
      if (!info || !info.isFile()) {
        res.writeHead(404).end('not found');
        return;
      }
      res.writeHead(200, {
        'Content-Type': TYPES[path.extname(file)] ?? 'application/octet-stream',
        'Cache-Control': 'no-store',
      });
      res.end(await readFile(file));
    } catch (e) {
      res.writeHead(500).end(String(e));
    }
  });
  return new Promise((resolve) => {
    server.listen(port, '127.0.0.1', () => {
      const { port: actual } = server.address();
      resolve({ origin: `http://127.0.0.1:${actual}`, close: () => new Promise((r) => server.close(r)) });
    });
  });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const s = await startServer(Number(process.argv[2] ?? 8766));
  console.log(`${s.origin}/polis/index.html`);
}
