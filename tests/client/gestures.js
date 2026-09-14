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

// What the fake host answers. Scenarios swap these to simulate a changed config or throttling.
let hostLayouts = FIXTURE_LAYOUTS;
let hostAuthResult = { t: 'authResult', ok: true };

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
          if (!this.onmessage) return;
          this.onmessage({ data: JSON.stringify(hostAuthResult) });
          if (!hostAuthResult.ok || !this.onmessage) return;
          this.onmessage({ data: JSON.stringify({
            t: 'config',
            hostName: 'TEST',
            shortcuts: [],
            layouts: hostLayouts,
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
sandbox.addEventListener = () => {};

vm.createContext(sandbox);
for (const file of ['protocol.js', 'motion.js', 'app.js']) {
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
      else if (op === 0x06) { out.push({ op: 'time', t: b.readUInt32LE(i + 1) / 10 }); i += 5; }
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

  // ---- connection recovery ----------------------------------------------------

  const waitMs = (ms) => new Promise((r) => setTimeout(r, ms));
  const visiblePage = () => {
    const shown = documentStub.querySelectorAll('.layout-page').filter((p) => !p.hidden);
    return shown.length ? shown[0].dataset.page : '(none)';
  };

  // 13. A reconnect that resends the same config leaves the layout page on screen, untouched.
  byId('tabs').dispatch('click', { target: documentStub.querySelectorAll('.layout-tab')[0], preventDefault() {} });
  const pageBefore = documentStub.querySelectorAll('.layout-page')[0];
  socket.close();                    // host drops the socket; the client backs off and reconnects
  await waitMs(450);
  results.push(['reconnect keeps page',
    `${visiblePage()} same=${documentStub.querySelectorAll('.layout-page')[0] === pageBefore}`,
    'expect layout:netflix same=true', '']);

  // 14. A reconnect with a changed layout rebuilds it - and still shows it instead of a blank screen.
  hostLayouts = JSON.parse(JSON.stringify(FIXTURE_LAYOUTS));
  hostLayouts[0].label = 'Netflix (edited)';
  socket.close();
  await waitMs(450);
  results.push(['rebuilt page shown', visiblePage(), 'expect layout:netflix',
    `cached=${JSON.parse(sandbox.localStorage.getItem('simpleremote.config')).layouts[0].label} tab=${sandbox.localStorage.getItem('simpleremote.tab')}`]);
  hostLayouts = FIXTURE_LAYOUTS;

  // The transport, driven directly with a controllable clock.
  const creds = { deviceId: 'd', token: 't', name: 'test' };
  let clock = 50000;
  const probe = new sandbox.RemoteLink();
  probe._clock = () => clock;
  probe.connect(creds);
  await waitTicks(); await waitTicks();

  // 15. After standby the socket still says OPEN but nothing comes back: an unanswered ping must
  //     end in a fresh socket, and the dead one's late onclose must not disturb it.
  const deadSocket = socket;
  probe._tick();                     // ping, never answered by the fake host
  clock += 5000;
  probe._tick();
  const replaced = socket !== deadSocket;
  if (deadSocket.onclose) deadSocket.onclose();
  await waitTicks(); await waitTicks();
  results.push(['dead socket replaced', `replaced=${replaced} state=${probe.state}`,
    'expect replaced=true state=open', '']);

  // 16. A healthy socket in a throttled background tab (one tick a minute) is not declared dead.
  const liveSocket = socket;
  probe._tick();
  const pong = new ArrayBuffer(5);
  new DataView(pong).setUint8(0, 0x81);
  socket.onmessage({ data: pong });
  clock += 60000;
  probe._tick();
  results.push(['quiet socket kept', `kept=${socket === liveSocket}`, 'expect kept=true', '']);

  // 17. Waking up mid-connect restarts the connect instead of waiting on a socket from before sleep.
  //     (Checked synchronously, before the fake socket gets a chance to open.)
  probe.close();
  const hung = new sandbox.RemoteLink();
  hung.connect(creds);
  const hungSocket = socket;
  hung.revive();
  results.push(['revive restarts connect', `restarted=${socket !== hungSocket} state=${hung.state}`,
    'expect restarted=true state=connecting', `old handlers detached=${hungSocket.onopen === null}`]);
  hung.close();

  // 18. Throttled auth is "try later", not "unpaired": credentials survive and a retry is queued.
  hostAuthResult = { t: 'authResult', ok: false, reason: 'Too many attempts', retry: true };
  const throttled = new sandbox.RemoteLink();
  let unpaired = 0;
  throttled.on('authFailed', () => unpaired++);
  throttled.connect(creds);
  await waitTicks(); await waitTicks();
  results.push(['throttled auth retries', `unpaired=${unpaired} retry=${Boolean(throttled._retryTimer)} creds=${Boolean(throttled._creds)}`,
    'expect unpaired=0 retry=true creds=true', '']);
  throttled.close();
  hostAuthResult = { t: 'authResult', ok: true };

  // 19a. Motion is stamped with the animation frame time, ahead of the motion it describes, so the
  //      host can replay it at the pace it was made rather than the pace Wi-Fi delivered it.
  const stamped = new sandbox.RemoteLink();
  stamped.connect(creds);
  await waitTicks(); await waitTicks();
  reset();
  stamped.moveBy(5, 3);
  stamped._frame(1234.5);
  const frameOps = decode(sent);
  results.push(['motion is timestamped',
    frameOps.map((e) => e.op).join(' ') + ` t=${frameOps[0] && frameOps[0].t}`,
    'expect time move t=1234.5', '']);

  // 19b. A click carries the motion before it, in that order - the cursor arrives, then clicks.
  reset();
  stamped.moveBy(7, 0);
  stamped.button(0, true);
  results.push(['motion precedes click', decode(sent).map((e) => e.op).join(' '), 'expect time move button', '']);
  stamped.close();

  // ---- smoothing pipeline (motion.js), measured on synthetic finger paths ----------

  const Motion = sandbox.Motion;

  // Deterministic noise, so a failure is reproducible rather than flaky.
  let seed = 12345;
  const noise = (amplitude) => {
    seed = (seed * 1103515245 + 12345) % 2147483648;
    return (seed / 2147483648 - 0.5) * 2 * amplitude;
  };

  /**
   * Drives a MotionPath + Resampler the way a phone does: the digitizer samples every sampleMs,
   * but samples are only delivered in batches just before each animation frame.
   * position(t) returns the true finger position or null while the finger is resting.
   */
  function simulate(options) {
    const o = Object.assign({ smoothing: 0.5, sampleMs: 8, frameMs: 16.7, deliveryLagMs: 3, jitter: 0 }, options);
    const motionPath = new Motion.MotionPath({ smoothing: o.smoothing });
    if (o.unfiltered) {
      // A cutoff far above the sample rate passes samples straight through: the pre-filter baseline.
      motionPath._fx.minCutoff = motionPath._fy.minCutoff = 1e9;
    }
    const estimator = new Motion.DelayEstimator();
    const sampler = new Motion.Resampler(motionPath, estimator);

    const frames = [];
    let nextSample = 0;
    for (let frame = 0; frame * o.frameMs <= o.durationMs; frame++) {
      const frameTime = frame * o.frameMs;
      while (nextSample <= frameTime - o.deliveryLagMs) {
        const p = o.position(nextSample);
        if (p) motionPath.add(p.x + noise(o.jitter), p.y + noise(o.jitter), nextSample);
        nextSample += o.sampleMs;
      }
      frames.push({ t: frameTime, delta: sampler.frame(frameTime), delay: estimator.delay });
    }
    return { frames, flush: sampler.flush(), path: motionPath, estimator };
  }

  const stats = (values) => {
    const mean = values.reduce((s, v) => s + v, 0) / values.length;
    const sd = Math.sqrt(values.reduce((s, v) => s + (v - mean) * (v - mean), 0) / values.length);
    return { mean, sd };
  };
  const wander = (frames) => frames.reduce((s, f) => s + (f.delta ? Math.hypot(f.delta.dx, f.delta.dy) : 0), 0);

  // 19. A resting finger with a pixel of digitizer noise: the raw path wanders, the smoothed one
  //     should barely move. This is the "cursor shivers while aiming" case.
  const still = () => ({ x: 100, y: 100 });
  const rawWander = wander(simulate({ unfiltered: true, durationMs: 1000, position: still, jitter: 1 }).frames);
  const smoothWander = wander(simulate({ smoothing: 0.5, durationMs: 1000, position: still, jitter: 1 }).frames);
  results.push(['resting finger jitter', `reduced=${smoothWander < rawWander * 0.15}`, 'expect reduced=true',
    `raw=${rawWander.toFixed(1)}px smoothed=${smoothWander.toFixed(1)}px`]);

  // 20. A steady 600 px/s swipe with noise: every frame after warm-up should carry a step, and the
  //     steps should be even. Uneven steps are what reads as "not smooth" at speed.
  const swipe = simulate({ durationMs: 800, position: (t) => ({ x: 0.6 * t, y: 0.2 * t }), jitter: 0.5 });
  const steady = swipe.frames.filter((f) => f.t > 150 && f.t < 750);
  const stalls = steady.filter((f) => !f.delta).length;
  const steps = stats(steady.filter((f) => f.delta).map((f) => Math.hypot(f.delta.dx, f.delta.dy)));
  results.push(['steady swipe cadence', `stalls=${stalls} even=${steps.sd / steps.mean < 0.12}`,
    'expect stalls=0 even=true', `step=${steps.mean.toFixed(2)}±${steps.sd.toFixed(2)}px delay=${swipe.estimator.delay.toFixed(1)}ms`]);

  // 21. Delivery that is late by a whole frame now and then must not cause stalls once learned.
  const lateDelivery = simulate({ durationMs: 1200, deliveryLagMs: 12, position: (t) => ({ x: 0.5 * t, y: 0 }) });
  const lateStalls = lateDelivery.frames.filter((f) => f.t > 400 && f.t < 1150 && !f.delta).length;
  results.push(['late delivery learned', `stalls=${lateStalls}`, 'expect stalls=0',
    `delay=${lateDelivery.estimator.delay.toFixed(1)}ms`]);

  // 22. Nothing is lost: every frame's step plus the release flush add up to where the finger stopped.
  const easeOut = (t) => { const k = Math.min(t / 400, 1); return { x: 300 * (1 - (1 - k) * (1 - k)), y: 0 }; };
  const landed = simulate({ durationMs: 700, position: easeOut });
  const travelledX = landed.frames.reduce((s, f) => s + (f.delta ? f.delta.dx : 0), 0) + (landed.flush ? landed.flush.dx : 0);
  results.push(['travel conserved', `within=${Math.abs(travelledX - 300) < 1.5}`, 'expect within=true',
    `travelled=${travelledX.toFixed(2)}px of 300`]);

  // 23. A pause mid-gesture (pointermove stops firing) is not mistaken for slow delivery. If it were,
  //     every movement after it would lag by the length of the pause.
  const pausing = simulate({
    durationMs: 900,
    position: (t) => (t > 300 && t < 500 ? null : { x: 0.4 * t, y: 0 }),
  });
  const delayBefore = pausing.frames.find((f) => f.t >= 290).delay;
  const delayAfter = pausing.frames.find((f) => f.t >= 560).delay;
  results.push(['pause not learned', `inflated=${delayAfter > delayBefore + 3}`, 'expect inflated=false',
    `before=${delayBefore.toFixed(1)}ms after=${delayAfter.toFixed(1)}ms`]);

  // 24. Latency budget at the default: how far behind the true finger the smoothed read point is,
  //     mid-swipe at a brisk 800 px/s. Smoothing must not buy steadiness with obvious lag.
  const brisk = simulate({ durationMs: 600, position: (t) => ({ x: 0.8 * t, y: 0 }) });
  const readX = brisk.frames.filter((f) => f.t <= 500).reduce((s, f) => s + (f.delta ? f.delta.dx : 0), 0);
  const lagMs = (0.8 * 500 - readX) / 0.8;
  results.push(['lag at 800px/s', `under40ms=${lagMs < 40}`, 'expect under40ms=true', `lag=${lagMs.toFixed(1)}ms`]);

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
