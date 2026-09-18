/* Air mouse: turns the phone's gyroscope into pointer motion.
 *
 * Pure, like motion.js: no DOM, no transport. app.js hands it devicemotion events and gets back an
 * accumulated pointing angle, which it feeds into the same smoothing path as the trackpad - so the
 * filter, frame resampling, frame stamps and host playout all apply unchanged.
 *
 * Why angular velocity rather than orientation: absolute orientation (deviceorientation) leans on
 * the magnetometer for heading, which drifts and jumps near speakers, laptops and steel furniture.
 * The gyroscope measures how fast the phone is turning, cleanly, many times a second. Integrated, it
 * gives relative pointing, which is exactly what a mouse needs.
 *
 * Why project onto gravity: the gyroscope reports rotation about the phone's own axes, and which of
 * those means "turn left" depends on how the phone is held. Pointed like a TV remote, turning is
 * rotation about the axis through the screen; held upright like a camera, it is rotation about the
 * long edge. Rotation about the *vertical* is turning left/right in either grip, and the phone's
 * right edge made horizontal is the tilt axis in either grip. Gravity tells us which way is vertical.
 */

(function (global) {
  'use strict';

  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const norm = (v) => {
    const length = Math.hypot(v[0], v[1], v[2]);
    return length > 1e-6 ? [v[0] / length, v[1] / length, v[2] / length] : null;
  };

  class GyroPointer {
    /**
     * deadzone      °/s below which rotation is treated as hand tremor. The phone is never
     *               perfectly still in a hand; without this the cursor creeps.
     * gravityTauMs  Time constant of the gravity estimate. Long enough to ignore the jolt of a
     *               flick, short enough to follow a change of grip within a moment.
     * gravitySign   +1 for spec-conforming browsers, where accelerationIncludingGravity points
     *               up at rest. Some WebKit builds report it inverted; app.js decides per platform.
     */
    constructor(options) {
      const o = options || {};
      this.deadzone = o.deadzone !== undefined ? o.deadzone : 1.5;
      this.gravityTauMs = o.gravityTauMs !== undefined ? o.gravityTauMs : 100;
      this.gravitySign = o.gravitySign !== undefined ? o.gravitySign : 1;
      this.reset();
    }

    /** Forgets the gravity estimate and timing. The accumulated angle carries on from where it is. */
    reset() {
      this._gravity = null;
      this._up = null;
      this._t = null;
      this.x = this.x || 0;
      this.y = this.y || 0;
    }

    /**
     * Consumes one devicemotion event. Returns the accumulated pointing angle in degrees
     * ({ x: right, y: down, t }), or null when the event carries nothing usable.
     */
    sample(event) {
      const rate = event && event.rotationRate;
      const accel = event && event.accelerationIncludingGravity;
      if (!rate || !accel || rate.alpha === null || accel.z === null) return null;

      const t = event.timeStamp;
      const dt = this._t === null ? 0 : Math.min(Math.max(t - this._t, 0), 100);
      this._t = t;

      // Gravity, low-passed so a flick of the wrist does not tip the estimate.
      const s = this.gravitySign;
      const measured = [(accel.x || 0) * s, (accel.y || 0) * s, (accel.z || 0) * s];
      if (this._gravity === null) {
        this._gravity = measured;
      } else {
        const k = dt > 0 ? 1 - Math.exp(-dt / this.gravityTauMs) : 0;
        for (let i = 0; i < 3; i++) this._gravity[i] += k * (measured[i] - this._gravity[i]);
      }
      this._up = norm(this._gravity) || this._up;
      if (!this._up) return null;

      // Angular velocity on the device's x, y, z axes, in °/s. The spec names these by the Euler
      // angle each one changes: beta turns about x, gamma about y, alpha about z.
      const omega = [rate.beta || 0, rate.gamma || 0, rate.alpha || 0];

      // Turning left/right: rotation about the vertical. Positive (right-hand rule, up axis) is a
      // turn to the left, so the cursor moves the other way.
      const yawRate = -dot(omega, this._up);

      // Tilting up/down: rotation about the phone's right edge, made horizontal. Positive lifts
      // where the phone points, and screen y grows downward.
      const up = this._up;
      const tilt = norm([1 - up[0] * up[0], -up[0] * up[1], -up[0] * up[2]]);
      const pitchRate = tilt ? -dot(omega, tilt) : 0;

      // Soft dead zone on the combined rate: fades in over one more deadzone-width rather than
      // switching on abruptly, and scales both axes together so direction is preserved.
      const speed = Math.hypot(yawRate, pitchRate);
      const fade = Math.min(Math.max((speed - this.deadzone) / this.deadzone, 0), 1);

      this.x += yawRate * fade * dt / 1000;
      this.y += pitchRate * fade * dt / 1000;
      return { x: this.x, y: this.y, t: t };
    }
  }

  global.AirMouse = { GyroPointer };
})(typeof window !== 'undefined' ? window : globalThis);
