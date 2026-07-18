// Feng Sync's protocol boundary. Uses ssh2's maintained SSH/SFTP server implementation;
// it is intentionally a separate process so a protocol fault cannot take down the WPF app.
'use strict';
const { Server, utils: { parseKey, sftp: { flagsToString, STATUS_CODE } } } = require('ssh2');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const config = JSON.parse(Buffer.from(process.env.FENGSYNC_SFTP_CONFIG || '', 'base64').toString('utf8'));
const opt = config.Options;
const tmpSuffix = '.fengsync-upload-part';
const auditPath = config.AuditLogPath;
const maxUploadBytes = Number.isSafeInteger(opt?.MaxUploadBytes) ? opt.MaxUploadBytes : 1073741824;
const maxAuthenticationFailures = Number.isSafeInteger(opt?.MaxAuthenticationFailures) ? opt.MaxAuthenticationFailures : 5;
const authenticationBlockMs = Number(opt?.AuthenticationBlockDuration?.TotalMilliseconds) || 300000;
const authenticationFailures = new Map();

function audit(account, address, action, virtualPath, outcome) {
  if (!auditPath) return;
  try { fs.mkdirSync(path.dirname(auditPath), { recursive: true }); fs.appendFileSync(auditPath, JSON.stringify({ Account: account || '', SourceAddress: address || '', Action: action, VirtualPath: virtualPath || null, Outcome: String(outcome).replace(/(password|pass|credential)\s*[=:]\s*[^\s,;]+/ig, 'credential=<redacted>'), TimestampUtc: new Date().toISOString() }) + '\n'); } catch { /* auditing must not terminate the isolated protocol host */ }
}
function authenticationKey(address, user) { return `${address || ''}\n${user || ''}`; }
function isBlocked(address, user) { const state = authenticationFailures.get(authenticationKey(address, user)); if (!state) return false; if (state.until > Date.now()) return true; authenticationFailures.delete(authenticationKey(address, user)); return false; }
function recordFailure(address, user) { const key = authenticationKey(address, user); const prior = authenticationFailures.get(key); const count = (prior?.until > Date.now()) ? prior.count : (prior?.count || 0) + 1; authenticationFailures.set(key, { count, until: count >= maxAuthenticationFailures ? Date.now() + authenticationBlockMs : 0 }); }
function clearFailures(address, user) { authenticationFailures.delete(authenticationKey(address, user)); }

function fail(message) { console.error(message); process.exit(2); }
if (!opt || !Array.isArray(opt.Accounts) || !Array.isArray(opt.Shares)) fail('Invalid SFTP configuration.');
if (!fs.existsSync(config.HostKeyPath)) {
  fs.mkdirSync(path.dirname(config.HostKeyPath), { recursive: true });
  const pair = crypto.generateKeyPairSync('rsa', { modulusLength: 3072, privateKeyEncoding: { type: 'pkcs1', format: 'pem' } });
  fs.writeFileSync(config.HostKeyPath, pair.privateKey, { mode: 0o600, flag: 'wx' });
}
const hostKey = fs.readFileSync(config.HostKeyPath);
const accounts = new Map(opt.Accounts.filter(x => x.Enabled).map(x => [x.UserName, x]));
const shares = new Map(opt.Shares.map(x => [x.VirtualName.toLowerCase(), { ...x, root: path.resolve(x.PhysicalPath) }]));
for (const s of shares.values()) if (!fs.statSync(s.root).isDirectory()) fail(`Share does not exist: ${s.root}`);

