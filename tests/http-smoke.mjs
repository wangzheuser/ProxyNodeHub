import assert from 'node:assert/strict';

// Run against an isolated service with AUTO_REFRESH=false and a synthetic password.
const base = new URL(process.env.BASE_URL ?? 'http://127.0.0.1:8389');
const password = process.env.SMOKE_PASSWORD ?? 'synthetic-test-password-only';
assert(['http:', 'https:'].includes(base.protocol) && !base.username && !base.password,
  'BASE_URL must be an HTTP(S) service address without credentials');
const cookies = new Map();
let csrfToken;

async function request(path, { method = 'GET', body, csrf = true } = {}) {
  const headers = { Cookie: [...cookies].map(([name, value]) => `${name}=${value}`).join('; ') };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (csrf && method !== 'GET' && csrfToken) headers['X-CSRF-TOKEN'] = csrfToken;
  const response = await fetch(new URL(path, base), {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body),
    redirect: 'manual', signal: AbortSignal.timeout(15_000),
  });
  for (const cookie of response.headers.getSetCookie()) {
    const pair = cookie.split(';', 1)[0];
    const index = pair.indexOf('=');
    assert(index > 0, 'Server emitted a malformed cookie');
    const name = pair.slice(0, index);
    const value = pair.slice(index + 1);
    if (value) cookies.set(name, value);
    else cookies.delete(name);
  }
  return response;
}

function status(response, expected, label) {
  assert.equal(response.status, expected, `${label}: HTTP status`);
}

async function session(authenticated) {
  const response = await request('/api/session');
  status(response, 200, 'Session');
  const value = await response.json();
  assert.equal(value.authenticated, authenticated, 'Session authentication state');
  assert(typeof value.csrfToken === 'string' && value.csrfToken.length > 0, 'Session issues a CSRF token');
  csrfToken = value.csrfToken;
}

function assertNoSecretFields(value) {
  if (value === null || typeof value !== 'object') return;
  for (const [key, child] of Object.entries(value)) {
    assert(!/^(github_?token|token|admin_?password|password|api_?key|subscheck_?api_?key)$/i.test(key),
      'Public state must not contain credential fields');
    assertNoSecretFields(child);
  }
}

status(await request('/live'), 200, 'Liveness');
status(await request('/api/state'), 401, 'Anonymous state access');
const page = await request('/');
status(page, 200, 'Web page');
const csp = page.headers.get('content-security-policy') ?? '';
assert(csp.includes("default-src 'self'") && csp.includes("frame-ancestors 'none'") && !csp.includes("'unsafe-inline'"),
  'Web page enforces a same-origin CSP without inline code');
assert.equal(page.headers.get('x-content-type-options'), 'nosniff', 'Web page disables MIME sniffing');
await session(false);
status(await request('/api/connections'), 401, 'Anonymous connection settings');
status(await request('/api/connections/github', { method: 'PUT', body: { mode: 'disabled' } }), 401,
  'Anonymous credential mutation');
status(await request('/api/login', { method: 'POST', body: { password }, csrf: false }), 400, 'Login without CSRF');
status(await request('/api/login', { method: 'POST', body: { password: `${password}-wrong` } }), 401, 'Wrong password');
status(await request('/api/login', { method: 'POST', body: { password } }), 204, 'Login');
await session(true);
const stateResponse = await request('/api/state');
status(stateResponse, 200, 'Authenticated state');
const state = await stateResponse.json();
assert.equal(typeof state.tokenConfigured, 'boolean', 'Only token configuration status is exposed');
assertNoSecretFields(state);
assert(Array.isArray(state.repositories) && Array.isArray(state.favorites) && Array.isArray(state.history),
  'State includes the public collection contract');
assert.equal(state.feedPath, '/subscriptions.txt', 'Feed URL contract');
const original = state.settings;
assert.equal(original.autoRefresh, false, 'Smoke service must disable auto refresh to avoid external scans');
const connections = await (await request('/api/connections')).json();
assertNoSecretFields(connections);
assert.equal(connections.github.source, 'environment', 'Isolated smoke starts with environment defaults');
assert.equal(connections.checker.source, 'environment', 'Isolated checker starts with environment defaults');
const syntheticToken = 'synthetic-smoke-github-token';
const syntheticKey = 'synthetic-smoke-checker-key';
status(await request('/api/connections/github', { method: 'PUT', body: { mode: 'custom', token: syntheticToken }, csrf: false }),
  400, 'Credential mutation without CSRF');
