/* Simple Remote - client application logic. */

(function () {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const STORE_KEY = 'simpleremote.creds';

  /**
   * Named icons, drawn as inline SVG.
   *
   * Unicode glyphs looked like a free icon set but are not: whether U+26F6 or U+23ED renders at all
   * depends on the fonts on the phone, many Android builds show an empty box for them, and others
   * swap in a colour emoji that ignores the button's colour. SVG renders the same everywhere.
   *
   * Paths are 24x24 and drawn with currentColor, so they pick up the button's text colour.
   */
  const ICONS = {
    play: 'M8 5v14l11-7z',
    pause: 'M6 5h4v14H6zM14 5h4v14h-4z',
    prev: 'M6 6h2v12H6zM9.5 12 18 18V6z',
    next: 'M16 6h2v12h-2zM6 18l8.5-6L6 6z',
    rewind: 'M12 5V1L7 6l5 5V7a6 6 0 1 1-6 6H4a8 8 0 1 0 8-8z',
    forward: 'M12 5V1l5 5-5 5V7a6 6 0 1 0 6 6h2a8 8 0 1 1-8-8z',
    skip: 'M3 6l7.5 6L3 18zM10.5 6 18 12l-7.5 6zM18.5 6H21v12h-2.5z',
    back: 'M20 11H7.8l5.6-5.6L12 4l-8 8 8 8 1.4-1.4L7.8 13H20z',
    fullscreen: 'M4 4h6v2H6v4H4zM14 4h6v6h-2V6h-4zM4 14h2v4h4v2H4zM18 14h2v6h-6v-2h4z',
    film: 'M3 4h18v16H3zM5 6v2h2V6zm0 5v2h2v-2zm0 5v2h2v-2zM17 6v2h2V6zm0 5v2h2v-2zm0 5v2h2v-2zM9 6v12h6V6z',
    grid: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
  };

  /**
   * Shows an icon in an element: a known name becomes SVG, anything else (an emoji, a letter) is
   * shown as text. Config values never reach innerHTML - only the fixed paths above do.
   */
  function setIcon(element, icon) {
    if (Object.prototype.hasOwnProperty.call(ICONS, icon)) {
      element.innerHTML = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true" fill="currentColor" fill-rule="evenodd"><path d="' + ICONS[icon] + '"/></svg>';
      element.dataset.icon = icon;
    } else {
      element.textContent = icon || '';
      element.dataset.icon = '';
    }
  }

  const link = new window.RemoteLink();

  /** Pointer feel, replaced by the host config message once connected. */
  let P = {
    sensitivity: 0.55, acceleration: 0.4, maxSpeed: 3.0, scrollSpeed: 1.0, naturalScroll: true,
    tapHoldMs: 200, screenWidth: 0, screenHeight: 0,
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
    // Merged rather than replaced: a field the host omits would otherwise become undefined, and
    // an undefined timeout or gain silently breaks the gesture it belongs to.
    if (message.pointer) P = Object.assign({}, P, message.pointer);
    if (message.hostName) $('host').textContent = message.hostName;
    renderShortcuts(message.shortcuts || []);
    renderLayouts(message.layouts || []);
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

  // Delegated, not bound per tab: layout pages add their own tabs at runtime, and binding once
  // at startup would leave those tabs dead.
  $('tabs').addEventListener('click', (event) => {
    const tab = event.target && event.target.closest ? event.target.closest('.tab') : null;
    if (tab) selectTab(tab.dataset.tab);
  });

  function selectTab(id) {
    for (const tab of document.querySelectorAll('.tab')) {
      tab.classList.toggle('active', tab.dataset.tab === id);
    }
    for (const page of document.querySelectorAll('.page')) {
      page.hidden = page.dataset.page !== id;
    }

    // Opening the Keyboard tab almost always means "I want to type", so raise the phone keyboard
    // straight away. This has to run synchronously inside the tap handler - mobile browsers only
    // show the keyboard for a focus() that comes from a user gesture - and after the page is
    // unhidden, because a hidden element cannot take focus.
    if (id === 'keys') focusTyper();
  }

  function focusTyper() {
    const typer = $('typer');
    if (!typer || typeof typer.focus !== 'function') return;

    typer.focus();

    // Put the caret at the end, so typing continues after anything already in the field instead
    // of landing wherever the browser decided to place it.
    if (typeof window.getSelection === 'function' && typeof document.createRange === 'function') {
      const range = document.createRange();
      range.selectNodeContents(typer);
      range.collapse(false);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
    }
  }

  // ---------------------------------------------------------------- touchpad

  // The gesture engine drives whichever trackpad the finger is currently on. Only one can be
  // visible at a time, so the gesture state is shared rather than duplicated per element.
  let pad = $('pad');

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

  /**
   * Interpolated-emission state.
   *
   * Motion is no longer sent from inside the pointermove handler. Instead the finger path is
   * resampled once per animation frame at a fixed point in time, and the delta between successive
   * resample points is what gets sent. For a constant finger speed that produces equal deltas at
   * equal intervals, whereas emitting whatever arrived in each frame produces uneven steps - one
   * frame carries one touch sample, the next carries three.
   */
  let emitting = false;
  let emitTime = 0;
  let emitX = 0;
  let emitY = 0;

  /** null | 'move' | 'scroll' - which consumer the interpolated delta feeds. */
  let gestureMode = null;

  const TAP_MS = 250;
  const TAP_SLOP = 12;

  /**
   * Tap-and-a-half state.
   *
   * A tap does NOT send a complete click. It sends the button down and holds it for a moment. If a
   * second press arrives while it is still held, that press adopts the already-down button and
   * becomes a drag.
   *
   * Releasing immediately is what broke this gesture: the host then saw down/up followed by
   * another down at the same spot inside Windows' double-click time, which is by definition a
   * double click, so applications ran their double-click behaviour instead of dragging. Holding
   * the button means the host sees a single down, the movement, and one up.
   *
   * The cost is that a plain tap's release is deferred by the hold, so the click activates that
   * much later. The press itself is still immediate, which is what gives the visual feedback.
   */
  let heldFromTap = false;
  let releaseTimer = null;
  let chained = false;
  // Wheel units per CSS pixel of finger travel. One notch is 120 units, so 4 means a 30px drag
  // is one notch and a full pad swipe is roughly a screenful - phone users expect content to keep
  // up with the finger, and the 1:1 mapping a lower value gives reads as sluggish.
  const WHEEL_PER_PX = 4;

  /**
   * Timestamped sample history, used to estimate finger velocity.
   *
   * Velocity must NOT be taken from the gap between two consecutive events. Browsers deliver
   * pointermove at irregular intervals - 1ms here, 20ms there, for identical physical motion -
   * so distance/dt over one event pair swings by an order of magnitude sample to sample. Feeding
   * that into an acceleration curve is precisely what makes the cursor jitter.
   *
   * Instead every sample is recorded with its own timestamp and velocity is measured across a
   * fixed time window, which is stable regardless of how the events happened to be delivered.
   */
  const VELOCITY_WINDOW_MS = 45;

  /** Retained longer than the velocity window, because interpolation reads into the recent past. */
  const HISTORY_MS = 200;

  let samples = [];

  /** Smoothed gain, so the curve cannot step discontinuously between frames. */
  let gainSmoothed = 0;

  function resetSampling() {
    samples = [];
    gainSmoothed = 0;
    emitting = false;
  }

  function pushSample(x, y, t) {
    samples.push({ x: x, y: y, t: t });
    while (samples.length > 2 && t - samples[0].t > HISTORY_MS) samples.shift();
  }

  /** Finger velocity in CSS px per ms, measured across the velocity window. */
  function windowedVelocity() {
    if (samples.length < 2) return { x: 0, y: 0, speed: 0 };

    const last = samples[samples.length - 1];

    // Oldest sample still inside the window, rather than the oldest retained.
    let i = samples.length - 1;
    while (i > 0 && last.t - samples[i - 1].t <= VELOCITY_WINDOW_MS) i--;

    const first = samples[i];
    const dt = last.t - first.t;
    if (dt <= 0) return { x: 0, y: 0, speed: 0 };

    const vx = (last.x - first.x) / dt;
    const vy = (last.y - first.y) / dt;
    return { x: vx, y: vy, speed: Math.hypot(vx, vy) };
  }

  /**
   * The finger position at an arbitrary time, linearly interpolated between the two samples that
   * bracket it.
   *
   * Deliberately does not extrapolate past the newest sample: guessing where the finger went next
   * overshoots and then corrects, which looks exactly like the jitter this is meant to remove.
   * Clamping instead means a stalled finger simply stops.
   */
  function pathAt(t) {
    if (samples.length === 0) return null;

    const first = samples[0];
    const last = samples[samples.length - 1];
    if (t <= first.t) return { x: first.x, y: first.y };
    if (t >= last.t) return { x: last.x, y: last.y };

    for (let i = samples.length - 1; i > 0; i--) {
      const a = samples[i - 1];
      const b = samples[i];
      if (t >= a.t && t <= b.t) {
        const span = b.t - a.t;
        const f = span > 0 ? (t - a.t) / span : 0;
        return { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f };
      }
    }

    return { x: last.x, y: last.y };
  }

  /**
   * How far behind the newest sample to interpolate.
   *
   * Interpolation needs a sample on both sides of the target time, so the target has to lag the
   * input stream by a little over one sample interval. That lag is the entire cost of this
   * technique, so it is measured rather than guessed: a 120Hz digitizer gets ~12ms, a 60Hz one
   * ~25ms, instead of everyone paying the worst case.
   */
  function interpolationDelay() {
    if (samples.length < 3) return 16;

    const span = samples[samples.length - 1].t - samples[0].t;
    const mean = span / (samples.length - 1);
    return Math.min(Math.max(mean * 1.5, 8), 28);
  }

  /**
   * The individual timestamped samples behind one pointermove.
   *
   * The browser coalesces several real touch samples into one event for delivery; asking for them
   * back gives the true high-rate stream with per-sample timestamps, which both sharpens the
   * velocity estimate and avoids losing the shape of a fast gesture. Synthetic events return an
   * empty list, hence the fallback.
   */
  function coalescedSamples(event) {
    if (typeof event.getCoalescedEvents === 'function') {
      const list = event.getCoalescedEvents();
      if (list && list.length) return list;
    }
    return [event];
  }

  function padPoint(event) {
    return { x: event.clientX, y: event.clientY, t: event.timeStamp };
  }

  function attachTrackpad(element) {
    element.addEventListener('pointerdown', onPadDown);
    element.addEventListener('pointermove', onPadMove);
    element.addEventListener('pointerup', endPointer);
    element.addEventListener('pointercancel', endPointer);

    // Belt and braces: iOS still emits these on some gestures and they would scroll the page.
    element.addEventListener('touchstart', (e) => e.preventDefault(), { passive: false });
    element.addEventListener('touchmove', (e) => e.preventDefault(), { passive: false });

    const strip = document.createElement('div');
    strip.className = 'scroll-strip';
    element.appendChild(strip);
    attachScrollStrip(strip, element);
    return strip;
  }

  /**
   * One-finger scroll strip along the right edge of a trackpad.
   *
   * Two-finger scroll needs two hands on a phone held in one. Dragging a single finger - usually the
   * thumb - up and down this strip scrolls instead, feeding the same interpolated path and momentum
   * as two-finger scroll so the two feel identical.
   *
   * The strip lives inside the trackpad, so its events would otherwise bubble into the trackpad's
   * gesture handlers and read as cursor movement or a tap. Every handler stops propagation.
   */
  let stripPointer = null;

  function attachScrollStrip(strip, owner) {
    strip.addEventListener('pointerdown', (event) => {
      event.preventDefault();
      if (event.stopPropagation) event.stopPropagation();

      // A finger already on the trackpad owns the gesture; do not start a second one under it.
      if (pointers.size > 0 || stripPointer !== null) return;

      try { strip.setPointerCapture(event.pointerId); } catch (err) { /* not capturable */ }

      pad = owner;
      stripPointer = event.pointerId;
      stopMomentum();
      resetSampling();

      // Only the vertical axis is recorded: a thumb never travels a perfectly straight line, and
      // letting that drift through would produce unintended horizontal scrolling.
      pushSample(0, event.clientY, event.timeStamp);
      gestureMode = 'scroll';
      strip.classList.add('active');
    });

    strip.addEventListener('pointermove', (event) => {
      if (event.stopPropagation) event.stopPropagation();
      if (event.pointerId !== stripPointer) return;
      event.preventDefault();

      for (const sample of coalescedSamples(event)) {
        pushSample(0, sample.clientY, sample.timeStamp);
      }
    });

    const end = (event) => {
      if (event.stopPropagation) event.stopPropagation();
      if (event.pointerId !== stripPointer) return;

      flushRemainingMotion();
      startMomentum();

      stripPointer = null;
      gestureMode = null;
      resetSampling();
      strip.classList.remove('active');
    };

    strip.addEventListener('pointerup', end);
    strip.addEventListener('pointercancel', end);
  }

  function onPadDown(event) {
    // Scrolling on the strip owns the gesture until it ends.
    if (stripPointer !== null) return;

    pad = event.currentTarget || pad;

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

      // A new gesture must not inherit the previous one's velocity, or the first movement is
      // accelerated by however fast the last flick happened to be.
      resetSampling();
      pushSample(point.x, point.y, point.t);

      // Tap-and-a-half: this press lands while the previous tap still has the button held, so it
      // adopts that button rather than pressing a new one. No second down reaches the host, so
      // nothing can read as a double click.
      const near = Math.hypot(point.x - lastTapX, point.y - lastTapY) < 40;
      if (heldFromTap && near) {
        cancelPendingRelease();
        chained = true;
      }
    }

    maxPointers = Math.max(maxPointers, pointers.size);
  }

  /** Holds the left button down after a tap, pending either a release or a chained drag. */
  function holdAfterTap() {
    link.button(0, true);
    heldFromTap = true;

    cancelPendingRelease();
    releaseTimer = setTimeout(() => {
      releaseTimer = null;
      if (!heldFromTap || chained) return;
      link.button(0, false);
      heldFromTap = false;
    }, P.tapHoldMs);
  }

  function cancelPendingRelease() {
    if (releaseTimer === null) return;
    clearTimeout(releaseTimer);
    releaseTimer = null;
  }

  /** Releases a held button immediately, whatever the reason it was held. */
  function releaseHeld() {
    cancelPendingRelease();
    if (!heldFromTap) return;
    link.button(0, false);
    heldFromTap = false;
  }

  function onPadMove(event) {
    pad = event.currentTarget || pad;

    const previous = pointers.get(event.pointerId);
    if (!previous) return;

    event.preventDefault();

    // Walk the real timestamped samples rather than just the delivered event, so total
    // displacement is exact and the velocity estimate sees the true sample rate.
    let totalDx = 0;
    let totalDy = 0;
    let cursor = previous;

    for (const sample of coalescedSamples(event)) {
      const x = sample.clientX;
      const y = sample.clientY;
      totalDx += x - cursor.x;
      totalDy += y - cursor.y;
      cursor = { x: x, y: y, t: sample.timeStamp };

      if (event.isPrimary) pushSample(x, y, sample.timeStamp);
    }

    pointers.set(event.pointerId, cursor);
    travelled += Math.hypot(totalDx, totalDy);

    // A chained press that has moved is a drag, not a second tap. Confirm it visually only once
    // that is known, so the badge never lies.
    if (chained && !dragLocked && travelled >= TAP_SLOP) {
      dragLocked = true;
      pad.classList.add('dragging');
      if (typeof navigator.vibrate === 'function') {
        try { navigator.vibrate(15); } catch (err) { /* unsupported */ }
      }
    }

    // Nothing is sent from here. The handler only records the path; emitFrame resamples it on the
    // animation frame, so pointer and scroll both leave at an even cadence.
    if (pointers.size >= 2) {
      // Two fingers scroll. Only the primary pointer drives it - averaging every contact makes a
      // slight pinch read as scroll jitter.
      setGestureMode('scroll');
    } else if (maxPointers === 1) {
      setGestureMode('move');
    } else {
      // Fingers lifted mid-scroll: stop driving anything rather than jerking the cursor.
      setGestureMode(null);
    }
  }

  /** Switching consumer mid-gesture must not carry the old anchor across, or the cursor jumps. */
  function setGestureMode(mode) {
    if (gestureMode === mode) return;

    flushRemainingMotion();
    gestureMode = mode;
    emitting = false;
  }

  /**
   * Resamples the finger path at a fixed point in time and sends the delta since the last
   * resample. Runs once per animation frame, immediately before the transport flushes.
   */
  function emitFrame() {
    if (!gestureMode || samples.length < 2) return;

    const target = performance.now() - interpolationDelay();
    const point = pathAt(target);
    if (!point) return;

    // First frame of a gesture only anchors: there is no previous point to difference against.
    if (!emitting) {
      emitting = true;
      emitTime = target;
      emitX = point.x;
      emitY = point.y;
      return;
    }

    if (target <= emitTime) return;

    const dx = point.x - emitX;
    const dy = point.y - emitY;
    emitTime = target;
    emitX = point.x;
    emitY = point.y;

    emitMotion(dx, dy);
  }

  function emitMotion(dx, dy) {
    if (dx === 0 && dy === 0) return;

    if (gestureMode === 'scroll') {
      const factor = WHEEL_PER_PX * P.scrollSpeed * speedScale;
      const direction = P.naturalScroll ? 1 : -1;
      link.scrollBy(dx * factor * -direction, dy * factor * direction);

      // Fling velocity from the window, not from one event pair: the latter makes identical
      // flicks coast wildly different distances.
      scrollVelocity = windowedVelocity().y * factor * direction;
      return;
    }

    // Evaluated once per emission: it advances the smoothing filter.
    const gain = pointerGain();
    link.moveBy(dx * gain, dy * gain);
  }

  /**
   * Sends whatever is left between the last resample point and the newest sample.
   *
   * Interpolating behind the input stream means the final fraction of a gesture has not been sent
   * when the finger lifts. Without this the cursor lands slightly short of where the gesture
   * actually ended, and a fast flick loses a visible chunk of its travel.
   */
  function flushRemainingMotion() {
    if (!emitting || samples.length === 0) return;

    const last = samples[samples.length - 1];
    const dx = last.x - emitX;
    const dy = last.y - emitY;

    emitTime = last.t;
    emitX = last.x;
    emitY = last.y;

    emitMotion(dx, dy);
  }

  function endPointer(event) {
    if (!pointers.has(event.pointerId)) return;
    pointers.delete(event.pointerId);

    if (pointers.size > 0) return;

    pad.classList.remove('active');

    // Land the tail of the gesture before anything else reads its outcome: momentum needs the
    // final velocity, and a click must arrive with the cursor already in its final position.
    flushRemainingMotion();

    const duration = event.timeStamp - gestureStart;

    const wasTap = duration < TAP_MS && travelled < TAP_SLOP;

    if (chained) {
      // This gesture adopted the button held by the previous tap, so releasing it completes
      // whatever it turned out to be.
      chained = false;
      releaseHeld();

      if (dragLocked) {
        dragLocked = false;
        pad.classList.remove('dragging');
      } else if (wasTap) {
        // Two quick taps in the same spot: the user meant a double click, so send the second
        // click that the hold deliberately withheld.
        link.button(0, true);
        link.button(0, false);
      }
    } else if (wasTap) {
      // A tap. Which button depends on how many fingers were down at the peak of the gesture.
      if (maxPointers === 1) {
        // Held, not released: see the tap-and-a-half note above.
        holdAfterTap();
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
    gestureMode = null;

    // startMomentum has already taken the velocity it needs; clearing here stops a stale sample
    // window from seeding the next gesture.
    resetSampling();
  }

  // Resample the finger path on every frame, just before the transport sends.
  link.onFrame(emitFrame);

  // The built-in Touchpad tab. Layout pages attach their own trackpads the same way.
  attachTrackpad($('pad'));

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
   * Current pointer gain, from the windowed velocity.
   *
   * Deltas are in CSS pixels, which are already device independent, so there is no devicePixelRatio
   * term here - dividing by it would make high-DPI phones inexplicably slower. The base is
   * screen-relative (see screenFit) so one sensitivity value feels the same on a laptop panel and
   * a 4K desktop.
   *
   * The result is low-pass filtered. Even with a windowed velocity the curve still moves as the
   * finger accelerates, and an abrupt change in gain mid-gesture reads as the cursor twitching;
   * easing it over a few samples keeps the response smooth without adding perceptible lag.
   *
   * Has a side effect on the filter state, so call it exactly once per batch of motion.
   */
  function pointerGain() {
    const speed = windowedVelocity().speed;
    const target = screenFit() * P.sensitivity * speedScale
      * (1 + P.acceleration * Math.min(speed, P.maxSpeed));

    gainSmoothed = gainSmoothed === 0 ? target : gainSmoothed * 0.6 + target * 0.4;
    return gainSmoothed;
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
  let lastMediaState = null;

  /**
   * Every now-playing panel on screen: the Media tab, plus any layout that embeds one.
   *
   * They all paint from the same state and share one set of handlers, so a layout gets the real
   * media interface - artwork, scrub bar, capability-aware transport - rather than a second,
   * slightly different copy of it.
   */
  const mediaUis = [];

  function registerMediaUi(ui) {
    mediaUis.push(ui);
    ui.seeking = false;

    setIcon(ui.playpause, 'play');
    if (ui.prev) setIcon(ui.prev, 'prev');
    if (ui.next) setIcon(ui.next, 'next');
    if (ui.back) setIcon(ui.back, 'rewind');
    if (ui.forward) setIcon(ui.forward, 'forward');

    ui.playpause.addEventListener('click', () => link.sendJson({ t: 'media', cmd: 'playpause' }));
    if (ui.prev) ui.prev.addEventListener('click', () => link.sendJson({ t: 'media', cmd: 'prev' }));
    if (ui.next) ui.next.addEventListener('click', () => link.sendJson({ t: 'media', cmd: 'next' }));

    // Relative seek buttons run host-side actions (typically arrow keys), because many players -
    // browser video in particular - never report a seekable timeline to the media session.
    if (ui.back) ui.back.addEventListener('click', () => link.sendJson({ t: 'shortcut', id: ui.backActionId }));
    if (ui.forward) ui.forward.addEventListener('click', () => link.sendJson({ t: 'shortcut', id: ui.forwardActionId }));

    if (ui.seek) {
      ui.seek.addEventListener('pointerdown', () => { ui.seeking = true; });
      ui.seek.addEventListener('change', () => {
        ui.seeking = false;
        if (media.durationMs > 0) {
          const target = Math.round((Number(ui.seek.value) / 1000) * media.durationMs);
          link.sendJson({ t: 'media', cmd: 'seek', pos: target });
        }
      });
    }

    // A layout can be built after the state arrived; paint it straight away rather than leaving
    // it blank until the next track change.
    if (lastMediaState) paintMediaUi(ui, lastMediaState);
    return ui;
  }

  function paintMediaUi(ui, message) {
    ui.title.textContent = message.active && message.title ? message.title : 'Nothing playing';
    if (ui.artist) ui.artist.textContent = message.artist || '';
    if (ui.app) ui.app.textContent = message.app || '';

    setIcon(ui.playpause, message.status === 'playing' ? 'pause' : 'play');

    // Play/pause stays enabled with no session: the host falls back to the hardware media key,
    // which reaches players the media session never sees.
    if (ui.prev) ui.prev.disabled = !message.canPrevious;
    if (ui.next) ui.next.disabled = !message.canNext;
    if (ui.seek) ui.seek.disabled = !message.canSeek || !message.durationMs;

    const art = ui.art;
    if (!art) return;
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
  }

  link.on('mediaState', (message) => {
    media = {
      positionMs: message.positionMs || 0,
      durationMs: message.durationMs || 0,
      status: message.status,
      at: performance.now(),
    };
    lastMediaState = message;

    for (const ui of mediaUis) paintMediaUi(ui, message);
    paintProgress();
  });

  /**
   * Every volume control on screen, so the Media tab and any layout page showing volume stay in
   * step with each other and with the PC.
   */
  const volumeUis = [];
  let lastMuted = false;

  function registerVolumeUi(ui) {
    volumeUis.push(ui);

    let pending = null;
    ui.slider.addEventListener('input', () => {
      const level = Number(ui.slider.value) / 100;
      if (ui.readout) ui.readout.textContent = Math.round(level * 100) + '%';

      // Coalesced to one message per frame so dragging the slider does not spam the socket.
      if (pending !== null) { pending = level; return; }
      pending = level;
      requestAnimationFrame(() => {
        link.sendJson({ t: 'volume', level: pending });
        pending = null;
      });
    });

    if (ui.mute) {
      // Toggles against the state the PC last reported, rather than a local guess that drifts
      // the moment anything else changes the volume.
      ui.mute.addEventListener('click', () => link.sendJson({ t: 'volume', mute: !lastMuted }));
    }
  }

  link.on('volumeState', (message) => {
    const percent = Math.round((message.level || 0) * 100);
    lastMuted = !!message.muted;

    for (const ui of volumeUis) {
      // Never fight the finger that is currently dragging this slider.
      if (document.activeElement !== ui.slider) ui.slider.value = String(percent);
      if (ui.readout) ui.readout.textContent = percent + '%';
      if (ui.mute) ui.mute.textContent = lastMuted ? '🔇' : '🔊';
      if (ui.device) ui.device.textContent = message.device || '';
    }
  });

  /**
   * Interpolates position locally between the once-a-second updates from the host, so the bar
   * moves smoothly without the host having to stream position at frame rate.
   */
  function paintProgress() {
    let position = media.positionMs;
    if (media.status === 'playing') position += performance.now() - media.at;
    if (media.durationMs > 0) position = Math.min(position, media.durationMs);

    for (const ui of mediaUis) {
      // Never fight the finger that is dragging this particular scrub bar.
      if (ui.seeking) continue;
      if (ui.pos) ui.pos.textContent = formatTime(position);
      if (ui.dur) ui.dur.textContent = formatTime(media.durationMs);
      if (ui.seek) {
        ui.seek.value = media.durationMs > 0
          ? String(Math.round((position / media.durationMs) * 1000))
          : '0';
      }
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

  // The Media tab's now-playing panel. Layout pages register theirs the same way.
  registerMediaUi({
    title: $('mtitle'),
    artist: $('martist'),
    app: $('mapp'),
    art: $('art'),
    playpause: $('playpause'),
    prev: $('prev'),
    next: $('next'),
    seek: $('seek'),
    pos: $('tpos'),
    dur: $('tdur'),
  });

  // The Media tab's volume control. Layout pages register theirs the same way.
  registerVolumeUi({
    slider: $('vol'),
    readout: $('volval'),
    mute: $('mute'),
    device: $('voldev'),
  });

  // ---------------------------------------------------------------- shortcuts

  function renderShortcuts(list) {
    const container = $('shortcuts');
    container.innerHTML = '';

    for (const shortcut of list) {
      const button = document.createElement('button');
      button.className = 'sc';
      const icon = document.createElement('span');
      setIcon(icon, shortcut.icon || 'grid');
      button.appendChild(icon);
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

  // ---------------------------------------------------------------- layouts

  /**
   * Builds the custom control pages the host describes.
   *
   * Nothing here knows what Netflix is. The host sends rows of typed controls and this renders
   * them, so a new page is a config.json edit rather than a code change on either side. An
   * unrecognised control type is skipped rather than aborting the page, so an older client stays
   * usable against a newer host.
   */
  function renderLayouts(layouts) {
    const nav = $('tabs');

    // Every reconnect resends the config and rebuilds these pages. Drop the components the previous
    // build registered, or the registries would accumulate detached elements on each reconnect.
    for (let i = mediaUis.length - 1; i >= 0; i--) if (mediaUis[i].layout) mediaUis.splice(i, 1);
    for (let i = volumeUis.length - 1; i >= 0; i--) if (volumeUis[i].layout) volumeUis.splice(i, 1);

    for (const stale of Array.from(document.querySelectorAll('.layout-page, .layout-tab'))) {
      stale.remove();
    }

    for (const layout of layouts) {
      if (!layout || !layout.id) continue;
      const pageId = 'layout:' + layout.id;

      const page = document.createElement('section');
      page.className = 'page layout-page';
      page.dataset.page = pageId;
      page.hidden = true;

      const body = document.createElement('div');
      body.className = 'layout';
      page.appendChild(body);

      for (const row of layout.rows || []) {
        const rowElement = document.createElement('div');
        rowElement.className = row.fill ? 'layout-row fill' : 'layout-row';

        for (const control of row.controls || []) {
          const element = buildControl(control);
          if (element) rowElement.appendChild(element);
        }

        if (rowElement.childNodes.length) body.appendChild(rowElement);
      }

      // Pages live before the tab bar so the bar stays pinned to the bottom.
      $('app').insertBefore(page, nav);

      const tab = document.createElement('button');
      tab.className = 'tab layout-tab';
      tab.dataset.tab = pageId;

      const icon = document.createElement('span');
      setIcon(icon, layout.icon || 'grid');
      tab.appendChild(icon);
      tab.appendChild(document.createTextNode(layout.label || layout.id));
      nav.appendChild(tab);
    }
  }

  function buildControl(control) {
    switch (control.type) {
      case 'button': return buildLayoutButton(control);
      case 'trackpad': return buildLayoutTrackpad();
      case 'volume': return buildLayoutVolume();
      case 'media': return buildLayoutMedia(control);
      case 'mouse': return buildLayoutMouseButtons();
      case 'label': return buildLayoutLabel(control);
      case 'spacer': {
        const spacer = document.createElement('div');
        spacer.className = 'lc-spacer';
        return spacer;
      }
      default: return null;
    }
  }

  function buildLayoutButton(control) {
    const button = document.createElement('button');
    button.className = control.accent ? 'lc-btn accent' : 'lc-btn';
    button.style.flexGrow = String(control.span || 1);

    if (control.icon) {
      const icon = document.createElement('span');
      icon.className = 'lc-icon';
      setIcon(icon, control.icon);
      button.appendChild(icon);
    }

    if (control.label) {
      const label = document.createElement('span');
      label.className = 'lc-label';
      label.textContent = control.label;
      button.appendChild(label);
    }

    if (control.actionId) {
      button.addEventListener('click', () => link.sendJson({ t: 'shortcut', id: control.actionId }));
    } else {
      button.disabled = true;
    }

    return button;
  }

  function buildLayoutTrackpad() {
    const element = document.createElement('div');
    element.className = 'pad layout-pad used';

    // Same gesture engine as the Touchpad tab, so tap, drag, scroll and tap-and-a-half all behave
    // identically here rather than being a second, subtly different implementation.
    attachTrackpad(element);
    return element;
  }

  function buildLayoutVolume() {
    const wrapper = document.createElement('div');
    wrapper.className = 'lc-volume';

    const mute = document.createElement('button');
    mute.className = 'tbtn small';
    mute.textContent = '🔊';

    const slider = document.createElement('input');
    slider.type = 'range';
    slider.className = 'vol';
    slider.min = '0';
    slider.max = '100';
    slider.step = '1';
    slider.value = '0';

    const readout = document.createElement('span');
    readout.className = 'volval';
    readout.textContent = '0%';

    wrapper.appendChild(mute);
    wrapper.appendChild(slider);
    wrapper.appendChild(readout);

    registerVolumeUi({ slider: slider, readout: readout, mute: mute, layout: true });
    return wrapper;
  }

  /**
   * The Media tab's now-playing interface, embedded in a layout.
   *
   * A compact arrangement of the same component - artwork, title, scrub bar, transport - painted
   * by the same code, plus optional relative-seek buttons for players that only seek by keypress.
   */
  function buildLayoutMedia(control) {
    const make = (tag, className, text) => {
      const element = document.createElement(tag);
      if (className) element.className = className;
      if (text !== undefined) element.textContent = text;
      return element;
    };

    const wrapper = make('div', 'lc-media');

    const top = make('div', 'lc-media-top');

    // Artwork is optional: for video the "cover" is usually a random frame, and the space is worth
    // more to the trackpad than to a thumbnail.
    let art;
    if (control.artwork !== false) {
      art = make('div', 'art small');
      art.appendChild(make('span', '', '♫'));
    }
    const meta = make('div', 'lc-media-meta');
    const title = make('div', 'title', 'Nothing playing');
    const artist = make('div', 'artist');
    const app = make('div', 'app');
    meta.appendChild(title);
    meta.appendChild(artist);
    meta.appendChild(app);
    if (art) top.appendChild(art);
    top.appendChild(meta);

    const scrub = make('div', 'scrub');
    const seek = make('input', 'seek');
    seek.type = 'range';
    seek.min = '0';
    seek.max = '1000';
    seek.step = '1';
    seek.value = '0';
    seek.disabled = true;
    const times = make('div', 'times');
    const pos = make('span', '', '0:00');
    const dur = make('span', '', '0:00');
    times.appendChild(pos);
    times.appendChild(dur);
    scrub.appendChild(seek);
    scrub.appendChild(times);

    const transport = make('div', 'transport compact');
    const ui = { layout: true, title, artist, app, art, seek, pos, dur };

    if (control.seekBackwardId) {
      ui.back = make('button', 'tbtn');
      ui.back.title = 'Back';
      ui.backActionId = control.seekBackwardId;
      transport.appendChild(ui.back);
    }

    // Track buttons are optional: for some players "next track" means something drastic, like
    // jumping to the next episode, and a layout may deliberately leave them out.
    if (control.trackButtons !== false) {
      ui.prev = make('button', 'tbtn');
      transport.appendChild(ui.prev);
    }

    ui.playpause = make('button', 'tbtn big');
    transport.appendChild(ui.playpause);

    if (control.trackButtons !== false) {
      ui.next = make('button', 'tbtn');
      transport.appendChild(ui.next);
    }

    if (control.seekForwardId) {
      ui.forward = make('button', 'tbtn');
      ui.forward.title = 'Forward';
      ui.forwardActionId = control.seekForwardId;
      transport.appendChild(ui.forward);
    }

    wrapper.appendChild(top);
    wrapper.appendChild(scrub);
    wrapper.appendChild(transport);

    registerMediaUi(ui);
    return wrapper;
  }

  function buildLayoutMouseButtons() {
    const wrapper = document.createElement('div');
    wrapper.className = 'lc-mouse';

    for (const [code, text] of [[0, 'Left'], [2, 'Mid'], [1, 'Right']]) {
      const button = document.createElement('button');
      button.className = 'mb';
      button.textContent = text;
      button.addEventListener('pointerdown', (e) => { e.preventDefault(); link.button(code, true); });
      button.addEventListener('pointerup', (e) => { e.preventDefault(); link.button(code, false); });
      button.addEventListener('pointercancel', () => link.button(code, false));
      wrapper.appendChild(button);
    }

    return wrapper;
  }

  function buildLayoutLabel(control) {
    const label = document.createElement('div');
    label.className = 'lc-text';
    label.textContent = control.label || '';
    return label;
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
      stripPointer = null;
      gestureMode = null;
      chained = false;
      resetSampling();

      // A held or dragging button must never survive backgrounding, or it stays down on the PC
      // with no finger left to release it.
      releaseHeld();
      if (dragLocked) {
        dragLocked = false;
        pad.classList.remove('dragging');
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
