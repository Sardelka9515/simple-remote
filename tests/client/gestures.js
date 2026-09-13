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

// The exact layouts the host sends. LayoutTests.ClientFixtureMatchesTheRealLayoutOutput fails if
// this file drifts from the host, so these scenarios always exercise what actually ships.
const FIXTURE_LAYOUTS = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'layouts.json'), 'utf8'));

// One control type no client knows, to prove unknown types are skipped rather than breaking a page.
FIXTURE_LAYOUTS[0].rows[FIXTURE_LAYOUTS[0].rows.length - 1].controls.push({ type: 'whatever-is-new', label: 'Future' });

// ---------------------------------------------------------------- DOM stub

// Every element ever created, so querySelectorAll can be a flat filter instead of a tree walk.
const allElements = [];

function makeElement(id, tag) {
  const listeners = new Map();
  const classes = new Set();

  const el = {
    id,
    tagName: (tag || 'div').toUpperCase(),
    dataset: {},
    style: {},
    textContent: '',
    innerHTML: '',
    value: '',
    type: '',
    min: '', max: '', step: '',
    hidden: false,
    disabled: false,
    firstChild: { textContent: '' },
    childNodes: [],
    _detached: false,
    classList: {
      add(...c) { c.forEach((x) => classes.add(x)); },
      remove(...c) { c.forEach((x) => classes.delete(x)); },
      toggle(c, on) { if (on) classes.add(c); else classes.delete(c); },
      contains(c) { return classes.has(c); },
    },
    addEventListener(type, fn) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(fn);
    },
    removeEventListener() {},
    dispatch(type, ev) {
      for (const fn of listeners.get(type) || []) fn(ev);
    },
    hasListener(type) { return (listeners.get(type) || []).length > 0; },
    removeAttribute() {},
    setAttribute() {},
    setPointerCapture() {},
    releasePointerCapture() {},
    appendChild(child) { el.childNodes.push(child); return child; },
    insertBefore(child) { el.childNodes.push(child); return child; },
    remove() { el._detached = true; },
    closest(sel) { return matches(el, sel) ? el : null; },
    getBoundingClientRect() { return { left: 10, top: 60, width: 355, height: 628, right: 365, bottom: 688 }; },
    click() { el.dispatch('click', { preventDefault() {}, target: el }); },
    focus() {},
  };

  // className is how the app sets classes on created elements, so keep it and classList in step.
  Object.defineProperty(el, 'className', {
    get() { return [...classes].join(' '); },
    set(v) { classes.clear(); String(v).split(/\s+/).filter(Boolean).forEach((c) => classes.add(c)); },
  });

  allElements.push(el);
  return el;
}

