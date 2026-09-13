/* Simple Remote - client application logic. */

(function () {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const STORE_KEY = 'simpleremote.creds';

  const link = new window.RemoteLink();

  /** Pointer feel, replaced by the host config message once connected. */
  let P = {
    sensitivity: 0.55, acceleration: 0.4, maxSpeed: 3.0, scrollSpeed: 1.0, naturalScroll: true,
    screenWidth: 0, screenHeight: 0,
  };

  /**
   * Per-device speed multiplier from the on-screen slider.
   *
   * Kept on the phone rather than in the host config because feel is subjective and differs by
   * device - a tablet with a large pad wants a lower value than a small phone driving the same PC.
   */
  let speedScale = 1;
  try {
    const saved = parseFloat(localStorage.getItem('simpleremote.speed'));
    if (saved >= 0.25 && saved <= 3) speedScale = saved;
  } catch (err) { /* private mode */ }

  // ---------------------------------------------------------------- pairing / boot

  function loadCreds() {
    try {
      const raw = localStorage.getItem(STORE_KEY);
      return raw ? JSON.parse(raw) : null;
    } catch (err) {
      return null;
    }
  }

  function saveCreds(creds) {
    try { localStorage.setItem(STORE_KEY, JSON.stringify(creds)); } catch (err) { /* private mode */ }
  }

  function clearCreds() {
    try { localStorage.removeItem(STORE_KEY); } catch (err) { /* private mode */ }
  }

  function showGate(title, text, retry) {
    $('gateTitle').textContent = title;
    $('gateText').textContent = text;
    $('gateRetry').hidden = !retry;
    $('gate').hidden = false;
    $('app').hidden = true;
  }

  function hideGate() {
    $('gate').hidden = true;
    $('app').hidden = false;
  }

  async function redeem(token) {
    const response = await fetch('api/pair', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ pairingToken: token }),
    });

    if (!response.ok) return null;

    const body = await response.json();
    return { deviceId: body.deviceId, token: body.token, name: navigator.platform || 'Phone', host: body.hostName };
  }

  async function boot() {
    let creds = loadCreds();

    // The pairing token rides in the fragment, so it never leaves the browser. Redeem it whenever
    // one is present, even if we already hold credentials - the user only rescans deliberately,
    // typically because the old device was revoked.
    const match = location.hash.match(/[#&]p=([A-Za-z0-9_-]+)/);
    if (match) {
      history.replaceState(null, '', location.pathname);
      showGate('Pairing…', 'Setting this device up.');
      try {
        const paired = await redeem(match[1]);
        if (paired) {
          creds = paired;
          saveCreds(creds);
        } else if (!creds) {
          showGate('Pairing failed', 'That code has expired or was already used. Open the pairing window on your PC and scan a fresh code.');
          return;
        }
      } catch (err) {
        showGate('Cannot reach the PC', 'The code scanned, but the PC did not answer. Check that both devices are on the same Wi-Fi network.', true);
        return;
      }
    }

    if (!creds) {
      showGate('Not paired yet', 'Open Simple Remote on your PC, choose "Pair a device", and scan the QR code with your camera.');
      return;
    }

    if (creds.host) $('host').textContent = creds.host;

    hideGate();
    link.connect(creds);
    requestWakeLock();
  }

  $('gateRetry').addEventListener('click', () => location.reload());

  // ---------------------------------------------------------------- connection state

  link.on('state', (state) => {
    $('conn').dataset.state = state;
    if (state !== 'open') {
      $('rtt').textContent = '--';
      $('rtt').removeAttribute('data-quality');
    }
  });

  link.on('authFailed', () => {
    clearCreds();
    showGate('Pairing no longer valid', 'This device was unpaired. Open the pairing window on your PC and scan a new QR code.');
  });

  link.on('rtt', (rtt) => {
    const ms = Math.round(rtt);
    $('rtt').textContent = ms + ' ms';
    $('rtt').dataset.quality = ms < 30 ? 'good' : ms < 80 ? 'fair' : 'poor';
  });

  link.on('config', (message) => {
    if (message.pointer) P = message.pointer;
    if (message.hostName) $('host').textContent = message.hostName;
    renderShortcuts(message.shortcuts || []);
  });

  link.on('toast', (message) => toast(message.s, message.kind));

  let toastTimer = null;
  function toast(text, kind) {
    if (!text) return;
    const element = $('toast');
    element.textContent = text;
    element.dataset.kind = kind || 'info';
    element.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { element.hidden = true; }, 2200);
  }

  // ---------------------------------------------------------------- tabs

  for (const tab of document.querySelectorAll('.tab')) {
    tab.addEventListener('click', () => {
      for (const other of document.querySelectorAll('.tab')) other.classList.toggle('active', other === tab);
      for (const page of document.querySelectorAll('.page')) page.hidden = page.dataset.page !== tab.dataset.tab;
    });
  }

  // ---------------------------------------------------------------- touchpad

  const pad = $('pad');

  /** Live pointers on the pad, keyed by pointerId. */
  const pointers = new Map();

  let maxPointers = 0;
  let gestureStart = 0;
  let travelled = 0;
  let dragLocked = false;
  let lastTapEnd = 0;
  let lastTapX = 0;
  let lastTapY = 0;
  let scrollVelocity = 0;
  let momentumHandle = null;

  const TAP_MS = 250;
  const TAP_SLOP = 12;
  const DOUBLE_TAP_MS = 300;
  // Wheel units per CSS pixel of finger travel. One notch is 120 units, so 4 means a 30px drag
  // is one notch and a full pad swipe is roughly a screenful - phone users expect content to keep
  // up with the finger, and the 1:1 mapping a lower value gives reads as sluggish.
  const WHEEL_PER_PX = 4;

  function padPoint(event) {
    return { x: event.clientX, y: event.clientY, t: event.timeStamp };
  }

  pad.addEventListener('pointerdown', (event) => {
    event.preventDefault();

    // Capture keeps a drag alive if the finger slides past the pad edge. It throws for a pointer
    // the browser does not consider active, which is harmless and must not abort the gesture.
    try { pad.setPointerCapture(event.pointerId); } catch (err) { /* not capturable */ }

    pad.classList.add('active', 'used');

    stopMomentum();

    const point = padPoint(event);
    pointers.set(event.pointerId, point);

    if (pointers.size === 1) {
      maxPointers = 1;
      gestureStart = event.timeStamp;
      travelled = 0;

      // Tap-and-a-half: a tap immediately followed by a press starts a drag, the same gesture a
      // laptop trackpad uses. Without it, dragging a window from the couch is impossible.
      const sinceTap = event.timeStamp - lastTapEnd;
      if (sinceTap < DOUBLE_TAP_MS && Math.hypot(point.x - lastTapX, point.y - lastTapY) < 40) {
        dragLocked = true;
        pad.classList.add('dragging');
        link.button(0, true);
      }
    }

    maxPointers = Math.max(maxPointers, pointers.size);
  });

  pad.addEventListener('pointermove', (event) => {
    const previous = pointers.get(event.pointerId);
    if (!previous) return;

    event.preventDefault();

    const point = padPoint(event);
    const dx = point.x - previous.x;
    const dy = point.y - previous.y;
    const dt = Math.max(point.t - previous.t, 1);
    pointers.set(event.pointerId, point);

    travelled += Math.hypot(dx, dy);

    if (pointers.size >= 2) {
      // Two fingers scroll. Only the primary pointer drives it - averaging every contact makes a
      // slight pinch read as scroll jitter.
      if (event.isPrimary) {
        const factor = WHEEL_PER_PX * P.scrollSpeed * speedScale;
        const units = dy * factor * (P.naturalScroll ? 1 : -1);
        const unitsX = dx * factor * (P.naturalScroll ? -1 : 1);
        link.scrollBy(unitsX, units);
        scrollVelocity = units / dt;
      }
      return;
    }

    if (maxPointers > 1) return; // fingers lifted mid-scroll: do not jerk the cursor

    const [ax, ay] = accelerate(dx, dy, dt);
    link.moveBy(ax, ay);
  });

  function endPointer(event) {
    if (!pointers.has(event.pointerId)) return;
    pointers.delete(event.pointerId);

    if (pointers.size > 0) return;

    pad.classList.remove('active');
    const duration = event.timeStamp - gestureStart;

    if (dragLocked) {
      dragLocked = false;
      pad.classList.remove('dragging');
      link.button(0, false);
    } else if (duration < TAP_MS && travelled < TAP_SLOP) {
      // A tap. Which button depends on how many fingers were down at the peak of the gesture.
      if (maxPointers === 1) {
        link.button(0, true);
        link.button(0, false);
        lastTapEnd = event.timeStamp;
        lastTapX = event.clientX;
        lastTapY = event.clientY;
      } else if (maxPointers === 2) {
        link.button(1, true);
        link.button(1, false);
      } else {
        link.button(2, true);
        link.button(2, false);
      }
    } else if (maxPointers >= 2) {
      startMomentum();
    }

    maxPointers = 0;
    travelled = 0;
  }

  pad.addEventListener('pointerup', endPointer);
  pad.addEventListener('pointercancel', endPointer);

  // Belt and braces: iOS still emits these on some gestures and they would scroll the page.
  pad.addEventListener('touchstart', (e) => e.preventDefault(), { passive: false });
  pad.addEventListener('touchmove', (e) => e.preventDefault(), { passive: false });

  /**
   * Ratio of screen distance to pad distance.
   *
   * Compared on the diagonal rather than per axis so the gain stays a single scalar: separate x
   * and y factors would skew diagonal movement, and the cursor must always travel in the same
   * direction as the finger. Recomputed on demand because the pad resizes with the viewport.
   */
  function screenFit() {
    const r = pad.getBoundingClientRect();
    if (!P.screenWidth || !P.screenHeight || r.width < 1 || r.height < 1) return 3;

    const screenDiagonal = Math.hypot(P.screenWidth, P.screenHeight);
    const padDiagonal = Math.hypot(r.width, r.height);
    return screenDiagonal / padDiagonal;
  }

  /**
   * Pointer acceleration.
   *
   * Deltas are in CSS pixels, which are already device independent, so there is no devicePixelRatio
   * term here - dividing by it would make high-DPI phones inexplicably slower.
   *
   * The base gain is screen-relative (see screenFit) so that the same sensitivity value feels the
   * same whether the PC has a laptop panel or a 4K desktop.
   */
  function accelerate(dx, dy, dt) {
    const distance = Math.hypot(dx, dy);
    if (distance === 0) return [0, 0];

    const speed = distance / dt; // CSS px per ms
    const gain = screenFit() * P.sensitivity * speedScale
      * (1 + P.acceleration * Math.min(speed, P.maxSpeed));

    return [dx * gain, dy * gain];
  }

  function startMomentum() {
    // Below this the flick was really a slow drag, and coasting would feel like drift.
    if (Math.abs(scrollVelocity) < 0.4) return;

    let velocity = scrollVelocity * 16; // per frame rather than per ms
    const step = () => {
      // 0.96 coasts for roughly a second, which is what makes a flick feel like it carries.
      velocity *= 0.96;
      if (Math.abs(velocity) < 2) { momentumHandle = null; return; }
      link.scrollBy(0, velocity);
      momentumHandle = requestAnimationFrame(step);
    };
    momentumHandle = requestAnimationFrame(step);
  }

  function stopMomentum() {
    if (momentumHandle) cancelAnimationFrame(momentumHandle);
    momentumHandle = null;
    scrollVelocity = 0;
  }

  // Speed slider. Applies live to both cursor and scroll, since a user who finds one sluggish
  // almost always finds the other sluggish too.
  const speedInput = $('speed');
  speedInput.value = String(Math.round(speedScale * 100));
  $('speedval').textContent = Math.round(speedScale * 100) + '%';

  speedInput.addEventListener('input', () => {
    speedScale = Number(speedInput.value) / 100;
    $('speedval').textContent = Math.round(speedScale * 100) + '%';
    try { localStorage.setItem('simpleremote.speed', String(speedScale)); } catch (err) { /* private mode */ }
  });

  for (const button of document.querySelectorAll('.mb')) {
    const code = Number(button.dataset.btn);
    button.addEventListener('pointerdown', (e) => { e.preventDefault(); link.button(code, true); });
    button.addEventListener('pointerup', (e) => { e.preventDefault(); link.button(code, false); });
    button.addEventListener('pointercancel', () => link.button(code, false));
  }

  // ---------------------------------------------------------------- keyboard

  const typer = $('typer');
  const latched = new Set();
  let composing = false;

  for (const button of document.querySelectorAll('.mod')) {
    button.addEventListener('click', () => {
      const vk = Number(button.dataset.mod);
      if (latched.has(vk)) latched.delete(vk); else latched.add(vk);
      button.classList.toggle('latched', latched.has(vk));
    });
  }

  function clearLatched() {
    latched.clear();
    for (const button of document.querySelectorAll('.mod')) button.classList.remove('latched');
  }

  /** Presses a key with any latched modifiers wrapped around it, then unlatches them. */
  function pressWithMods(vk) {
    const mods = [...latched];
    for (const mod of mods) link.key(mod, true);
    link.tap(vk);
    for (const mod of mods.reverse()) link.key(mod, false);
    if (mods.length) clearLatched();
  }

  function bindKey(button) {
    const vk = Number(button.dataset.vk);
    button.addEventListener('click', () => pressWithMods(vk));
  }

  for (const button of document.querySelectorAll('.key[data-vk]')) bindKey(button);

  // F1-F12, built here rather than written out twelve times in the HTML.
  const fkeys = $('fkeys');
  for (let i = 1; i <= 12; i++) {
    const button = document.createElement('button');
    button.className = 'key';
    button.dataset.vk = String(0x6F + i);
    button.textContent = 'F' + i;
    fkeys.appendChild(button);
    bindKey(button);
  }

  function sendText(text) {
    if (!text) return;
    link.sendJson({ t: 'text', s: text });
  }

  typer.addEventListener('compositionstart', () => { composing = true; });
  typer.addEventListener('compositionend', (event) => {
    composing = false;
    if (event.data) sendText(event.data);
  });

  typer.addEventListener('beforeinput', (event) => {
    // Composition (IME, and some autocorrect paths) is only sent once, at compositionend.
    if (composing || event.inputType === 'insertCompositionText') return;

    switch (event.inputType) {
      case 'insertText':
        if (event.data == null) return;

        // With a modifier latched, a typed letter is a shortcut, not text: Ctrl then C is Ctrl+C.
        if (latched.size && event.data.length === 1) {
          const upper = event.data.toUpperCase();
          const code = upper.charCodeAt(0);
          if ((code >= 65 && code <= 90) || (code >= 48 && code <= 57)) {
            event.preventDefault();
            pressWithMods(code);
            return;
          }
        }
        sendText(event.data);
        break;

      case 'insertFromPaste': {
        const pasted = event.dataTransfer ? event.dataTransfer.getData('text') : '';
        if (pasted) { event.preventDefault(); sendText(pasted); }
        break;
      }

      case 'insertLineBreak':
      case 'insertParagraph':
        event.preventDefault();
        pressWithMods(13);
        typer.textContent = '';
        break;

      case 'deleteContentBackward':
        link.tap(8);
        break;

      case 'deleteContentForward':
        link.tap(46);
        break;

      default:
        break;
    }
  });

  // The field mirrors what was typed as feedback, but it is not a document - keep it short.
  typer.addEventListener('input', () => {
    if (typer.textContent.length > 300) typer.textContent = '';
  });

  // ---------------------------------------------------------------- clipboard

  $('clipSend').addEventListener('click', () => {
    const text = $('clipText').value;
    if (!text) return;
    link.sendJson({ t: 'clip', s: text });
    toast('Sent to PC');
  });

  $('clipPull').addEventListener('click', () => link.sendJson({ t: 'clipPull' }));

  link.on('clipboard', (message) => {
    if (typeof message.s === 'string') {
      $('clipText').value = message.s;
      toast('Clipboard from PC');
    }
  });

  // navigator.clipboard.readText needs a secure context, which plain HTTP on the LAN is not. Say
  // so plainly rather than shipping a button that fails silently.
  $('clipNote').textContent = window.isSecureContext
    ? 'Tip: paste into the box above, then send.'
    : 'Reading the phone clipboard automatically needs HTTPS, so paste into the box manually.';

  // ---------------------------------------------------------------- media

  let media = { positionMs: 0, durationMs: 0, status: 'unknown', at: 0 };
  let seeking = false;

  link.on('mediaState', (message) => {
    media = {
      positionMs: message.positionMs || 0,
      durationMs: message.durationMs || 0,
      status: message.status,
      at: performance.now(),
    };

    $('mtitle').textContent = message.active && message.title ? message.title : 'Nothing playing';
    $('martist').textContent = message.artist || '';
    $('mapp').textContent = message.app || '';

    $('playpause').textContent = message.status === 'playing' ? '⏸' : '▶';
    $('playpause').disabled = !message.active;
    $('prev').disabled = !message.canPrevious;
    $('next').disabled = !message.canNext;
    $('seek').disabled = !message.canSeek || !message.durationMs;

    const art = $('art');
    if (message.artUrl) {
      if (art.dataset.url !== message.artUrl) {
        art.dataset.url = message.artUrl;
        art.innerHTML = '';
        const image = new Image();
        image.src = message.artUrl;
        image.alt = '';
        art.appendChild(image);
      }
    } else if (art.dataset.url) {
      delete art.dataset.url;
      art.innerHTML = '<span>♫</span>';
    }

    paintProgress();
  });

  link.on('volumeState', (message) => {
    const percent = Math.round((message.level || 0) * 100);
    if (document.activeElement !== $('vol')) $('vol').value = String(percent);
    $('volval').textContent = percent + '%';
    $('mute').textContent = message.muted ? '🔇' : '🔊';
    $('voldev').textContent = message.device || '';
  });

  /**
   * Interpolates position locally between the once-a-second updates from the host, so the bar
   * moves smoothly without the host having to stream position at frame rate.
   */
  function paintProgress() {
    if (seeking) return;

    let position = media.positionMs;
    if (media.status === 'playing') position += performance.now() - media.at;
    if (media.durationMs > 0) position = Math.min(position, media.durationMs);

    $('tpos').textContent = formatTime(position);
    $('tdur').textContent = formatTime(media.durationMs);

    if (media.durationMs > 0) {
      $('seek').value = String(Math.round((position / media.durationMs) * 1000));
    } else {
      $('seek').value = '0';
    }
  }

  setInterval(() => { if (!$('gate').hidden) return; paintProgress(); }, 250);

  function formatTime(ms) {
    if (!ms || ms < 0) return '0:00';
    const total = Math.floor(ms / 1000);
    const minutes = Math.floor(total / 60);
    const seconds = total % 60;
    return minutes + ':' + String(seconds).padStart(2, '0');
  }

  $('playpause').addEventListener('click', () => link.sendJson({ t: 'media', cmd: 'playpause' }));
  $('next').addEventListener('click', () => link.sendJson({ t: 'media', cmd: 'next' }));
  $('prev').addEventListener('click', () => link.sendJson({ t: 'media', cmd: 'prev' }));

  $('seek').addEventListener('pointerdown', () => { seeking = true; });
  $('seek').addEventListener('change', () => {
    seeking = false;
    if (media.durationMs > 0) {
      const target = Math.round((Number($('seek').value) / 1000) * media.durationMs);
      link.sendJson({ t: 'media', cmd: 'seek', pos: target });
    }
  });

  // Volume moves continuously; coalesce to one message per frame so a slider drag does not spam.
  let volumePending = null;
  $('vol').addEventListener('input', () => {
    const level = Number($('vol').value) / 100;
    $('volval').textContent = Math.round(level * 100) + '%';
    if (volumePending !== null) { volumePending = level; return; }
    volumePending = level;
    requestAnimationFrame(() => {
      link.sendJson({ t: 'volume', level: volumePending });
      volumePending = null;
    });
  });

  let muted = false;
  $('mute').addEventListener('click', () => {
    muted = !muted;
    link.sendJson({ t: 'volume', mute: muted });
  });

  // ---------------------------------------------------------------- shortcuts

  function renderShortcuts(list) {
    const container = $('shortcuts');
    container.innerHTML = '';

    for (const shortcut of list) {
      const button = document.createElement('button');
      button.className = 'sc';
      button.innerHTML = '<span></span>';
      button.firstChild.textContent = shortcut.icon || '■';
      button.appendChild(document.createTextNode(shortcut.label));
      button.addEventListener('click', () => link.sendJson({ t: 'shortcut', id: shortcut.id }));
      container.appendChild(button);
    }

    if (!list.length) {
      const empty = document.createElement('p');
      empty.className = 'note center';
      empty.textContent = 'No shortcuts configured.';
      container.appendChild(empty);
    }
  }

  // ---------------------------------------------------------------- housekeeping

  /**
   * Keeps the screen awake while the remote is open. Wake Lock needs a secure context, so on plain
   * HTTP this quietly does nothing and the phone dims as usual - one of the reasons to move to
   * HTTPS later.
   */
  let wakeLock = null;
  async function requestWakeLock() {
    if (!('wakeLock' in navigator)) return;
    try {
      wakeLock = await navigator.wakeLock.request('screen');
    } catch (err) {
      wakeLock = null;
    }
  }

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState !== 'visible') {
      // Backgrounding stops requestAnimationFrame, stranding whatever motion was mid-gesture.
      // Delivering it on return would jump the cursor, so discard it and end any drag cleanly.
      link.dropPendingMotion();
      stopMomentum();
      pointers.clear();
      if (dragLocked) {
        dragLocked = false;
        pad.classList.remove('dragging');
        link.button(0, false);
      }
      return;
    }

    // Coming back from the lock screen: the socket is usually dead and the wake lock released.
    requestWakeLock();
    if (link.state === 'closed') link.connect();
  });

  // The page is a control surface, not a document: suppress the browser gestures that would
  // otherwise fire on double-taps and long-presses.
  document.addEventListener('gesturestart', (e) => e.preventDefault());
  document.addEventListener('contextmenu', (e) => e.preventDefault());

  boot();
})();