function sameKey(ctxKey, key) {
  return !(key instanceof Error) && key.type === ctxKey.algo && key.getPublicSSH().equals(ctxKey.data);
}
function verifyPassword(account, password) {
  try {
    const actual = crypto.pbkdf2Sync(password, Buffer.from(account.PasswordSalt, 'base64'), account.PasswordIterations, 32, 'sha256');
    return crypto.timingSafeEqual(actual, Buffer.from(account.PasswordHash, 'base64'));
  } catch { return false; }
}
function normalize(remote) {
  // OpenSSH clients commonly use "." for the initial REALPATH request; it is the virtual root.
  if (remote === '.' || remote === '') return '/';
  if (typeof remote !== 'string' || !remote.startsWith('/')) throw Error('Invalid virtual path');
  const p = path.posix.normalize(remote);
  if (p === '..' || p.startsWith('../') || p.includes('\0')) throw Error('Invalid virtual path');
  return p === '.' ? '/' : p;
}
function resolve(remote, write = false, allowRoot = false, account = null) {
  const p = normalize(remote); const bits = p.split('/').filter(Boolean);
  if (!bits.length && allowRoot) return { root: true, virtual: '/' };
  const share = shares.get((bits.shift() || '').toLowerCase());
  if (!share) throw Error('Share not found');
  if (Array.isArray(account?.AllowedShares) && account.AllowedShares.length && !account.AllowedShares.some(x => String(x).toLowerCase() === String(share.VirtualName).toLowerCase())) throw Error('Share not authorized for account');
  if (write && share.Permission !== 1 && share.Permission !== 'ReadWrite') throw Error('Read-only share');
  const local = path.resolve(share.root, ...bits);
  if (local !== share.root && !local.startsWith(share.root + path.sep)) throw Error('Escapes share');
  // Existing reparse points/symlinks are never traversed.
  let probe = share.root;
  for (const bit of bits) { probe = path.join(probe, bit); if (fs.existsSync(probe) && fs.lstatSync(probe).isSymbolicLink()) throw Error('Link traversal denied'); }
  return { local, share, virtual: p, root: false };
}
function attrs(st) { return { mode: st.mode, size: st.size, atime: Math.floor(st.atimeMs / 1000), mtime: Math.floor(st.mtimeMs / 1000) }; }
function tempName(local) { return local + tmpSuffix; }
function visiblePath(r) { const t = tempName(r.local); return fs.existsSync(t) ? t : r.local; }