/** Supports the simple selectors the app actually uses: .a, .a.b, and .key[data-vk]. */
function matches(el, selector) {
  for (const part of selector.trim().split(',')) {
    const sel = part.trim();
    if (!sel) continue;

    const attr = /\[data-vk\]$/.test(sel);
    const classNames = sel.replace(/\[data-vk\]$/, '').split('.').filter(Boolean);

    if (classNames.every((c) => el.classList.contains(c)) && (!attr || el.dataset.vk !== undefined)) {
      return true;
    }
  }
  return false;
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
    if (sel === '.key[data-vk]') return keyStubs;
    return allElements.filter((e) => !e._detached && matches(e, sel));
  },
  createElement: (tag) => makeElement('created', tag),
  createTextNode: (text) => ({ textContent: String(text), nodeType: 3 }),
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
            layouts: FIXTURE_LAYOUTS,
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

  // ---- layout rendering -------------------------------------------------------

  // 6. The Netflix layout became a tab and a page, built purely from the host description.
  const layoutTabs = documentStub.querySelectorAll('.layout-tab');
  const layoutPages = documentStub.querySelectorAll('.layout-page');
  results.push(['layout tab + page',
    `${layoutTabs.length}/${layoutPages.length}`, 'expect 1/1',
    layoutTabs.length ? 'page=' + layoutPages[0].dataset.page : '']);

  // 7. Every control type rendered, and the unknown one was skipped rather than breaking the page.
  const lcButtons = documentStub.querySelectorAll('.lc-btn');
  const lcVolume = documentStub.querySelectorAll('.lc-volume');
  const lcMedia = documentStub.querySelectorAll('.lc-media');
  const layoutPads = documentStub.querySelectorAll('.layout-pad');
  results.push(['layout controls',
    `btn=${lcButtons.length} media=${lcMedia.length} vol=${lcVolume.length} pad=${layoutPads.length}`,
    'expect btn=3 media=1 vol=1 pad=1', 'unknown type skipped']);

  // 8. A layout button sends the action id the host gave it.
  reset();
  const skip = lcButtons.find((b) => b.textContent === 'Skip intro' ||
    b.childNodes.some((c) => c.textContent === 'Skip intro'));
  if (skip) skip.click();
  const skipSent = sent.filter((f) => f.kind === 'text').map((f) => JSON.parse(f.data));
  results.push(['layout button action',
    skipSent.length ? `${skipSent[0].t}:${skipSent[0].id}` : '(nothing)',
    'expect shortcut:layout:netflix:0:0', '']);

  // 9. The trackpad inside the layout drives the same gesture engine as the built-in one.
  await new Promise((r) => setTimeout(r, 320));
  reset();
  const layoutPad = layoutPads[0];
  if (layoutPad) {
    const at = (type, x, y) => layoutPad.dispatch(type, {
      pointerId: 31, isPrimary: true, pointerType: 'touch',
      clientX: x, clientY: y, timeStamp: now, currentTarget: layoutPad,
      preventDefault() {}, getCoalescedEvents: () => [],
    });
    at('pointerdown', 100, 300);
    advance(60);
    at('pointerup', 100, 300);
    pumpFrames(1);
    await new Promise((r) => setTimeout(r, 320));
  }
  results.push(['layout trackpad tap', buttons(decode(sent)), 'expect 0v 0^', '']);

  // 10. The embedded media panel is the real component: it paints from mediaState exactly like the
  //     Media tab, hides track buttons when asked, and its seek buttons run the host actions.
  socket.onmessage({ data: JSON.stringify({
    t: 'mediaState', active: true, title: 'Stranger Things', artist: 'S4 E1', app: 'firefox',
    status: 'playing', positionMs: 60000, durationMs: 3000000,
    canPrevious: true, canNext: true, canSeek: false,
  }) });
  const panel = lcMedia[0];
  const findIn = (node, predicate) => {
    if (!node) return null;
    if (predicate(node)) return node;
    for (const child of node.childNodes || []) {
      const hit = findIn(child, predicate);
      if (hit) return hit;
    }
    return null;
  };
  const panelTitle = findIn(panel, (n) => n.classList && n.classList.contains('title'));
  const tabTitle = byId('mtitle');
  results.push(['media panel paints',
    `${panelTitle ? panelTitle.textContent : '?'} | tab=${tabTitle.textContent}`,
    'expect Stranger Things | tab=Stranger Things', '']);

  const transportButtons = [];
  (function collect(node) {
    if (!node) return;
    if (node.tagName === 'BUTTON' && node.classList && node.classList.contains('tbtn')) transportButtons.push(node.dataset.icon);
    for (const child of node.childNodes || []) collect(child);
  })(panel);
  results.push(['media panel transport',
    transportButtons.join(' '), 'expect rewind pause forward', 'track buttons hidden, state playing']);

  const panelArt = findIn(panel, (n) => n.classList && n.classList.contains('art'));
  results.push(['media panel artwork', panelArt ? 'shown' : 'hidden', 'expect hidden', '']);

  // Every bundled icon must be a name the client can draw. A glyph or a mangled escape falls back to
  // plain text, which is exactly how "U0001F3AC" ended up on the Netflix tab.
  const tabIcon = documentStub.querySelectorAll('.layout-tab')[0].childNodes[0];
  const buttonIcons = documentStub.querySelectorAll('.lc-icon').map((e) => e.dataset.icon || ('TEXT:' + e.textContent));
  results.push(['layout icons are svg',
    [tabIcon.dataset.icon || ('TEXT:' + tabIcon.textContent)].concat(buttonIcons).join(' '),
    'expect film skip back fullscreen', '']);

  reset();
  const rewind = findIn(panel, (n) => n.tagName === 'BUTTON' && n.dataset.icon === 'rewind');
  const forward = findIn(panel, (n) => n.tagName === 'BUTTON' && n.dataset.icon === 'forward');
  if (rewind) rewind.click();
  if (forward) forward.click();
  const seekIds = sent.filter((f) => f.kind === 'text').map((f) => JSON.parse(f.data))
    .filter((m) => m.t === 'shortcut').map((m) => m.id).join(' ');
  results.push(['media seek buttons', seekIds,
    'expect layout:netflix:1:0:back layout:netflix:1:0:forward', '']);

  // 11. One-finger scroll strip: a vertical drag scrolls, and never moves the cursor or clicks.
  await new Promise((r) => setTimeout(r, 320));
  reset();
  const strip = documentStub.querySelectorAll('.scroll-strip')[0];
  if (strip) {
    const onStrip = (type, y) => strip.dispatch(type, {
      pointerId: 41, isPrimary: true, pointerType: 'touch',
      clientX: 340, clientY: y, timeStamp: now,
      preventDefault() {}, stopPropagation() {}, getCoalescedEvents: () => [],
    });
    onStrip('pointerdown', 200);
    for (let i = 1; i <= 15; i++) { advance(10); onStrip('pointermove', 200 + i * 10); pumpFrames(1); }
    advance(10);
    onStrip('pointerup', 350);
    pumpFrames(6);
  }
  const stripEvents = decode(sent);
  const scrolls = stripEvents.filter((e) => e.op === 'scroll');
  results.push(['scroll strip',
    `scroll=${scrolls.length > 0} move=${stripEvents.filter((e) => e.op === 'move').length} buttons=${buttons(stripEvents) || 'none'}`,
    'expect scroll=true move=0 buttons=none',
    `strips=${documentStub.querySelectorAll('.scroll-strip').length} dy=${scrolls.reduce((s, e) => s + e.dy, 0)} dx=${scrolls.reduce((s, e) => s + e.dx, 0)}`]);

  // 12. Opening the Keyboard tab focuses the type box straight away.
  const typer = byId('typer');
  let focused = 0;
  typer.focus = () => { focused++; };
  const keysTab = makeElement('tab-keys', 'button');
  keysTab.className = 'tab';
  keysTab.dataset.tab = 'keys';
  byId('tabs').dispatch('click', { target: keysTab, preventDefault() {} });
  results.push(['keyboard tab focus', `focus calls=${focused}`, 'expect focus calls=1', '']);

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
