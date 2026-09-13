// Gesture tests for the web client.
//
//   node tests/client/gestures.js src/SimpleRemote.Host/wwwroot
//
// Loads the REAL protocol.js and app.js under a minimal DOM stub, feeds synthetic pointer events
// into the actual handlers, and decodes the binary frames the client emits. It therefore tests
// the shipped code rather than a reimplementation of it - which matters here, because the exact
// sequence of button events is the difference between a drag and an accidental double click.
//
// Exits non-zero if any scenario fails.

const fs = require('fs');
const vm = require('vm');
const path = require('path');

const ROOT = process.argv[2] || path.join(__dirname, '..', '..', 'src', 'SimpleRemote.Host', 'wwwroot');

// ---------------------------------------------------------------- DOM stub

function makeElement(id) {
  const listeners = new Map();
  const el = {
    id,
    dataset: {},
    style: {},
    textContent: '',
    innerHTML: '',
    value: '',
    hidden: false,
    disabled: false,
    firstChild: { textContent: '' },
    classList: {
      _set: new Set(),
      add(...c) { c.forEach((x) => this._set.add(x)); },
      remove(...c) { c.forEach((x) => this._set.delete(x)); },
      toggle(c, on) { if (on) this._set.add(c); else this._set.delete(c); },
      contains(c) { return this._set.has(c); },
    },
    addEventListener(type, fn) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(fn);
    },
    removeEventListener() {},
    dispatch(type, ev) {
      const fns = listeners.get(type) || [];
      for (const fn of fns) fn(ev);
    },
    hasListener(type) { return (listeners.get(type) || []).length > 0; },
    removeAttribute() {},
    setAttribute() {},
    setPointerCapture() {},
    releasePointerCapture() {},
    appendChild() {},
    getBoundingClientRect() { return { left: 10, top: 60, width: 355, height: 628, right: 365, bottom: 688 }; },
    click() { el.dispatch('click', { preventDefault() {} }); },
    focus() {},
  };
  return el;
}

const elements = new Map();
const byId = (id) => {
  if (!elements.has(id)) elements.set(id, makeElement(id));
  return elements.get(id);
};

const keyStubs = [];
for (const vk of [27, 9, 8, 46, 13, 36, 35, 33, 34, 93]) {
  const e = makeElement('key' + vk);
  e.dataset.vk = String(vk);
  keyStubs.push(e);
}

const documentStub = {
  _listeners: new Map(),
  getElementById: byId,
  querySelector: (sel) => {
    const m = /data-vk="(\d+)"/.exec(sel);
    if (m) return keyStubs.find((k) => k.dataset.vk === m[1]) || makeElement('k');
    return makeElement('q');
  },
  querySelectorAll: (sel) => {
    if (sel === '.tab') return [];
    if (sel === '.mb') return [];
    if (sel === '.mod') return [];
    if (sel === '.key[data-vk]') return keyStubs;
    if (sel === '.page') return [];
    return [];
  },
  createElement: () => makeElement('created'),
  addEventListener(type, fn) {
    if (!this._listeners.has(type)) this._listeners.set(type, []);
    this._listeners.get(type).push(fn);
  },
  visibilityState: 'visible',
  hidden: false,
  activeElement: null,
};

// ---------------------------------------------------------------- transport stub

const sent = [];          // every frame the client sends
let socket = null;

