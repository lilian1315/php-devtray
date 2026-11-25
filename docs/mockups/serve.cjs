// Local preview only: expose this directory on loopback, never the repository.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = __dirname;
http.createServer((req, res) => {
  const pathname = new URL(req.url, 'http://localhost').pathname;
  const name = pathname === '/' ? 'interface.html' : pathname.slice(1);
  const file = ['interface.html', 'interface.js', 'interface.css'].includes(name) ? path.join(root, name) : null;
  if (!file) { res.writeHead(404); res.end('Not found'); return; }
  const mime = name.endsWith('.js') ? 'text/javascript' : name.endsWith('.css') ? 'text/css' : 'text/html';
  res.writeHead(200, { 'Content-Type': mime + '; charset=utf-8', 'Cache-Control': 'no-store' });
  fs.createReadStream(file).pipe(res);
}).listen(4179, '127.0.0.1', () => console.log('Mockups: http://127.0.0.1:4179/'));
