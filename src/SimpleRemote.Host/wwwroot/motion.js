/* Finger-path smoothing for the trackpad and scroll surfaces.
 *
 * Pure: no DOM, no transport, no globals besides the export. app.js feeds it timestamped touch
 * samples and asks it, once per animation frame, how far the finger travelled since the last frame.
 * Keeping it separate is what makes the pipeline testable - tests/client/gestures.js drives it with
 * synthetic noisy paths and measures the output directly.
 *
 * The pipeline, in order:
 *
 *   touch samples ──► OneEuroFilter ──► MotionPath ──► Resampler ──► per-frame delta
 *   (irregular,        (removes         (timestamped    (reads the path at a steady
 *    noisy)             jitter)          history)        point behind real time)
 *
 * Every stage works in real timestamps rather than per-event or per-frame steps, because neither
 * touch events nor animation frames arrive at a steady rate, and any "alpha per step" constant is
 * silently a different filter at 60Hz, 120Hz, and on a frame that ran late.
 */

(function (global) {
  'use strict';

  // ---------------------------------------------------------------------------- filtering

  /** Smoothing factor for a first-order low-pass with the given cutoff (Hz) over dt (seconds). */
  function alphaFor(cutoffHz, dtSeconds) {
    const tau = 1 / (2 * Math.PI * cutoffHz);
    return 1 / (1 + tau / dtSeconds);
  }

  /**
   * The 1€ filter (Casiez, Roussel & Vogel, CHI 2012), one axis.
   *
   * A fixed low-pass forces a choice between jitter and lag. This one adapts its cutoff to speed:
   * when the finger is nearly still - aiming at a small button, where a pixel of digitizer noise
   * multiplied by the pointer gain is very visible - the cutoff is low and the noise is removed.
   * As the finger speeds up the cutoff rises, so a fast swipe carries almost no lag, and at that
   * speed nobody can see a pixel of noise anyway.
   *
   *   minCutoff  Hz. Lower means steadier when slow, at the cost of lag when slow.
   *   beta       How quickly the cutoff opens up with speed (per px/s). Higher means less lag
   *              when fast.
   */
  class OneEuroFilter {
    constructor(minCutoff, beta, derivativeCutoff) {
      this.minCutoff = minCutoff;
      this.beta = beta;
      this.derivativeCutoff = derivativeCutoff || 1.0;
      this.reset();
    }

    reset() {
      this._x = null;
      this._dx = 0;
      this._t = null;
    }

    /** Filters one value observed at time t (ms). Returns the filtered value. */
    filter(value, t) {
      if (this._x === null) {
        this._x = value;
        this._t = t;
        return value;
      }

      const dt = (t - this._t) / 1000;
      if (dt <= 0) return this._x;
      this._t = t;

      // Speed estimate, itself lightly filtered - the raw derivative of a noisy signal is noisier
      // still, and would open the cutoff on every twitch.
      const rawDerivative = (value - this._x) / dt;
      this._dx += alphaFor(this.derivativeCutoff, dt) * (rawDerivative - this._dx);

      const cutoff = this.minCutoff + this.beta * Math.abs(this._dx);
      this._x += alphaFor(cutoff, dt) * (value - this._x);
      return this._x;
    }
  }

  /**
   * Maps the one user-facing Smoothing control (0..1) onto filter parameters.
   *
   * Both parameters move together along a geometric scale, so each step of the slider feels like
   * the same amount of change: 0 is nearly raw input, 0.5 is the default, 1 is heavily damped.
   *
   * The default (minCutoff ~1.8Hz, beta ~0.018) is steady at rest - a pixel of digitizer noise
   * multiplied by the pointer gain would otherwise show as the cursor shivering while aiming - while
   * the fairly high beta opens the filter quickly once the finger moves, so a brisk swipe is not
   * paying for that steadiness in lag.
   */
  function smoothingParams(level) {
    const s = Math.min(Math.max(Number.isFinite(level) ? level : 0.5, 0), 1);
    const scale = Math.pow(0.05, s);
    return { minCutoff: 8 * scale, beta: 0.08 * scale };
  }

  // ---------------------------------------------------------------------------- path

  /**
   * The filtered finger path: a short history of timestamped points.
   *
   * Two axes are filtered independently. For the one-finger scroll strip, x is simply always 0.
   */
  class MotionPath {
    constructor(options) {
      const o = options || {};
      this.historyMs = o.historyMs || 250;
      const params = smoothingParams(o.smoothing);
      this._fx = new OneEuroFilter(params.minCutoff, params.beta);
      this._fy = new OneEuroFilter(params.minCutoff, params.beta);
      this.points = [];

      // Typical spacing between digitizer samples (ms). Kept across gestures: it describes the
      // phone, and a fresh gesture has too few samples to measure it.
      this.interval = 8;
    }

    setSmoothing(level) {
      const params = smoothingParams(level);
      for (const f of [this._fx, this._fy]) {
        f.minCutoff = params.minCutoff;
        f.beta = params.beta;
      }
    }

    /** Forgets the path. A new gesture starts from its own first sample, with no lag. */
    reset() {
      this.points = [];
      this._fx.reset();
      this._fy.reset();
    }

    get length() { return this.points.length; }
    get newest() { return this.points.length ? this.points[this.points.length - 1] : null; }

    add(x, y, t) {
      const newest = this.newest;

      // Coalesced events can repeat a timestamp, and a sample from the past would fold the path
      // back on itself. Neither carries usable timing, so neither may reach the filter.
      if (newest && t <= newest.t) return;

      // Gaps longer than a few frames are pauses, not the sample rate.
      if (newest && t - newest.t < 40) this.interval += 0.1 * ((t - newest.t) - this.interval);

      const point ={ x: this._fx.filter(x, t), y: this._fy.filter(y, t), t: t };
      this.points.push(point);

      while (this.points.length > 2 && t - this.points[0].t > this.historyMs) this.points.shift();
    }

    /**
     * Position at time t, linearly interpolated between the samples either side.
     *
     * Never extrapolates past the newest sample: guessing where the finger went next overshoots and
     * then corrects, which looks exactly like the jitter this exists to remove.
     */
    at(t) {
      const points = this.points;
      if (points.length === 0) return null;

      const first = points[0];
      const last = points[points.length - 1];
      if (t <= first.t) return { x: first.x, y: first.y };
      if (t >= last.t) return { x: last.x, y: last.y };

      for (let i = points.length - 1; i > 0; i--) {
        const a = points[i - 1];
        const b = points[i];
        if (t >= a.t) {
          const f = (t - a.t) / (b.t - a.t);
          return { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f };
        }
      }

      return { x: last.x, y: last.y };
    }

    /** The oldest retained sample strictly after time t, or null. */
    firstAfter(t) {
      for (const point of this.points) if (point.t > t) return point;
      return null;
    }

    /**
     * Velocity in px/ms across a time window ending at the newest sample.
     *
     * Never from one pair of events: delivery intervals swing from 1ms to 20ms for identical motion,
     * so distance/dt over a single pair varies by an order of magnitude.
     */
    velocity(windowMs) {
      const points = this.points;
      if (points.length < 2) return { x: 0, y: 0, speed: 0 };

      const last = points[points.length - 1];
      let i = points.length - 1;
      while (i > 0 && last.t - points[i - 1].t <= windowMs) i--;

      const first = points[i];
      const dt = last.t - first.t;
      if (dt <= 0) return { x: 0, y: 0, speed: 0 };

      const vx = (last.x - first.x) / dt;
      const vy = (last.y - first.y) / dt;
      return { x: vx, y: vy, speed: Math.hypot(vx, vy) };
    }
  }

  // ---------------------------------------------------------------------------- resampling

  /**
   * How far behind real time the path is read.
   *
   * Interpolation needs a sample on both sides of the point being read. The previous version
   * derived this from the digitizer's sample interval and recomputed it every frame, which was wrong
   * twice over:
   *
   *  - What matters is how stale the newest sample is when the frame runs, not how far apart samples
   *    are. A 120Hz digitizer whose events are delivered once per 60Hz frame still leaves the newest
   *    sample up to a whole frame old. Reading ahead of it clamps, the cursor stalls for a frame,
   *    then catches up with a double step - the very unevenness this is supposed to remove.
   *  - A delay that changes every frame moves the read point back and forth in time, which shows up
   *    directly as speed wobble even when the finger moves perfectly steadily.
   *
   * So this tracks the observed staleness as a decaying peak: it rises as soon as a frame is known to
   * have found the path running dry, and relaxes only slowly (a couple of percent of real time), which
   * the eye cannot see. It survives across gestures, since it is a property of the phone, not of a
   * swipe.
   *
   * "Known to" matters. A frame that finds no new samples could mean delivery is late, or that the
   * finger simply stopped - pointermove does not fire for a resting finger. Learning from a pause
   * would add lag to every movement after it. So a frame's staleness is only committed once the next
   * samples arrive and prove the motion was continuous: the first of them follows the old newest
   * sample at the digitizer's normal spacing, rather than after a gap.
   */
  class DelayEstimator {
    constructor(options) {
      const o = options || {};
      this.min = o.min !== undefined ? o.min : 6;
      this.max = o.max !== undefined ? o.max : 40;
      this.margin = o.margin !== undefined ? o.margin : 2;
      this.relaxPerMs = o.relaxPerMs !== undefined ? o.relaxPerMs : 0.02;
      this.delay = o.initial !== undefined ? o.initial : 20;
      this._lastFrame = null;
      this._pendingNeed = 0;
      this._pendingNewestT = null;
    }

    /** Forgets in-flight observations (a new gesture), but keeps the learned delay. */
    reset() {
      this._pendingNeed = 0;
      this._pendingNewestT = null;
    }

    /** Updates from this frame's view of the path and returns the delay to read it with. */
    frame(frameTime, path) {
      const elapsed = this._lastFrame === null ? 0 : Math.min(Math.max(frameTime - this._lastFrame, 0), 100);
      this._lastFrame = frameTime;

      const newest = path.newest;
      if (!newest) return this.delay;

      if (this._pendingNewestT !== null && newest.t > this._pendingNewestT) {
        // New samples arrived since the frames that recorded a need. Commit it only if they
        // continue the old path at normal spacing.
        const next = path.firstAfter(this._pendingNewestT);
        const gap = next ? next.t - this._pendingNewestT : Infinity;
        const continuous = gap <= Math.max(path.interval * 2.5, 12);

        if (continuous && this._pendingNeed > this.delay && this._pendingNeed <= this.max) {
          this.delay = this._pendingNeed;
        }
        this._pendingNewestT = null;
        this._pendingNeed = 0;
      }

      this.delay = Math.min(Math.max(this.delay - this.relaxPerMs * elapsed, this.min), this.max);

      // What this frame would have needed so its read point did not run past the newest sample.
      const need = frameTime - newest.t + this.margin;
      if (this._pendingNewestT === null || newest.t === this._pendingNewestT) {
        this._pendingNewestT = newest.t;
        this._pendingNeed = Math.max(this._pendingNeed, need);
      }

      return this.delay;
    }
  }

  /**
   * Turns the path into per-frame deltas.
   *
   * Each frame reads the path at (frame time - delay) and emits the distance from the previous read.
   * For a steadily moving finger that gives equal deltas at equal intervals, whereas sending whatever
   * arrived during each frame gives uneven steps - one frame carries one touch sample, the next three.
   */
  class Resampler {
    constructor(path, delay) {
      this.path = path;
      this.delay = delay;
      this.reset();
    }

    /** Next read starts a new anchor. Needed whenever the consumer or the path changes. */
    reset() {
      this._anchored = false;
      this._t = 0;
      this._x = 0;
      this._y = 0;
      this.delay.reset();
    }

    get anchored() { return this._anchored; }

    /** Delta since the previous frame, or null when there is nothing to send yet. */
    frame(frameTime) {
      const newest = this.path.newest;
      if (!newest || this.path.length < 2) return null;

      const target = frameTime - this.delay.frame(frameTime, this.path);
      const point = this.path.at(target);

      // The first read of a gesture only anchors: there is nothing to difference against.
      if (!this._anchored) {
        this._anchored = true;
        this._t = target;
        this._x = point.x;
        this._y = point.y;
        return null;
      }

      // Time must only move forward. If the delay just grew, hold still for a frame rather than
      // stepping backwards along the path.
      if (target <= this._t) return null;

      const dx = point.x - this._x;
      const dy = point.y - this._y;
      this._t = target;
      this._x = point.x;
      this._y = point.y;
      return dx === 0 && dy === 0 ? null : { dx: dx, dy: dy };
    }

    /**
     * Delta from the last read to the end of the path.
     *
     * Reading behind real time means the final stretch of a gesture has not been sent when the finger
     * lifts. Without this the cursor lands short, and a quick flick loses a visible part of its travel.
     */
    flush() {
      const newest = this.path.newest;
      if (!this._anchored || !newest) return null;

      const dx = newest.x - this._x;
      const dy = newest.y - this._y;
      this._t = newest.t;
      this._x = newest.x;
      this._y = newest.y;
      return dx === 0 && dy === 0 ? null : { dx: dx, dy: dy };
    }
  }

  /**
   * Eases a value toward a target with a fixed time constant, whatever the frame rate.
   *
   * Used for the pointer gain: the acceleration curve moves as the finger speeds up, and an abrupt
   * change in gain mid-gesture reads as the cursor twitching.
   */
  class Smoother {
    constructor(tauMs) {
      this.tauMs = tauMs;
      this.reset();
    }

    reset() {
      this.value = null;
      this._t = null;
    }

    next(target, t) {
      if (this.value === null || this._t === null) {
        this.value = target;
      } else {
        const dt = Math.max(t - this._t, 0);
        this.value += (1 - Math.exp(-dt / this.tauMs)) * (target - this.value);
      }
      this._t = t;
      return this.value;
    }
  }

  global.Motion = { OneEuroFilter, MotionPath, DelayEstimator, Resampler, Smoother, smoothingParams };
})(typeof window !== 'undefined' ? window : globalThis);
