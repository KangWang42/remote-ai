/* Regression checks for transport selection, pipelining and immutable retries. */
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../web/terminal/bridge.js'), 'utf8');
const settle = () => new Promise(resolve => setImmediate(resolve));
const terminalId = 'a'.repeat(32);
const output = { t: 'out', terminal: { id: terminalId, state: 'running' }, chunks: [], after: 7 };

function page(host = 'test.trycloudflare.com') {
  const requests = [], sockets = [], timers = new Map(), received = [], errors = [], connections = [], listeners = {}, deliveries = [];
  let counter = 0, now = 0;
  class Socket {
    static OPEN = 1;
    constructor(url) { this.url = url; this.readyState = 0; this.sent = []; sockets.push(this); }
    open() { this.readyState = 1; if (this.onopen) this.onopen(); }
    send(text) { this.sent.push(JSON.parse(text)); }
    message(item) { this.onmessage({ data: JSON.stringify(item) }); }
    close(code = 1006) { this.readyState = 3; if (this.onclose) this.onclose({ code }); }
  }
  const document = { hidden: false, addEventListener: (name, handler) => { listeners[name] = handler; } };
  const context = {
    URL, URLSearchParams, TextDecoder, Uint8Array, AbortController, WebSocket: Socket, document,
    location: { hostname: host, host, protocol: 'https:', search: '?id=' + terminalId, href: 'https://' + host + '/terminal/?id=' + terminalId, replace() {} },
    localStorage: { getItem() { return null; } },
    crypto: { getRandomValues(bytes) { bytes.fill(++counter); return bytes; } },
    Date: { now: () => now },
    setTimeout(fn, delay) { const id = ++counter; timers.set(id, { fn, at: now + delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    fetch(url, options = {}) {
      return new Promise((resolve, reject) => {
        const request = { url, options, resolve, reject };
        requests.push(request);
        if (options.signal) options.signal.addEventListener('abort', () => reject(new Error('aborted')));
      });
    }
  };
  context.window = context;
  context.TerminalUI = { receive(item) { received.push(item); context.ProjectTerminal.rendered(item.after); },
    connection: info => connections.push({ ...info }),
    error: text => errors.push(text), inputError: text => errors.push(text) };
  vm.runInNewContext(source, context);
  return { context, requests, sockets, received, errors, connections, listeners, deliveries,
    trackDelivery() { context.TerminalUI.inputState = (state, op, message) => deliveries.push({ state, id: op.id, message }); },
    advance(ms) {
      now += ms;
      for (const [id, timer] of [...timers]) if (timer.at <= now && timers.has(id)) { timers.delete(id); timer.fn(); }
    } };
}

async function check() {
  const live = page();
  live.context.ProjectTerminal.ready();
  const ws = live.sockets[0];
  assert.match(ws.url, /^wss:\/\/test.trycloudflare.com\/api\/terminal\/ws/);
  assert.deepEqual(live.connections.at(-1), { transport: 'websocket', state: 'connecting', reconnects: 0, lastLatency: null });
  ws.open(); ws.message(output);
  assert.equal(live.connections.at(-1).transport, 'websocket');
  assert.equal(live.connections.at(-1).state, 'connected');
  assert.equal(live.connections.at(-1).reconnects, 0);
  assert.equal(live.connections.at(-1).lastLatency, null, 'the socket handshake must not be presented as recent network latency');
  const measurement = live.requests.find(item => item.url === '../api/session');
  live.advance(37); measurement.resolve({ status: 200, ok: true, json: async () => ({ signed_in: true }) }); await settle();
  assert.equal(live.connections.at(-1).lastLatency, 37, 'latency must come from an immediate response, with a concrete round-trip time');
  live.context.ProjectTerminal.input('a');
  live.context.ProjectTerminal.input('b');
  assert.equal(ws.sent.length, 2, 'typing must not wait for the previous acknowledgment');
  const originals = ws.sent.map(item => JSON.stringify(item));
  ws.close();
  assert.equal(live.connections.at(-1).state, 'reconnecting');
  assert.equal(live.connections.at(-1).reconnects, 1);
  live.advance(300);
  const resumed = live.sockets[1];
  assert.match(resumed.url, /after=7$/);
  resumed.open(); resumed.message(output);
  assert.deepEqual(resumed.sent.map(item => JSON.stringify(item)), originals, 'lost acknowledgments retry identical IDs and payloads');
  for (const op of resumed.sent) resumed.message({ t: 'ack', id: op.id, status: 200, state: 'queued' });
  live.advance(21000);
  assert.equal(resumed.readyState, 1, 'acknowledged operations must cancel their timeout');

  const reconnecting = page();
  reconnecting.context.ProjectTerminal.ready();
  const previous = reconnecting.sockets[0];
  previous.open(); previous.message(output); previous.close(); reconnecting.advance(300);
  const reconnects = reconnecting.connections.at(-1).reconnects;
  previous.onclose({ code: 1006 });  // a late event from the old connection
  assert.equal(reconnecting.connections.at(-1).reconnects, reconnects, 'a stale close must not count as another reconnect');
  reconnecting.advance(4000); reconnecting.advance(0);
  assert.ok(reconnecting.requests.some(item => /&wait=20$/.test(item.url)), 'an old close event must not cancel the new connection timeout');
  assert.equal(reconnecting.connections.at(-1).state, 'reconnecting', 'a failed retry must retain the outage state');
  assert.equal(reconnecting.connections.at(-1).reconnects, 1, 'retries during one outage must count only once');

  const old = page();
  old.trackDelivery();
  old.context.ProjectTerminal.ready(); old.sockets[0].close(); old.advance(0);
  assert.match(old.requests[0].url, /&wait=20$/, 'quick tunnels must bypass unsupported SSE when WebSocket fails');
  assert.equal(old.connections.at(-1).transport, 'http');
  assert.equal(old.connections.at(-1).reconnects, 0, 'fallback before the first connection is not a reconnect');
  old.advance(20000);
  old.requests[0].resolve({ status: 200, ok: true, json: async () => output }); await settle();
  assert.equal(old.connections.at(-1).state, 'connected');
  assert.equal(old.connections.at(-1).lastLatency, null, 'twenty seconds of long-poll waiting must not count as latency');
  old.advance(64);
  old.requests.find(item => item.url === '../api/session').resolve({ status: 200, ok: true, json: async () => ({ signed_in: true }) }); await settle();
  assert.equal(old.connections.at(-1).lastLatency, 64);
  old.context.ProjectTerminal.input('first');
  let request = old.requests.find(item => item.options.method === 'POST');
  const first = JSON.parse(request.options.body);
  request.reject(new Error('lost response')); await settle();
  assert.equal(old.deliveries.at(-1).state, 'retrying');
  assert.equal(old.errors.length, 0, 'a retried delivery must not be presented as rejected');
  old.context.ProjectTerminal.input('second');
  request = old.requests.filter(item => item.options.method === 'POST').at(-1);
  assert.deepEqual(JSON.parse(request.options.body), first, 'new input must not change an operation already sent');
  request.resolve({ status: 200, json: async () => ({ state: 'queued' }) }); await settle();
  assert.equal(old.deliveries.at(-1).state, 'accepted');
  assert.equal(old.deliveries.at(-1).id, first.id);
  const next = JSON.parse(old.requests.filter(item => item.options.method === 'POST').at(-1).options.body);
  assert.notEqual(next.id, first.id);
  assert.equal(next.data, 'second');

  const sse = page('relay.example');
  sse.context.ProjectTerminal.ready(); sse.sockets[0].close(); sse.advance(0);
  const sseRequest = sse.requests[0];
  const sseData = Uint8Array.from(Array.from('data: ' + JSON.stringify(output) + '\n\n'), character => character.charCodeAt(0));
  let sseRead = 0, finishSse;
  sseRequest.resolve({ status: 200, body: { getReader: () => ({ read: () => sseRead++ ? new Promise(resolve => { finishSse = resolve; }) : Promise.resolve({ value: sseData, done: false }) }) } });
  await settle(); await settle();
  const sseConnected = sse.connections.find(info => info.transport === 'sse' && info.state === 'connected');
  assert.ok(sseConnected, 'SSE must report a connected state after its first event');
  assert.equal(sseConnected.lastLatency, null);
  sse.advance(24);
  sse.requests.find(item => item.url === '../api/session').resolve({ status: 200, ok: true, json: async () => ({ signed_in: true }) }); await settle();
  assert.equal(sse.connections.at(-1).lastLatency, 24);
  sse.advance(55000); finishSse({ done: true }); await settle();
  assert.equal(sse.connections.at(-1).state, 'connected', 'normal SSE renewal must keep the logical connection alive');
  assert.equal(sse.connections.at(-1).reconnects, 0, 'the relay ends SSE responses regularly; renewal is not an outage');
  sse.advance(30);
  assert.ok(sse.requests.some(item => /\/stream\?.*after=7$/.test(item.url)), 'SSE renewal must preserve the output cursor');

  const buffered = page('relay.example');
  buffered.context.ProjectTerminal.ready(); buffered.sockets[0].close(); buffered.advance(0);
  assert.match(buffered.requests[0].url, /\/stream\?/);
  assert.equal(buffered.connections.at(-1).transport, 'sse');
  buffered.advance(4000); await settle();
  assert.match(buffered.requests.at(-1).url, /&wait=20$/, 'buffered first event must fall back within four seconds');
  assert.equal(buffered.connections.at(-1).transport, 'http');
  const stale = buffered.requests.at(-1);
  const beforeHide = buffered.connections.at(-1).reconnects;
  buffered.context.document.hidden = true; buffered.listeners.visibilitychange(); await settle();
  assert.equal(stale.options.signal.aborted, true);
  assert.equal(buffered.connections.at(-1).reconnects, beforeHide, 'pausing the page must not count as a reconnect');
  assert.equal(buffered.connections.at(-1).state, 'paused');
  buffered.context.document.hidden = false; buffered.listeners.visibilitychange();
  const current = buffered.requests.at(-1);
  assert.notEqual(current, stale);
  current.resolve({ status: 200, ok: true, json: async () => output }); await settle();
  assert.equal(buffered.received.length, 1, 'only the current reader may update the screen');
  assert.equal(buffered.connections.at(-1).state, 'connected');
  assert.equal(buffered.connections.at(-1).lastLatency, null);
  buffered.advance(0);
  const heldRetry = buffered.requests.findLast(item => /&wait=20$/.test(item.url));
  heldRetry.reject(new Error('network lost')); await settle();
  assert.equal(buffered.connections.at(-1).reconnects, beforeHide + 1);
  buffered.advance(1500);
  buffered.requests.findLast(item => /&wait=20$/.test(item.url)).reject(new Error('still offline')); await settle();
  assert.equal(buffered.connections.at(-1).reconnects, beforeHide + 1, 'HTTP failure retries must use the same reconnect count as WebSocket');
  const suspended = page();
  suspended.context.ProjectTerminal.ready(); suspended.sockets[0].open(); suspended.sockets[0].message(output);
  suspended.context.document.hidden = true; suspended.listeners.visibilitychange(); await settle();
  assert.equal(suspended.connections.at(-1).state, 'paused');
  assert.equal(suspended.connections.at(-1).reconnects, 0);
  assert.equal(suspended.requests[0].options.signal.aborted, true, 'backgrounding must cancel latency probes too');
  const fresh = page();
  fresh.context.ProjectTerminal.ready(); fresh.sockets[0].open();
  fresh.sockets[0].message({ ...output, terminal: { id: terminalId, state: 'closed', tool: 'claude', dir: 'new-project', session: '' } });
  fresh.context.ProjectTerminal.again();
  const freshRequest = fresh.requests.find(item => item.options.method === 'POST');
  const freshPayload = JSON.parse(freshRequest.options.body);
  assert.equal(freshPayload.history, false, 'a terminal without an ID must restart fresh instead of opening the resume picker');
  assert.equal(freshPayload.session, undefined);
  freshRequest.resolve({ status: 200, json: async () => ({ terminal: terminalId }) }); await settle();
  const resumedPage = page();
  resumedPage.context.ProjectTerminal.ready(); resumedPage.sockets[0].open();
  const savedSession = '12345678-1234-1234-1234-123456789abc';
  resumedPage.sockets[0].message({ ...output, terminal: { id: terminalId, state: 'closed', tool: 'claude', dir: 'demo', session: savedSession } });
  resumedPage.context.ProjectTerminal.again();
  assert.equal(JSON.parse(resumedPage.requests.find(item => item.options.method === 'POST').options.body).session, savedSession, 'continue must preserve the exact conversation');
  console.log('bridge transport checks passed');
}

check().catch(error => { console.error(error); process.exitCode = 1; });
