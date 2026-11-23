// SuDown 测试服务器：支持 Range / HEAD 405 / 忽略 Range / 服务端限速 / Content-Disposition
// 用法：node server.js [port]
const http = require('http');
const crypto = require('crypto');

const PORT = Number(process.argv[2] || 8899);

function makeBlock(seed) {
  const buf = Buffer.alloc(65536);
  let x = seed >>> 0 || 1;
  for (let i = 0; i < buf.length; i++) {
    x ^= x << 13; x >>>= 0;
    x ^= x >> 17;
    x ^= x << 5; x >>>= 0;
    buf[i] = x & 0xff;
  }
  return buf;
}

const blockCache = new Map();
function blockFor(name) {
  let b = blockCache.get(name);
  if (!b) {
    const seed = parseInt(crypto.createHash('sha256').update(name).digest('hex').slice(0, 8), 16);
    b = makeBlock(seed);
    blockCache.set(name, b);
  }
  return b;
}

function sliceBlock(block, start, endInclusive) {
  const len = endInclusive - start + 1;
  const out = Buffer.alloc(len);
  for (let i = 0; i < len; i++) out[i] = block[(start + i) % block.length];
  return out;
}

function log(...args) {
  console.log(`[${new Date().toISOString()}]`, ...args);
}

// 把文件响应发给客户端：抽成公共函数，供 /file 和各类鉴权端点复用。
function serveFile(req, res, { size, name, bps = 0, dispo = 'plain', mode = '', extraHeaders = {} }) {
  const block = blockFor(name);
  const noRange = mode === 'norange';
  const drop = mode === 'drop';

  const headers = Object.assign({
    'content-type': 'application/octet-stream',
    'accept-ranges': noRange ? 'none' : 'bytes',
    'etag': `"${crypto.createHash('md5').update(`${name}:${size}`).digest('hex')}"`,
    'last-modified': new Date(1700000000000).toUTCString(),
  }, extraHeaders);

  if (dispo === 'star') {
    headers['content-disposition'] = `attachment; filename="fallback.bin"; filename*=UTF-8''${encodeURIComponent(name)}`;
  } else if (dispo === 'plain') {
    headers['content-disposition'] = `attachment; filename="${name}"`;
  }

  const rangeHeader = req.headers.range;
  let start = 0;
  let end = size - 1;
  let partial = false;

  if (rangeHeader && !noRange) {
    const rm = /bytes=(\d*)-(\d*)/.exec(rangeHeader);
    if (rm) {
      if (rm[1] !== '') start = Number(rm[1]);
      if (rm[2] !== '') end = Number(rm[2]);
      if (rm[1] === '' && rm[2] !== '') {
        start = Math.max(0, size - Number(rm[2]));
        end = size - 1;
      }
      if (end >= size) end = size - 1;
      if (start > end || start >= size) {
        res.writeHead(416, { 'content-range': `bytes */${size}` }).end();
        return;
      }
      partial = true;
    }
  }

  const length = end - start + 1;
  headers['content-length'] = String(length);
  if (partial) headers['content-range'] = `bytes ${start}-${end}/${size}`;
  if (noRange) delete headers['accept-ranges'];

  log(`${req.method} ${req.url} ${partial ? `range ${start}-${end}` : 'full'} ${length}B${bps ? ` @${bps}B/s` : ''}`);

  if (req.method === 'HEAD') {
    res.writeHead(200, headers).end();
    return;
  }

  res.writeHead(partial ? 206 : 200, headers);

  const CHUNK = 32 * 1024;
  let position = start;
  let served = 0;

  const writeNext = () => {
    if (position > end) { res.end(); return; }
    const len = Math.min(CHUNK, end - position + 1);
    const buf = sliceBlock(block, position, position + len - 1);
    position += len;
    served += len;

    if (drop && served >= 256 * 1024) {
      log(`DROP mid-transfer ${req.url} @${served}B`);
      res.destroy();
      return;
    }

    const ok = res.write(buf);
    const delay = bps > 0 ? Math.round((len / bps) * 1000) : 0;

    const next = () => { if (ok) schedule(); else res.once('drain', schedule); };
    const schedule = () => { if (delay > 0) setTimeout(writeNext, delay); else setImmediate(writeNext); };
    next();
  };

  writeNext();
}