class FakeWebSocket {
  constructor(url) {
    this.url = url;
    this.readyState = 1;
    this.bufferedAmount = 0;
    this.binaryType = 'arraybuffer';
    socket = this;
    setImmediate(() => { if (this.onopen) this.onopen(); });
  }
  send(data) {
    if (typeof data === 'string') {
      sent.push({ kind: 'text', data });
      const msg = JSON.parse(data);
      if (msg.t === 'auth') {
        // Authenticate, then deliver the config the real host would send.
        setImmediate(() => {
          this.onmessage({ data: JSON.stringify({ t: 'authResult', ok: true }) });
          this.onmessage({ data: JSON.stringify({
            t: 'config',
            hostName: 'TEST',
            shortcuts: [],
            pointer: { sensitivity: 0.55, acceleration: 0.4, maxSpeed: 3, scrollSpeed: 1,
                       naturalScroll: true, tapHoldMs: 200, screenWidth: 1920, screenHeight: 1080 },
          }) });
        });
      }
    } else {
      sent.push({ kind: 'bin', data: Buffer.from(data.buffer ? data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength) : data) });
    }
  }
  close() { this.readyState = 3; if (this.onclose) this.onclose(); }
}
FakeWebSocket.OPEN = 1;

// ---------------------------------------------------------------- frame pump

let rafQueue = [];
let now = 1000;

const sandbox = {
  console,
  setTimeout,
  clearTimeout,
  setInterval: () => 0,
  clearInterval: () => {},
  setImmediate,
  document: documentStub,
  WebSocket: FakeWebSocket,
  performance: { now: () => now },
  requestAnimationFrame: (fn) => { rafQueue.push(fn); return rafQueue.length; },
  cancelAnimationFrame: () => {},
  localStorage: {
    _m: new Map([['simpleremote.creds', JSON.stringify({ deviceId: 'd', token: 't', name: 'test' })]]),
    getItem(k) { return this._m.has(k) ? this._m.get(k) : null; },
    setItem(k, v) { this._m.set(k, String(v)); },
    removeItem(k) { this._m.delete(k); },
    clear() { this._m.clear(); },
  },
  navigator: { platform: 'test', userAgent: 'test' },
  location: { protocol: 'http:', host: 'localhost:8787', hash: '', pathname: '/' },
  history: { replaceState() {} },
  fetch: () => Promise.reject(new Error('no network in harness')),
  Image: function () { return { src: '', alt: '' }; },
  isSecureContext: false,
  JSON, Math, Date, Object, Array, Number, String, Boolean, Error, Promise,
  ArrayBuffer, DataView, Uint8Array, parseFloat, parseInt,
};
sandbox.window = sandbox;
sandbox.globalThis = sandbox;

vm.createContext(sandbox);
for (const file of ['protocol.js', 'app.js']) {
  vm.runInContext(fs.readFileSync(path.join(ROOT, file), 'utf8'), sandbox, { filename: file });
}

function pumpFrames(count) {
  for (let i = 0; i < count; i++) {
    const q = rafQueue;
    rafQueue = [];
    now += 16.7;
    for (const fn of q) fn();
  }
}

// ---------------------------------------------------------------- gesture driving

const pad = byId('pad');

function pointer(type, x, y, extra) {
  const ev = Object.assign({
    pointerId: 1, isPrimary: true, pointerType: 'touch',
    clientX: x, clientY: y, timeStamp: now,
    preventDefault() {}, getCoalescedEvents: () => [],
  }, extra || {});
  pad.dispatch(type, ev);
}

function advance(ms) { now += ms; }

function decode(frames) {
  const out = [];
  for (const f of frames) {
    if (f.kind !== 'bin') continue;
    const b = f.data;
    let i = 0;
    while (i < b.length) {
      const op = b[i];
      if (op === 0x01) { out.push({ op: 'move', dx: b.readInt16LE(i + 1), dy: b.readInt16LE(i + 3) }); i += 5; }
      else if (op === 0x02) { out.push({ op: 'button', btn: b[i + 1], down: b[i + 2] === 1 }); i += 3; }
      else if (op === 0x03) { out.push({ op: 'scroll', dx: b.readInt16LE(i + 1), dy: b.readInt16LE(i + 3) }); i += 5; }
      else if (op === 0x04) { out.push({ op: 'key', vk: b.readUInt16LE(i + 1), down: b[i + 3] === 1 }); i += 4; }
      else if (op === 0x05) { i += 5; } // ping
      else { out.push({ op: 'UNKNOWN', code: op }); break; }
    }
  }
  return out;
}