let connections = 0;
const server = new Server({ hostKeys: [hostKey] }, client => {
  ++connections;
  // ssh2 surfaces a socket reset for port probes and abruptly terminated clients. It is expected
  // network behaviour, not a process-fatal protocol error.
  client.on('error', () => {}).on('close', () => connections--);
  if (connections > opt.MaxConnections) { client.end(); return; }
  let account; const address = client._sock?.remoteAddress || 'unknown';
  client
    .on('authentication', ctx => {
      account = accounts.get(ctx.username);
      if (isBlocked(address, ctx.username)) { audit(ctx.username, address, 'authentication', null, 'temporarily-blocked'); ctx.reject(['password']); return; }
      const allowedKey = ctx.method === 'publickey' && (account?.PublicKeys || []).map(k => parseKey(Buffer.from(k))).find(k => sameKey(ctx.key, k));
      const ok = account && ((ctx.method === 'password' && verifyPassword(account, ctx.password)) ||
        (allowedKey && (!ctx.signature || allowedKey.verify(ctx.blob, ctx.signature, ctx.hashAlgo) === true)));
      // Explicitly advertise supported methods. rclone follows the SSH server's method list
      // after its initial "none" probe and otherwise never offers the configured password.
      if (ok) { clearFailures(address, ctx.username); audit(ctx.username, address, 'authentication', null, 'success'); ctx.accept(); }
      else { recordFailure(address, ctx.username); audit(ctx.username, address, 'authentication', null, 'failed'); ctx.reject(account?.PublicKeys?.length ? ['password', 'publickey'] : ['password']); }
    })
    .on('ready', () => client.on('session', accept => {
      const session = accept();
      session.on('sftp', acceptSftp => {
        const sftp = acceptSftp(); const handles = new Map(); let serial = 1;
        const put = value => { const h = Buffer.from(String(serial++)); handles.set(h.toString('base64'), value); return h; };
        const get = h => handles.get(h.toString('base64'));
        const status = (id, code, msg) => sftp.status(id, code, msg);
        const guarded = (id, action) => { try { action(); } catch (e) { console.error(`SFTP request failed: ${e.message}`); status(id, STATUS_CODE.FAILURE, e.message); } };
        sftp.on('REALPATH', (id, p) => guarded(id, () => { const r = resolve(p, false, true, account); sftp.name(id, [{ filename: r.virtual, longname: r.virtual, attrs: {} }]); }))
          .on('STAT', (id, p) => guarded(id, () => { const r = resolve(p, false, true, account); if (r.root) return sftp.attrs(id, { mode: 0o040755, size: 0 }); sftp.attrs(id, attrs(fs.statSync(visiblePath(r)))); }))
          .on('LSTAT', (id, p) => guarded(id, () => { const r = resolve(p, false, true, account); if (r.root) return sftp.attrs(id, { mode: 0o040755, size: 0 }); sftp.attrs(id, attrs(fs.lstatSync(visiblePath(r)))); }))
          .on('OPENDIR', (id, p) => guarded(id, () => { const r = resolve(p, false, true, account); let entries;
            if (r.root) entries = [...shares.values()].filter(x => !Array.isArray(account?.AllowedShares) || !account.AllowedShares.length || account.AllowedShares.some(y => String(y).toLowerCase() === String(x.VirtualName).toLowerCase())).map(x => ({ filename: x.VirtualName, longname: x.VirtualName, attrs: { mode: 0o040755, size: 0 } }));
            else entries = fs.readdirSync(r.local, { withFileTypes: true }).filter(x => !x.name.endsWith(tmpSuffix)).map(x => { const f = path.join(r.local, x.name); return { filename: x.name, longname: x.name, attrs: attrs(fs.lstatSync(f)) }; });
            sftp.handle(id, put({ entries, index: 0 })); }))
          .on('READDIR', (id, h) => guarded(id, () => { const d = get(h); if (!d || !d.entries) throw Error('Invalid handle'); if (d.index >= d.entries.length) return status(id, STATUS_CODE.EOF); const page = d.entries.slice(d.index, d.index += 64); sftp.name(id, page); }))
          .on('OPEN', (id, p, flags) => guarded(id, () => { const mode = flagsToString(flags); const write = /[wa+]/.test(mode); const r = resolve(p, write, false, account); let file = r.local;
            if (write) { fs.mkdirSync(path.dirname(file), { recursive: true }); const temp = tempName(file); if (!fs.existsSync(temp) && fs.existsSync(file)) fs.copyFileSync(file, temp); if (fs.existsSync(temp) && fs.statSync(temp).size > maxUploadBytes) throw Error('Upload exceeds configured file size limit'); file = temp; }
            const fd = fs.openSync(file, mode); sftp.handle(id, put({ fd, target: r.local, temp: write ? file : null })); }))
          .on('READ', (id, h, offset, length) => guarded(id, () => { const f = get(h); if (!f || f.fd === undefined) throw Error('Invalid handle'); const b = Buffer.alloc(length); const n = fs.readSync(f.fd, b, 0, length, Number(offset)); n ? sftp.data(id, b.subarray(0, n)) : status(id, STATUS_CODE.EOF); }))
          .on('WRITE', (id, h, offset, data) => guarded(id, () => { const f = get(h); if (!f || !f.temp) throw Error('Read-only handle'); if (Number(offset) + data.length > maxUploadBytes) throw Error('Upload exceeds configured file size limit'); fs.writeSync(f.fd, data, 0, data.length, Number(offset)); status(id, STATUS_CODE.OK); }))
          .on('FSTAT', (id, h) => guarded(id, () => { const f = get(h); if (!f || f.fd === undefined) throw Error('Invalid handle'); sftp.attrs(id, attrs(fs.fstatSync(f.fd))); }))
          .on('CLOSE', (id, h) => guarded(id, () => { const f = get(h); if (!f) throw Error('Invalid handle'); if (f.fd !== undefined) fs.closeSync(f.fd); if (f.temp) { fs.renameSync(f.temp, f.target); audit(account?.UserName, address, 'upload', f.target, 'success'); } handles.delete(h.toString('base64')); status(id, STATUS_CODE.OK); }))
          .on('REMOVE', (id, p) => guarded(id, () => { const r = resolve(p, true, false, account); fs.unlinkSync(visiblePath(r)); audit(account?.UserName, address, 'delete', r.virtual, 'success'); status(id, STATUS_CODE.OK); }))
          .on('MKDIR', (id, p) => guarded(id, () => { const r = resolve(p, true, false, account); fs.mkdirSync(r.local); audit(account?.UserName, address, 'mkdir', r.virtual, 'success'); status(id, STATUS_CODE.OK); }))
          .on('RMDIR', (id, p) => guarded(id, () => { const r = resolve(p, true, false, account); fs.rmdirSync(r.local); audit(account?.UserName, address, 'rmdir', r.virtual, 'success'); status(id, STATUS_CODE.OK); }))
          .on('RENAME', (id, a, b) => guarded(id, () => { const from = resolve(a, true, false, account), to = resolve(b, true, false, account); fs.mkdirSync(path.dirname(to.local), { recursive: true }); fs.renameSync(visiblePath(from), to.local); audit(account?.UserName, address, 'rename', `${from.virtual} -> ${to.virtual}`, 'success'); status(id, STATUS_CODE.OK); }));
      });
    }));
});
server.listen(opt.Port, opt.ListenAddress, () => console.log(`Feng Sync SFTP listening on ${opt.ListenAddress}:${opt.Port}`));
function shutdown() { server.close(() => process.exit(0)); setTimeout(() => process.exit(0), 2500).unref(); }
process.on('SIGTERM', shutdown); process.on('SIGINT', shutdown);
process.stdin.resume(); process.stdin.on('end', shutdown);