function deny(res, code, msg) {
  res.writeHead(code, { 'content-type': 'text/plain' }).end(msg || `denied ${code}`);
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${PORT}`);

  // /echo：回显请求头（JSON），用于核对 -H / 认证 / Cookie 是否真的发出
  if (url.pathname === '/echo') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(req.headers));
    return;
  }

  // /auth/basic/:size/:name  需要 Authorization: Basic dXNlcjpwYXNz  (user:pass)
  // /auth/bearer/:size/:name 需要 Authorization: Bearer secret-token
  // /auth/header/:size/:name 需要 X-Api-Key: expected-key
  // /cookie/:size/:name      需要 Cookie: session=abc123
  const authMatch = url.pathname.match(/^\/(auth\/(?:basic|bearer|header)|cookie)\/(\d+)\/(.+)$/);
  if (authMatch) {
    const kind = authMatch[1];
    const size = Number(authMatch[2]);
    const name = decodeURIComponent(authMatch[3]);
    const auth = req.headers.authorization || '';
    let ok = false;

    if (kind === 'auth/basic') ok = auth === 'Basic ' + Buffer.from('user:pass').toString('base64');
    else if (kind === 'auth/bearer') ok = auth === 'Bearer secret-token';
    else if (kind === 'auth/header') ok = req.headers['x-api-key'] === 'expected-key';
    else if (kind === 'cookie') ok = (req.headers.cookie || '').includes('session=abc123');

    if (!ok) {
      log(`DENY ${kind} ${name} auth=${auth} cookie=${req.headers.cookie || '-'} xkey=${req.headers['x-api-key'] || '-'}`);
      deny(res, 401, `need credentials for ${kind}`);
      return;
    }
    log(`ALLOW ${kind} ${name}`);
    serveFile(req, res, { size, name, bps: Number(url.searchParams.get('bps') || 0) });
    return;
  }

  // /redirect/:size/:name → 302 到 /file/:size/:name（可指定 hops 做多跳）
  const redirMatch = url.pathname.match(/^\/redirect\/(\d+)\/(.+)$/);
  if (redirMatch) {
    const size = Number(redirMatch[1]);
    const name = redirMatch[2];
    const hops = Number(url.searchParams.get('hops') || 1);
    if (hops > 1) {
      res.writeHead(302, { location: `/redirect/${size}/${name}?hops=${hops - 1}` }).end();
    } else {
      res.writeHead(302, { location: `/file/${size}/${name}` }).end();
    }
    log(`REDIRECT ${url.pathname} hops=${hops}`);
    return;
  }

  // /setcookie/:size/:name → 先 Set-Cookie，再 302 到 /cookie/:size/:name（验证 session 跨请求带 Cookie）
  const scMatch = url.pathname.match(/^\/setcookie\/(\d+)\/(.+)$/);
  if (scMatch) {
    const size = Number(scMatch[1]);
    const name = scMatch[2];
    res.writeHead(302, {
      'set-cookie': 'session=abc123; Path=/',
      'location': `/cookie/${size}/${name}`,
    }).end();
    log(`SETCOOKIE ${url.pathname}`);
    return;
  }

  // /sha256/:size/:name
  const shaMatch = url.pathname.match(/^\/sha256\/(\d+)\/(.+)$/);
  if (shaMatch) {
    const size = Number(shaMatch[1]);
    const name = decodeURIComponent(shaMatch[2]);
    const block = blockFor(name);
    const hash = crypto.createHash('sha256');
    const chunk = 65536;
    for (let off = 0; off < size; off += chunk) {
      const len = Math.min(chunk, size - off);
      hash.update(sliceBlock(block, off, off + len - 1));
    }
    res.writeHead(200, { 'content-type': 'text/plain' });
    res.end(hash.digest('hex') + '\n');
    return;
  }

  // /file/:size/:name?mode=norange|nohead&bps=N&dispo=star|plain|none
  const m = url.pathname.match(/^\/file\/(\d+)\/(.+)$/);
  if (!m) {
    res.writeHead(404).end('not found');
    return;
  }

  const size = Number(m[1]);
  const name = decodeURIComponent(m[2]);
  const mode = url.searchParams.get('mode') || '';
  const bps = Number(url.searchParams.get('bps') || 0);
  const dispo = url.searchParams.get('dispo') || 'plain';

  if (req.method === 'HEAD' && mode === 'nohead') {
    log('HEAD -> 405', name);
    res.writeHead(405, { 'content-type': 'text/plain' }).end();
    return;
  }

  serveFile(req, res, { size, name, bps, dispo, mode });
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`SuDown test server on http://127.0.0.1:${PORT}`);
});