function buttons(events) {
  return events.filter((e) => e.op === 'button').map((e) => `${e.btn}${e.down ? 'v' : '^'}`).join(' ');
}

function reset() { sent.length = 0; }
function waitTicks() { return new Promise((r) => setImmediate(() => setImmediate(r))); }

// ---------------------------------------------------------------- scenarios

async function main() {
  await waitTicks();
  await waitTicks();
  pumpFrames(2);

  const results = [];

  // 1. Single tap -> exactly one click, released after the hold.
  reset();
  pointer('pointerdown', 100, 300);
  advance(60);
  pointer('pointerup', 100, 300);
  pumpFrames(1);
  const tapImmediate = buttons(decode(sent));
  await new Promise((r) => setTimeout(r, 320));
  pumpFrames(1);
  results.push(['single tap', buttons(decode(sent)), 'expect 0v 0^', tapImmediate]);

  // 2. Tap-and-a-half: tap, then press + move + lift. Must be ONE down and ONE up.
  reset();
  pointer('pointerdown', 100, 300);
  advance(60);
  pointer('pointerup', 100, 300);          // tap -> holds button down
  advance(90);                             // well inside the 200ms hold
  pointer('pointerdown', 102, 301);        // adopts the held button
  for (let i = 1; i <= 12; i++) { advance(10); pointer('pointermove', 102 + i * 9, 301 + i * 5); pumpFrames(1); }
  advance(20);
  pointer('pointerup', 210, 361);
  pumpFrames(2);
  await new Promise((r) => setTimeout(r, 320));
  results.push(['tap-and-a-half drag', buttons(decode(sent)), 'expect 0v 0^ (one click, no double)', '']);

  // 3. Two quick taps in place -> a real double click.
  reset();
  pointer('pointerdown', 100, 300);
  advance(50);
  pointer('pointerup', 100, 300);
  advance(80);
  pointer('pointerdown', 101, 300);
  advance(50);
  pointer('pointerup', 101, 300);
  pumpFrames(1);
  await new Promise((r) => setTimeout(r, 320));
  results.push(['double tap', buttons(decode(sent)), 'expect 0v 0^ 0v 0^', '']);

  // 4. Plain drag with no preceding tap -> motion only, no buttons.
  reset();
  await new Promise((r) => setTimeout(r, 320));
  reset();
  pointer('pointerdown', 100, 400);
  for (let i = 1; i <= 12; i++) { advance(10); pointer('pointermove', 100 + i * 9, 400); pumpFrames(1); }
  advance(20);
  pointer('pointerup', 208, 400);
  pumpFrames(2);
  const ev4 = decode(sent);
  results.push(['plain move', buttons(ev4) || '(none)', 'expect (none)', `moves=${ev4.filter((e) => e.op === 'move').length}`]);

  // 5. Two-finger tap -> right click.
  reset();
  await new Promise((r) => setTimeout(r, 320));
  reset();
  pointer('pointerdown', 100, 300, { pointerId: 1, isPrimary: true });
  pointer('pointerdown', 160, 300, { pointerId: 2, isPrimary: false });
  advance(60);
  pointer('pointerup', 160, 300, { pointerId: 2, isPrimary: false });
  pointer('pointerup', 100, 300, { pointerId: 1, isPrimary: true });
  pumpFrames(1);
  results.push(['two-finger tap', buttons(decode(sent)), 'expect 1v 1^', '']);

  console.log('');
  let failed = 0;
  for (const [name, got, want, note] of results) {
    const pass = want.includes(got);
    if (!pass) failed++;
    console.log(`${pass ? 'PASS' : 'FAIL'}  ${name.padEnd(22)} got: ${String(got).padEnd(16)} ${want}${note ? '   [' + note + ']' : ''}`);
  }

  if (failed > 0) {
    console.error(`\n${failed} gesture scenario(s) failed`);
    process.exit(1);
  }
}

main().catch((e) => { console.error('HARNESS ERROR:', e); process.exit(1); });
