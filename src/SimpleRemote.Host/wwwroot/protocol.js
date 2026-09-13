/* Transport for the remote.
 *
 * The whole latency story lives in this file. Two ideas do most of the work:
 *
 *  1. Pointer motion is coalesced to one packet per animation frame. Sending every touchmove
 *     event (90-120Hz on a modern phone) does not reduce latency - it queues packets behind each
 *     other and makes it worse. One packet per frame is both smoother and far cheaper.
 *
 *  2. Discrete events (clicks, keys) bypass the frame timer and flush immediately, with any
 *     pending motion written ahead of them, so the cursor is always in the right place before the
 *     button goes down.
 */

(function () {
  'use strict';

  const OP = { MOVE: 0x01, BTN: 0x02, SCROLL: 0x03, KEY: 0x04, PING: 0x05, PONG: 0x81 };

  // Stop writing when the socket is already backed up: queueing behind a stalled send is exactly
  // the latency we are trying to avoid.
  const MAX_BUFFERED = 8192;
  const PING_INTERVAL_MS = 1000;
  const BUFFER_BYTES = 512;

  class RemoteLink {
    constructor() {
      this.ws = null;
      this.state = 'closed';

      // One preallocated buffer, reused forever - the hot path allocates nothing.
      this._buf = new ArrayBuffer(BUFFER_BYTES);
      this._view = new DataView(this._buf);
      this._u8 = new Uint8Array(this._buf);
      this._len = 0;

      this._dx = 0;
      this._dy = 0;
      this._sx = 0;
      this._sy = 0;

      this._pingSeq = 0;
      this._pingsInFlight = new Map();
      this.rtt = null;

      this._handlers = new Map();
      this._frameHook = null;
      this._creds = null;
      this._retry = 0;
      this._retryTimer = null;
      this._pingTimer = null;
      this._rafHandle = null;
      this._closedByUs = false;
    }

    on(type, handler) {
      if (!this._handlers.has(type)) this._handlers.set(type, []);
      this._handlers.get(type).push(handler);
    }

    _emit(type, payload) {
      const list = this._handlers.get(type);
      if (list) for (const handler of list) handler(payload);
    }

    connect(creds) {
      this._creds = creds || this._creds;

      // Reconnect triggers (visibility change, backoff timer) call this with no argument. Without
      // credentials there is nothing to authenticate with, so opening a socket would only produce
      // a reconnect loop that fails on every onopen.
      if (!this._creds || !this._creds.deviceId || !this._creds.token) return;

      this._closedByUs = false;
      this._open();
    }

    close() {
      this._closedByUs = true;
      clearTimeout(this._retryTimer);
      clearInterval(this._pingTimer);
      if (this._rafHandle) cancelAnimationFrame(this._rafHandle);
      this._rafHandle = null;
      if (this.ws) this.ws.close();
    }

    _open() {
      if (!this._creds) return;

      // Scheme is derived, never hardcoded, so switching the host to HTTPS needs no client change.
      const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
      const url = `${scheme}//${location.host}/ws`;

      this._setState('connecting');

      let ws;
      try {
        ws = new WebSocket(url);
      } catch (err) {
        this._scheduleRetry();
        return;
      }

      ws.binaryType = 'arraybuffer';
      this.ws = ws;

      ws.onopen = () => {
        // Credentials go in the first message rather than the URL, so they never reach a log.
        this.sendJson({
          t: 'auth',
          deviceId: this._creds.deviceId,
          token: this._creds.token,
          name: this._creds.name,
        });
      };

      ws.onmessage = (event) => {
        if (typeof event.data === 'string') {
          let message;
          try {
            message = JSON.parse(event.data);
          } catch (err) {
            return;
          }

          if (message.t === 'authResult') {
            if (message.ok) {
              this._retry = 0;
              this._setState('open');
              this._startLoops();
            } else {
              this._closedByUs = true;
              this._emit('authFailed', message);
              ws.close();
              return;
            }
          }

          this._emit(message.t, message);
          return;
        }

        const bytes = new Uint8Array(event.data);
        if (bytes.length >= 5 && bytes[0] === OP.PONG) {
          const view = new DataView(event.data);
          const seq = view.getUint32(1, true);
          const sentAt = this._pingsInFlight.get(seq);
          if (sentAt !== undefined) {
            this._pingsInFlight.delete(seq);
            const sample = performance.now() - sentAt;
            // Light smoothing: a single stray packet should not make the readout jump.
            this.rtt = this.rtt === null ? sample : this.rtt * 0.7 + sample * 0.3;
            this._emit('rtt', this.rtt);
          }
        }
      };

      ws.onclose = () => {
        this._setState('closed');
        clearInterval(this._pingTimer);
        if (this._rafHandle) cancelAnimationFrame(this._rafHandle);
        this._rafHandle = null;
        this._pingsInFlight.clear();
        if (!this._closedByUs) this._scheduleRetry();
      };

      ws.onerror = () => { /* onclose always follows; nothing useful to add here. */ };
    }

    _startLoops() {
      clearInterval(this._pingTimer);
      this._pingTimer = setInterval(() => this._ping(), PING_INTERVAL_MS);
      this._ping();

      if (!this._rafHandle) this._rafHandle = requestAnimationFrame(() => this._frame());
    }

    /**
     * Called once per animation frame, immediately before the buffer is flushed.
     *
     * This is where the caller resamples its input path, so motion is produced on a steady cadence
     * instead of whenever input events happened to arrive.
     */
    onFrame(handler) {
      this._frameHook = handler;
    }

    _frame() {
      this._rafHandle = null;

      if (this._frameHook) {
        // A fault in the hook must not kill the frame loop and with it the whole transport.
        try { this._frameHook(); } catch (err) { /* keep the loop alive */ }
      }

      this.flush(false);
      if (this.state === 'open') this._rafHandle = requestAnimationFrame(() => this._frame());
    }

    _scheduleRetry() {
      clearTimeout(this._retryTimer);

      // Exponential backoff with jitter, capped at 5s. Jitter matters when several devices come
      // back at once after the Wi-Fi drops.
      const base = Math.min(250 * Math.pow(2, this._retry), 5000);
      const delay = base * (0.7 + Math.random() * 0.6);
      this._retry = Math.min(this._retry + 1, 6);
      this._retryTimer = setTimeout(() => this._open(), delay);
    }

    _setState(state) {
      if (this.state === state) return;
      this.state = state;
      this._emit('state', state);
    }

    _ping() {
      if (this.state !== 'open') return;

      const seq = (this._pingSeq = (this._pingSeq + 1) >>> 0);
      this._pingsInFlight.set(seq, performance.now());

      // Drop stale entries so a flaky link cannot grow this map without bound.
      if (this._pingsInFlight.size > 16) {
        const oldest = this._pingsInFlight.keys().next().value;
        this._pingsInFlight.delete(oldest);
      }

      this._reserve(5);
      this._u8[this._len] = OP.PING;
      this._view.setUint32(this._len + 1, seq, true);
      this._len += 5;
      this.flush(true);
    }

    // ---- hot path ------------------------------------------------------------------

    /** Accumulates pointer motion. Flushed once per animation frame. */
    moveBy(dx, dy) {
      this._dx += dx;
      this._dy += dy;
    }

    /** Accumulates wheel motion in wheel units (120 per notch). */
    scrollBy(dx, dy) {
      this._sx += dx;
      this._sy += dy;
    }

    /**
     * Throws away motion that has been accumulated but not sent.
     *
     * Needed when the page is backgrounded: requestAnimationFrame stops, so whatever the finger
     * was doing at that moment sits in the accumulator and would be delivered as one large jump
     * the instant the user came back.
     */
    dropPendingMotion() {
      this._dx = this._dy = this._sx = this._sy = 0;
    }

    /** Mouse button. Sent immediately, behind any pending motion. */
    button(button, down) {
      this._reserve(3);
      this._u8[this._len] = OP.BTN;
      this._u8[this._len + 1] = button;
      this._u8[this._len + 2] = down ? 1 : 0;
      this._len += 3;
      this.flush(true);
    }

    /** Virtual key. Sent immediately. */
    key(vk, down) {
      this._reserve(4);
      this._u8[this._len] = OP.KEY;
      this._view.setUint16(this._len + 1, vk, true);
      this._u8[this._len + 3] = down ? 1 : 0;
      this._len += 4;
      this.flush(true);
    }

    tap(vk) {
      this.key(vk, true);
      this.key(vk, false);
    }

    /**
     * Writes pending motion, then sends the buffer.
     *
     * Motion is written here rather than at accumulation time so that a click flushed mid-frame
     * still carries the movement that preceded it, in the right order.
     */
    flush(force) {
      if (this.state !== 'open' || !this.ws) { this._len = 0; return; }

      if (!force && this.ws.bufferedAmount > MAX_BUFFERED) return;

      const dx = Math.trunc(this._dx);
      const dy = Math.trunc(this._dy);
      if (dx !== 0 || dy !== 0) {
        this._dx -= dx;
        this._dy -= dy;
        this._writeMotion(OP.MOVE, dx, dy);
      }

      const sx = Math.trunc(this._sx);
      const sy = Math.trunc(this._sy);
      if (sx !== 0 || sy !== 0) {
        this._sx -= sx;
        this._sy -= sy;
        this._writeMotion(OP.SCROLL, sx, sy);
      }

      if (this._len === 0) return;

      try {
        this.ws.send(this._u8.subarray(0, this._len));
      } catch (err) {
        // A send on a socket that is closing throws; the close handler will reconnect.
      }
      this._len = 0;
    }

    _writeMotion(op, x, y) {
      this._reserve(5);
      this._u8[this._len] = op;
      this._view.setInt16(this._len + 1, clampI16(x), true);
      this._view.setInt16(this._len + 3, clampI16(y), true);
      this._len += 5;
    }

    /** Sends early if another message would not fit, so the buffer never needs to grow. */
    _reserve(bytes) {
      if (this._len + bytes > BUFFER_BYTES) {
        if (this.state === 'open' && this.ws && this._len > 0) {
          try { this.ws.send(this._u8.subarray(0, this._len)); } catch (err) { /* closing */ }
        }
        this._len = 0;
      }
    }

    // ---- control path --------------------------------------------------------------

    sendJson(object) {
      if (!this.ws || this.ws.readyState !== WebSocket.OPEN) return;
      try {
        this.ws.send(JSON.stringify(object));
      } catch (err) {
        /* closing */
      }
    }
  }

  function clampI16(value) {
    return value < -32768 ? -32768 : value > 32767 ? 32767 : value;
  }

  window.RemoteLink = RemoteLink;
  window.RemoteOps = OP;
})();
