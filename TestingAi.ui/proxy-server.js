const http = require('http');
const fs = require('fs');
const path = require('path');

const STATIC_DIR = path.join(__dirname, 'dist/testing-ai.ui/browser');
const API_HOST = 'localhost';
const API_PORT = 5000;
const PORT = process.env.PORT || 4200;

const MIME = {
  '.html': 'text/html', '.js': 'application/javascript',
  '.css': 'text/css', '.json': 'application/json',
  '.png': 'image/png', '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml', '.woff': 'font/woff', '.woff2': 'font/woff2',
  '.ttf': 'font/ttf', '.eot': 'application/vnd.ms-fontobject',
  '.map': 'application/json', '.txt': 'text/plain',
};

http.createServer((req, res) => {
  const parsedUrl = new URL(req.url, `http://localhost`);

  // Proxy /api/* → .NET API on port 5000
  if (parsedUrl.pathname.startsWith('/api')) {
    const opts = {
      hostname: API_HOST, port: API_PORT,
      path: req.url, method: req.method,
      headers: { ...req.headers, host: `${API_HOST}:${API_PORT}` },
    };
    const proxy = http.request(opts, apiRes => {
      res.writeHead(apiRes.statusCode, apiRes.headers);
      apiRes.pipe(res);
    });
    proxy.on('error', e => { res.writeHead(502); res.end(`API unavailable: ${e.message}`); });
    req.pipe(proxy);
    return;
  }

  // Static file serving with SPA fallback
  let filePath = path.join(STATIC_DIR, parsedUrl.pathname);
  if (!fs.existsSync(filePath) || fs.statSync(filePath).isDirectory()) {
    filePath = path.join(STATIC_DIR, 'index.html');
  }
  const ext = path.extname(filePath).toLowerCase();
  const contentType = MIME[ext] || 'application/octet-stream';
  fs.readFile(filePath, (err, data) => {
    if (err) { res.writeHead(404); res.end('Not found'); return; }
    res.writeHead(200, { 'Content-Type': contentType });
    res.end(data);
  });
}).listen(PORT, '0.0.0.0', () => {
  console.log(`\n🚀 Angular UI + API proxy running on http://0.0.0.0:${PORT}`);
  console.log(`   Static: ${STATIC_DIR}`);
  console.log(`   /api/* → http://${API_HOST}:${API_PORT}\n`);
});
