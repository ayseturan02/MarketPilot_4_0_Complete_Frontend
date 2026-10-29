const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const { randomUUID } = require('node:crypto');
const http = require('node:http');

const image = process.env.MARKETPILOT_DOCKER_IMAGE || 'marketpilot:ci';
const name = `marketpilot-check-${randomUUID()}`;
const network = `${name}-network`;
const volume = `${name}-data`;
const domain = 'marketpilot.example.test';
const password = 'container-test-password-123';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
function docker(...args) {
  return execFileSync('docker', args, { encoding: 'utf8', windowsHide: true, timeout: 120000 }).trim();
}

async function main() {
  try {
    docker('network', 'create', network);
    const gateway = JSON.parse(docker('network', 'inspect', network))[0].IPAM.Config[0].Gateway;
    docker('volume', 'create', volume);
    docker('run', '-d', '--name', name, '--network', network,
      '-p', '127.0.0.1::8080', '--read-only', '--tmpfs', '/tmp:rw,size=64m,mode=1777',
      '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
      '-v', `${volume}:/app/Data`,
      '-e', 'ASPNETCORE_ENVIRONMENT=Production',
      '-e', 'Deployment__Mode=public',
      '-e', `Deployment__TrustedProxyIp=${gateway}`,
      '-e', `Deployment__BootstrapGatewayIp=${gateway}`,
      '-e', `AllowedHosts=${domain};localhost;127.0.0.1`,
      '-e', 'Synchronization__IncludeRealStores=false',
      '-e', 'Synchronization__AllowRealWrites=false', image);
    const info = JSON.parse(docker('inspect', name))[0];
    assert.notEqual(info.Config.User, '0', 'application must run without root privileges');
    assert(info.Config.User && info.Config.User !== 'root');
    let base = `http://127.0.0.1:${info.NetworkSettings.Ports['8080/tcp'][0].HostPort}`;
    async function request(url, { remote = false, proto = 'https', cookie, body, headers = {} } = {}) {
      const allHeaders = remote ? {
        Host: domain,
        'X-Forwarded-Host': domain,
        'X-Forwarded-Proto': proto,
        'X-Forwarded-For': '203.0.113.20'
      } : {};
      if (body !== undefined) allHeaders['Content-Type'] = 'application/json';
      if (cookie) allHeaders.Cookie = cookie;
      return new Promise((resolve, reject) => {
        // Use node:http so the Host header reaches the server unchanged on every Node version.
        const req = http.request(base + url, {
          method: body === undefined ? 'GET' : 'POST',
          headers: { ...allHeaders, ...headers }
        }, res => {
          const chunks = [];
          res.on('data', chunk => chunks.push(chunk));
          res.on('error', reject);
          res.on('end', () => {
            const responseHeaders = new Headers();
            for (const [key, value] of Object.entries(res.headers)) {
              for (const item of Array.isArray(value) ? value : [value])
                if (item !== undefined) responseHeaders.append(key, item);
            }
            resolve(new Response(Buffer.concat(chunks), { status: res.statusCode, headers: responseHeaders }));
          });
        });
        req.on('error', reject);
        req.setTimeout(5000, () => req.destroy(new Error('Docker HTTP request timed out')));
        req.end(body === undefined ? undefined : JSON.stringify(body));
      });
    }
    async function ready() {
      for (let i = 0; i < 150; i++) {
        try { if ((await request('/api/health')).ok) return; } catch {}
        await sleep(200);
      }
      throw new Error('Docker application did not become healthy');
    }
    await ready();
    assert.equal((await (await request('/api/health')).json()).mode, 'public');
    assert.equal((await request('/api/health', { headers: { Host: domain } })).status, 400,
      'gateway HTTP requests must use a loopback host');
    assert.equal((await request('/api/health', { remote: true, proto: 'http' })).status, 400,
      'public HTTP must remain blocked');
    assert.equal((await request('/api/health', { remote: true, proto: 'http', headers: {
      'X-Forwarded-Host': 'localhost', 'X-Forwarded-For': gateway
    } })).status, 400, 'forwarded localhost/gateway must not bypass HTTPS');
    const credentials = { name: 'admin', password };
    assert.equal((await request('/api/access/setup', { remote: true, body: credentials })).status, 403,
      'remote clients must not create the first administrator');
    assert.equal((await request('/api/access/setup', { body: credentials })).status, 403,
      'the gateway bootstrap path must also require the one-time setup code');
    const setupToken = docker('exec', name, 'cat', '/app/Data/setup-token.txt');
    const setup = await request('/api/access/setup', { body: { ...credentials, setupToken } });
    assert.equal(setup.status, 200, await setup.clone().text());
    const setCookie = setup.headers.get('set-cookie') || '';
    assert.match(setCookie, /;\s*secure(?:;|$)/i);
    assert.match(setCookie, /;\s*httponly(?:;|$)/i);
    assert.match(setCookie, /;\s*samesite=strict/i);
    const cookie = setCookie.match(/it_session=[^;]+/)?.[0];
    assert(cookie, 'first administrator must receive a protected session');
    assert.equal((await request('/api/products', { remote: true })).status, 401);
    assert.equal((await request('/api/products', { remote: true, cookie })).status, 200);
    docker('restart', name);
    // Docker may allocate a different ephemeral host port when the container restarts.
    const restarted = JSON.parse(docker('inspect', name))[0];
    base = `http://127.0.0.1:${restarted.NetworkSettings.Ports['8080/tcp'][0].HostPort}`;
    await ready();
    assert.equal((await (await request('/api/access/status')).json()).configured, true,
      'administrator account must survive restart');
    assert.equal((await request('/api/products', { remote: true, cookie })).status, 200,
      'data-protection keys and sessions must survive restart');
    console.log('PASS Docker deployment: non-root, read-only filesystem, gateway bootstrap, HTTPS enforcement, authentication and persistent data.');
  } catch (error) {
    try { console.error(docker('logs', '--tail', '80', name)); } catch {}
    throw error;
  } finally {
    for (const args of [['rm', '-f', name], ['volume', 'rm', volume], ['network', 'rm', network]]) {
      try { docker(...args); } catch {}
    }
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
