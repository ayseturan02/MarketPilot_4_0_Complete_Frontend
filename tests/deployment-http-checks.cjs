const assert = require('node:assert/strict');
const fs = require('node:fs');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

const root = path.resolve(__dirname, '..');
const app = path.join(root, 'src', 'MarketPilot.Web', 'bin', 'Debug', 'net8.0', 'MarketPilot.Web.dll');
const domain = 'marketpilot.example.test';
const origin = `https://${domain}`;
const host = domain;
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function main() {
  const probe = net.createServer();
  await new Promise((resolve, reject) => probe.once('error', reject).listen(0, '127.0.0.1', resolve));
  const port = probe.address().port;
  await new Promise(resolve => probe.close(resolve));

  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'marketpilot-public-http-'));
  const base = `http://127.0.0.1:${port}`;
  const env = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Production',
    DOTNET_ENVIRONMENT: 'Production',
    Deployment__Mode: 'public',
    Deployment__TrustedProxyIp: '127.0.0.1',
    AllowedHosts: `${domain};localhost;127.0.0.1`
  };
  for (const key of Object.keys(env)) if (/^MarketplaceAccounts__/i.test(key)) delete env[key];
  const child = spawn('dotnet', [app, '--urls', base, '--contentRoot', temp], {
    cwd: root,
    env,
    stdio: 'ignore',
    windowsHide: true
  });
  const remoteHeaders = {
    Host: host,
    'X-Forwarded-Host': host,
    'X-Forwarded-Proto': 'https',
    'X-Forwarded-For': '203.0.113.20'
  };
  async function request(url, { body, cookie, remote = true, requestOrigin } = {}) {
    const headers = remote ? { ...remoteHeaders } : {};
    if (requestOrigin) headers.Origin = requestOrigin;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (cookie) headers.Cookie = cookie;
    return fetch(base + url, {
      method: body === undefined ? 'GET' : 'POST',
      headers,
      ...(body === undefined ? {} : { body: JSON.stringify(body) })
    });
  }

  try {
    let ready = false;
    for (let i = 0; i < 100; i++) {
      if (child.exitCode !== null) throw new Error('Public-mode test server exited during startup');
      try {
        if ((await request('/api/health')).ok) { ready = true; break; }
      } catch {}
      await sleep(100);
    }
    assert(ready, 'public-mode server did not become ready');
    assert.equal((await (await request('/api/health')).json()).mode, 'public');
    assert.equal((await request('/api/access/status')).status, 200);
    assert.equal((await request('/api/access/setup', {
      body: { name: 'admin', password: 'a-strong-test-password' },
      requestOrigin: origin
    })).status, 403, 'remote clients must not bootstrap the first admin');
    assert.equal((await request('/api/products')).status, 403, 'API must remain closed until local bootstrap');

    const setup = await request('/api/access/setup', {
      body: { name: 'admin', password: 'a-strong-test-password' },
      remote: false,
      requestOrigin: base
    });
    assert.equal(setup.status, 200, await setup.clone().text());
    const setCookie = setup.headers.get('set-cookie') || '';
    assert.match(setCookie, /;\s*secure(?:;|$)/i, 'public session cookie must require HTTPS');
    assert.match(setCookie, /;\s*httponly(?:;|$)/i);
    assert.match(setCookie, /;\s*samesite=strict/i);
    const cookie = setCookie.match(/it_session=[^;]+/)?.[0];
    assert(cookie, 'admin setup did not issue a session cookie');

    assert.equal((await request('/api/products')).status, 401, 'remote API must require login');
    assert.equal((await request('/api/products', { cookie })).status, 200);
    assert.equal((await request('/api/integrations/connections', { cookie })).status, 200);
    assert.equal((await request('/api/catalog/products', { cookie })).status, 200);
    assert.equal((await request('/api/access/password', {
      body: { currentPassword: 'wrong', newPassword: 'another-strong-test-password' },
      cookie,
      requestOrigin: 'https://attacker.example'
    })).status, 403, 'cross-origin mutations must remain blocked behind the proxy');
    console.log('PASS public deployment HTTP: proxy trust, first-admin bootstrap, HTTPS cookie, login gate, authenticated local-only APIs and CSRF.');
  } finally {
    child.kill();
    await new Promise(resolve => child.once('exit', resolve));
    fs.rmSync(temp, { recursive: true, force: true });
  }
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