status(await request('/api/connections/github', { method: 'PUT', body: { mode: 'custom', token: 'bad\r\nheader' } }),
  400, 'Header injection rejected');
try {
  status(await request('/api/connections/github', { method: 'PUT', body: { mode: 'custom', token: syntheticToken } }),
    204, 'Save GitHub token online');
  status(await request('/api/connections/checker', { method: 'PUT', body: {
    mode: 'custom', apiUrl: 'http://127.0.0.1:9', apiKey: syntheticKey, webUrl: null,
  } }), 204, 'Save checker connection online');
  const publicConnections = await (await request('/api/connections')).json();
  const publicState = await (await request('/api/state')).json();
  assertNoSecretFields(publicConnections);
  assertNoSecretFields(publicState);
  assert.equal(publicConnections.github.source, 'custom');
  assert.equal(publicConnections.github.configured, true);
  assert.equal(publicConnections.checker.configured, true);
  assert.equal(publicState.tokenConfigured, true);
  assert(!JSON.stringify([publicConnections, publicState]).includes(syntheticToken));
  assert(!JSON.stringify([publicConnections, publicState]).includes(syntheticKey));
  status(await request('/api/connections/checker', { method: 'PUT', body: {
    mode: 'custom', apiUrl: 'http://127.0.0.1:10', apiKey: '',
  } }), 400, 'Changing checker origin requires explicit key');
  status(await request('/api/connections/github', { method: 'PUT', body: { mode: 'disabled' } }), 204, 'Disable GitHub token');
  assert.equal((await (await request('/api/state')).json()).tokenConfigured, false);
} finally {
  status(await request('/api/connections/github', { method: 'PUT', body: { mode: 'environment' } }), 204, 'Restore GitHub environment');
  status(await request('/api/connections/checker', { method: 'PUT', body: { mode: 'environment' } }), 204, 'Restore checker environment');
}
for (const endpoint of ['/api/search-history', '/api/mirrors', '/api/logs'])
  status(await request(endpoint), 200, `Read ${endpoint}`);
status(await request('/api/favorites/batch', { method: 'PUT', body: { fullNames: [], favorite: true } }), 400, 'Empty batch rejected');
status(await request('/api/recheck', { method: 'POST', body: { fullNames: [] } }), 400, 'Empty recheck rejected');
status(await request('/api/checker/stop', { method: 'POST', csrf: false }), 400, 'Checker mutation requires CSRF');
if (!connections.checker.configured)
  for (const endpoint of ['/api/checker/config', '/api/checker/logs', '/api/checker/version'])
    status(await request(endpoint), 503, `Unconfigured ${endpoint} is explicit failure`);
status(await request('/api/settings', { method: 'PUT', body: original, csrf: false }), 400, 'Settings without CSRF');
status(await request('/api/settings', { method: 'PUT', body: { ...original, refreshHours: 0 } }), 400, 'Invalid settings');
const changed = { ...original, refreshHours: original.refreshHours === 1 ? 2 : 1 };
try {
  status(await request('/api/settings', { method: 'PUT', body: changed }), 204, 'Save settings');
  const persisted = await request('/api/state');
  status(persisted, 200, 'Read saved settings');
  assert.deepEqual((await persisted.json()).settings, changed, 'Settings update is observable');
} finally {
  status(await request('/api/settings', { method: 'PUT', body: original }), 204, 'Restore settings');
}
const restored = await request('/api/state');
status(restored, 200, 'Read restored settings');
assert.deepEqual((await restored.json()).settings, original, 'Original settings are restored');
const feed = await request('/subscriptions.txt');
status(feed, state.generatedAt === null ? 503 : 200, 'Public subscription feed');
if (feed.status === 200) {
  const links = (await feed.text()).trim().split(/\r?\n/);
  assert(links.length > 0 && links.every(link => {
    const url = new URL(link);
    return url.protocol === 'https:' && !url.username && !url.password && !url.search &&
      ['raw.githubusercontent.com', 'nodes.udptoos.com'].includes(url.hostname);
  }), 'Feed contains only supported public-source URLs');
}
status(await request('/api/logout', { method: 'POST' }), 204, 'Logout');
status(await request('/api/state'), 401, 'State access after logout');
await session(false);
console.log('HTTP authentication, CSRF, online credentials, settings, management boundaries and feed smoke checks passed.');
