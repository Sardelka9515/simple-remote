# Simple Remote

Turn your phone into a keyboard, mouse and media remote for a Windows PC — **without installing
anything on the phone.**

Run `SimpleRemote.exe`, scan the QR code with your phone camera, and the remote opens in the
browser. That is the whole setup.

```
Phone browser  ──WebSocket──►  Kestrel  ──►  receive loop  ──►  SendInput / SMTC / Core Audio
   (web UI)      binary  = input
                 text    = control + state
```

## What it does

| | |
|---|---|
| **Trackpad** | Drag to move, tap to click, two-finger tap for right click, two-finger drag to scroll with momentum, tap-and-a-half to drag. A scroll strip down the right edge scrolls with one thumb. Dedicated L/M/R buttons. |
| **Air mouse** | Switch the trackpad to Motion and point the phone like a TV remote: hold the pad and turn to move the cursor, or Lock to point hands-free. Needs the secure connection (one tap to switch). |
| **Keyboard** | Opening the tab raises the phone keyboard straight away. Type in any language (Unicode, so emoji and any layout work), plus arrows, function keys, and latching Ctrl/Alt/Shift/Win for real shortcuts. |
| **Media** | Now-playing title, artist and album art from whatever is playing, with transport controls, a scrub bar, and a system volume slider. |
| **Shortcuts** | Your own buttons — key combos or app/URL launches — defined in `config.json`. |
| **Clipboard** | Push text to the PC clipboard and pull it back. |

## Requirements

Windows 10 version 1903 or newer. Nothing to install on the phone.

## Build and run

```bash
dotnet run --project src/SimpleRemote.Host
```

### Publishing

Three flavours, all a single `SimpleRemote.exe` — the web UI is embedded in the executable, so
there is genuinely nothing beside it to copy.

| Flavour | Size | Memory | Needs .NET installed | Build needs |
|---|---|---|---|---|
| **Native AOT** | 31 MB | ~43 MB | no | MSVC linker |
| **Framework-dependent** | 26 MB | ~75 MB | yes | nothing extra |
| **Self-contained** | 163 MB | ~75 MB | no | nothing extra |

**Native AOT** — smallest standalone option and the lowest memory use, with no JIT warm-up:

```bash
dotnet publish src/SimpleRemote.Host -c Release -r win-x64 -p:PublishAot=true -p:DebugType=none --artifacts-path artifacts/aot -o out/native-aot
```

**Framework-dependent** — smallest download, but the target machine needs the .NET 10 Desktop and
ASP.NET Core runtimes:

```bash
dotnet publish src/SimpleRemote.Host -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none --artifacts-path artifacts/fx -o out/framework-dependent
```

**Self-contained** — bundles the whole runtime; use it when AOT cannot be built and the target has
no .NET:

```bash
dotnet publish src/SimpleRemote.Host -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none --artifacts-path artifacts/sc -o out/self-contained
```

Three non-obvious things about those command lines, each of which produces a silently wrong build
rather than an error:

- **`-p:SelfContained=false`, not `--self-contained false`.** The CLI flag is overridden when it
  appears *after* a `-p:` argument, so `-p:PublishSingleFile=true --self-contained false` quietly
  publishes self-contained — a 163 MB "framework-dependent" build. The explicit property is
  order-independent.
- **`--artifacts-path` must differ per flavour.** Sharing intermediates lets a self-contained
  publish leave runtime assemblies behind that the next single-file publish bundles, again giving
  163 MB. `BaseIntermediateOutputPath` does *not* work for this: the default `obj/` stops being
  excluded from the compile glob and the generated `AssemblyInfo.cs` files get compiled twice.
- **AOT needs `vswhere.exe` on `PATH`** (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`)
  so ILC can locate the MSVC linker. Without it the native link step fails *after* a full
  compile, with a confusing message about `vswhere` not being recognised. GitHub's
  `windows-latest` runners already have it.

### Tests

```bash
dotnet test                       # host: codec, pairing, key parsing, config migration
node tests/client/gestures.js     # client: gestures, layouts, media panel, icons
```

The client tests render layouts from `tests/client/fixtures/layouts.json`, and a host test fails if
that file differs from what the host actually sends — so the client is never tested against a
layout nobody ships. After deliberately changing a bundled layout, regenerate it with
`UPDATE_FIXTURES=1 dotnet test` and commit the result (and bump the config version, or existing
installs keep the old layout).

The client tests load the real `protocol.js` and `app.js` under a DOM stub, feed synthetic pointer
events into the actual handlers, and decode the binary frames that come out — so they assert the
exact button sequence each gesture emits, which is the difference between a drag and an accidental
double click.

### Releases

Versioning and releases are automated with
[release-please](https://github.com/googleapis/release-please). Merging to `master` keeps a
`chore(release)` pull request open that accumulates the changelog; merging *that* tags the release
and attaches all three binaries.

This only works with [Conventional Commit](https://www.conventionalcommits.org/) subjects — `feat:`
bumps the minor version, `fix:` the patch, and `feat!:` or a `BREAKING CHANGE:` footer the major.
A commit that follows no convention is not releasable and will not appear in the changelog.

The version lives in `SimpleRemote.Host.csproj` next to an `x-release-please-version` marker
comment; removing that comment silently stops the version tracking releases.

## How pairing works

1. The tray icon opens a window showing a QR code for `http://<your-lan-ip>:8787/#p=<token>`.
2. The token sits in the URL **fragment**, which browsers never send to the server, so it stays out
   of request logs and proxy history.
3. The phone redeems it once for a long-lived device token kept in `localStorage`. Reopening the
   page later needs no QR.
4. The device token is sent as the first WebSocket message, never in a URL.

Pairing tokens are single-use and expire after five minutes; the window mints a fresh one
automatically before the old one lapses, so the code on screen is always valid. Paired devices are
listed in the same window and can be revoked, which drops any live connection immediately.

Only SHA-256 hashes of device tokens are stored, in
`%APPDATA%\SimpleRemote\devices.json`.

## Configuration

`%APPDATA%\SimpleRemote\config.json` (tray icon → *Edit settings file*):

```json
{
  "port": 8787,
  "securePort": 8788,
  "pointer": {
    "sensitivity": 0.55,
    "acceleration": 0.4,
    "maxSpeed": 3.0,
    "scrollSpeed": 1.0,
    "naturalScroll": true,
    "smoothScroll": true,
    "tapHoldMs": 200,
    "networkSmoothing": true,
    "maxNetworkBufferMs": 60,
    "airSensitivity": 1.0
  },
  "shortcuts": [
    { "id": "netflix", "label": "Netflix", "icon": "🎬",
      "action": { "type": "launch", "target": "https://netflix.com" } },
    { "id": "close", "label": "Close window", "icon": "✕",
      "action": { "type": "keys", "target": "Alt+F4" } }
  ]
}
```

Shortcut actions are `keys` (a combo such as `Ctrl+Shift+Esc`, `Win+P`, `MediaPlayPause`), `launch`
(a path, document or URL), or `media` (`play`, `pause`, `playpause`, `next`, `prev`, `stop`). A
`keys` action may set `"repeat": n` to fire the combo several times, which is how you drive an app
that only seeks a fixed step per keypress. Anything that fails to compile is dropped at startup,
with the reason recorded, rather than failing silently the first time it is tapped.

## Custom layouts

A layout is a control page declared in `config.json` and rendered generically by the phone, so
**adding a page for a new app needs no code on either side**:

```json
"layouts": [
  {
    "id": "netflix",
    "label": "Netflix",
    "icon": "film",
    "rows": [
      { "controls": [
        { "type": "button", "label": "Skip intro", "icon": "skip", "accent": true,
          "action": { "type": "keys", "target": "S" } }
      ] },
      { "controls": [
        { "type": "media", "trackButtons": false, "artwork": false,
          "seekBackward": { "type": "keys", "target": "Left" },
          "seekForward": { "type": "keys", "target": "Right" } }
      ] },
      { "controls": [{ "type": "volume" }] },
      { "fill": true, "controls": [{ "type": "trackpad" }] }
    ]
  }
]
```

Each layout becomes its own tab. Rows lay out left to right; the one row with `"fill": true` takes
whatever vertical space the fixed rows leave, which is normally the trackpad.

| Control type | What it is |
|---|---|
| `button` | Runs an `action` (same shapes as a shortcut). `accent` draws it as the primary action, `span` makes it wider than its siblings. |
| `trackpad` | A full trackpad, driven by the same gesture engine as the Touchpad tab — tap, drag, two-finger scroll, tap-and-a-half and the one-thumb scroll strip all behave identically. |
| `media` | The Media tab's now-playing panel — artwork, title, scrub bar, play/pause — painted by the same code. Optional `seekBackward` / `seekForward` actions add rewind and fast-forward buttons; `trackButtons: false` hides previous/next, `artwork: false` hides the thumbnail. |
| `volume` | System volume slider and mute, kept in step with the Media tab and with the PC. |
| `mouse` | Left / Middle / Right buttons. |
| `label`, `spacer` | Static text, and flexible empty space. |

**Icons** are either a name from the built-in set — `play`, `pause`, `prev`, `next`, `rewind`,
`forward`, `skip`, `back`, `fullscreen`, `film`, `grid` — which render as SVG, or any other text,
such as an emoji. Prefer the names: Unicode symbols like ⛶ or ⏭ look like a free icon set but depend
on the fonts installed on the phone, and many Android builds show an empty box for them.

Two deliberate properties: a control type the phone does not recognise is **skipped** rather than
breaking the page, so an older phone stays usable against a newer host; and layout action ids are
derived from the control's position, so they stay stable across restarts and can never collide with
a shortcut id.

### The bundled Netflix layout

Skip intro · now-playing panel with ↺ 10 s / play-pause / 10 s ↻ · Volume · Trackpad · Back ·
Fullscreen.

Every button uses a shortcut the Netflix web player actually honours: `S` skips the intro, the
arrow keys seek 10 seconds, `F` toggles fullscreen, and Back is the browser back key. The middle of
the page is the same now-playing panel as the Media tab, so title, artwork, position and play/pause
state come from the Windows media session, and play/pause is a real session command rather than the
space bar — it cannot drift out of step with what is on screen.

Previous/next track and the artwork thumbnail are deliberately hidden: for Netflix the track
buttons jump episodes, which is too easy to hit by accident, and the "artwork" of a video is usually
an arbitrary frame that costs trackpad space. Everything here is plain config, so any of it can be changed.

Keys are delivered to whichever window has focus on the PC, so the Netflix tab needs to be the
focused window for Skip intro, seeking and fullscreen to reach it.

Upgrading from an earlier build replaces a previously saved Netflix layout with this one (config
version 4). Layouts you added yourself are untouched, and if you deleted the Netflix layout it stays
deleted.

### Pointer feel

**If the cursor feels too slow or too fast, use the Speed slider under the trackpad.** It applies
live to both cursor and scrolling, and is saved per device — a tablet and a phone driving the same
PC can each have their own setting. That is the intended way to tune feel; the config file below
only moves the centre point of the slider's range.

**If the cursor shivers or feels rough, raise the Smooth slider; if it feels floaty, lower it.** It
trades steadiness against lag (see *Why the cursor does not jitter*), is saved per device, and
applies from the next touch sample. 0% is close to unfiltered input.

Under the slider, deltas are accelerated **on the phone**, where the true event timestamps are:

```
gain = (screenDiagonal / padDiagonal) × sensitivity × speedSlider
     × (1 + acceleration × min(fingerSpeed, maxSpeed))
```

The base is **screen-relative**: the host reports its virtual desktop size, and the client divides
by the trackpad's own diagonal. This is what makes one sensitivity value feel the same on a laptop
panel and a 4K desktop — a fixed gain is necessarily wrong on most hardware, and was the original
cause of a sluggish cursor. The ratio is taken on the diagonal so the gain stays a single scalar:
separate x and y factors would skew diagonal movement.

Two things worth knowing:

- Windows applies **its own** acceleration to relative mouse input when *Enhance pointer precision*
  is enabled, which it is by default — measured at roughly 2.2× on top of whatever the phone sends.
  Turning it off (Settings → Bluetooth & devices → Mouse → Additional mouse settings → Pointer
  Options) makes this curve the only one in play and the feel much more predictable, at which point
  `sensitivity` wants raising.
- Sub-pixel motion is accumulated rather than truncated, so slow, precise drags are not lost to
  rounding.

### Why the cursor does not jitter

Finger velocity is **never** taken from the gap between two consecutive pointer events. Browsers
deliver `pointermove` at irregular intervals — 1 ms here, 20 ms there, for identical physical
motion — so `distance / dt` over one event pair swings wildly from sample to sample. Feeding that
into an acceleration curve is what makes a cursor twitch.

The whole pipeline lives in `wwwroot/motion.js`, which has no DOM or transport dependencies:

1. Every sample is recorded with **its own timestamp**, including the individual samples the
   browser coalesced into one delivered event (`getCoalescedEvents`). That recovers the true
   high-rate stream.
2. Each axis goes through a **[1€ filter](https://gery.casiez.net/1euro/)**, an adaptive low-pass
   whose cutoff rises with speed. A nearly still finger — aiming at a small button, where a pixel
   of digitizer noise times the pointer gain is very visible — is filtered hard; a fast swipe
   passes almost untouched, and at that speed nobody can see a pixel of noise anyway. The Smooth
   slider moves both of its parameters together.
3. Velocity is measured on the filtered path **across a fixed 45 ms window**, which is stable no
   matter how the events happened to be delivered.
4. The resulting gain is eased on a **50 ms time constant**, so it cannot step between frames. It
   is time-based rather than per-frame, so it behaves the same at 60 Hz and 120 Hz.

Measured by `tests/client/gestures.js` on synthetic paths at the default setting: a resting finger
with ±1 px of digitizer noise wanders 45 px/s unfiltered and about 5 px/s filtered (sub-pixel steps
on the phone, a few pixels on the PC), and a steady swipe arrives as per-frame steps within ±3%.

The cost is lag, and it depends on speed: roughly 10 ms of filter lag on a brisk 800 px/s swipe,
rising to about 45 ms on a slow 100 px/s movement, on top of the interpolation delay below. That is
why it is a slider.

### Tap-and-a-half, and why a tap does not release immediately

Tap, then press again and drag: the press adopts the button the tap left held, so you drag whatever
is under the cursor. Lifting ends the drag.

The important detail is that **a tap does not send a complete click.** It sends the button down and
holds it for `tapHoldMs` (200 ms by default). Only if no second press arrives does the button come
up.

Releasing immediately is what broke this gesture. The host then saw `down, up` followed by another
`down` at the same spot inside Windows' double-click time — which *is* a double click by
definition, so applications ran their double-click behaviour instead of dragging. Holding the
button means the host sees one `down`, the movement, and one `up`: a single clean click-drag.

Two quick taps in place still produce a real double click. The decision is deferred to how the
second gesture turns out — moved means drag, lifted in place means the second click is sent after
all.

The cost is that a plain tap's click activates `tapHoldMs` later. The press itself is immediate, so
buttons still highlight the moment you touch. Lower it if the delay bothers you; raise it if the
pause between your tap and your press is longer than 200 ms.

These sequences are covered by `tests/client/gestures.js`, which drives the real client code and
asserts the exact button events it emits:

```bash
node tests/client/gestures.js
```

### Interpolation: why motion arrives evenly

Fixing the *gain* is not enough on its own, because the *delivery* is also uneven. Animation frames
fire every ~16.7 ms while a 120 Hz digitizer samples every ~8.3 ms, so one frame carries one touch
sample and the next carries three. Emitting whatever arrived since the last frame therefore
produces uneven steps even when the finger moves at a perfectly constant speed.

So nothing is sent from the pointer handler at all. It only records the timestamped path. Once per
frame, that path is **resampled by linear interpolation at a fixed point in time**, and the delta
between successive resample points is what gets sent — to the cursor or to the wheel, depending on
the gesture. Equal time steps in, equal deltas out.

Details that matter:

- **Frames are timed by the animation frame's own timestamp**, not `performance.now()` inside the
  callback, whose start wobbles within the frame and would put that wobble straight into the step
  sizes.
- **It never extrapolates** past the newest sample. Guessing where the finger went next overshoots
  and then corrects, which looks exactly like the jitter this is meant to remove. A stalled finger
  simply stops.
- **The delay is learned from delivery, and held steady.** What interpolation needs is for the read
  point never to run past the newest sample. That depends on how stale the newest sample is when a
  frame runs — a 120 Hz digitizer delivered once per 60 Hz frame can leave it a whole frame old —
  not on the digitizer's sample spacing, which the previous version used. The delay rises as soon as
  a frame is shown to have run dry, relaxes by only 2% of real time, and survives across gestures.
  An earlier version recomputed it every frame, and that moving read point showed up as speed
  wobble on a perfectly steady swipe.
- **A pause is not mistaken for slow delivery.** A resting finger fires no events, so a frame with
  no new samples is only counted once the next samples arrive at normal spacing. Otherwise every
  movement after a brief pause would lag by the length of the pause.
- **The tail is flushed** when the finger lifts. Reading behind the stream means the last stretch of
  a gesture is still unsent at that moment; without flushing it, the cursor lands short and a fast
  flick visibly loses travel.

### Network smoothing: why Wi-Fi bursts do not make the cursor jump

Everything above makes motion leave the phone evenly. Wi-Fi does not deliver it evenly. Power-save
wakeups, frame aggregation and TCP retransmits hold packets back and release several at once, and
because a WebSocket is TCP, one late packet holds back everything behind it. Injected as it lands,
a quarter second of finger movement arrives in a single step: the cursor suddenly jumps.

So each motion frame carries the phone's frame timestamp (opcode `0x06`), and the host replays motion
at the pace it was made (`Input/PlayoutBuffer.cs`, driven by `Input/MotionPlayer.cs`):

- **Clock mapping.** The offset between phone and PC clocks is the smallest `arrival − sent` seen
  recently: the packet that waited least shows the true relationship, anything slower is network
  delay. It is relearned after a second of silence, since a phone's clock can pause while it sleeps.
- **Adaptive playout delay** of one send interval plus the recent worst jitter, capped at
  `maxNetworkBufferMs`. On clean Wi-Fi that is about 20 ms; it grows only while the network is
  actually misbehaving, and relaxes afterwards.
- **Even injection.** Each frame's delta is spread across the time it covers and injected every
  2 ms from a high-resolution waitable timer, rather than as one step per packet.
- **Glide, don't dump.** After a stall longer than the buffer, the backlog plays out at up to 2.5×
  real time. Only a backlog beyond 300 ms is sent at once, where gliding would lag too badly.
- **Clicks and keys are never delayed**, and never reordered: they first flush all pending motion,
  so a click lands exactly where the finger put the cursor.

Measured by `PlayoutBufferTests`: frames held back and released six at a time — 72 px per burst if
injected on arrival — play out with no single 2 ms step above 4 px, and a 200 ms retransmit stall
glides through at the same bound, with total travel exact in both.

On a wired or very clean network, set `networkSmoothing` to `false` to remove the playout delay.

### Scrolling

Wheel motion is sent at **1-unit granularity**, not quantised to whole 120-unit notches. Notch
quantisation is what makes scrolling feel abrupt: nothing moves until the finger has covered a
whole notch, then the view jumps three lines at once. Sub-notch deltas are exactly what a Windows
precision touchpad sends, and what smooth scrolling in browsers and modern apps consumes.

Scrolling is fed by the **same interpolated path** as the cursor, so a two-finger drag produces
equal wheel deltas at equal intervals rather than whatever accumulated between frames. Combined
with 1-unit granularity, that is what makes it track the finger instead of lurching.

Fling momentum uses the same windowed velocity as the cursor, so identical flicks coast the same
distance — an event-pair estimate could report an absurd velocity from one 1 ms sample and launch
the view across the document.

Holding the phone in one hand makes two-finger scroll awkward, so every trackpad has a **scroll
strip** down its right edge: drag a thumb up or down it to scroll. It feeds the same interpolated
path and momentum as two-finger scroll, uses the same direction setting, and only ever scrolls
vertically — a thumb never travels in a perfectly straight line, and letting that drift through
would produce stray sideways scrolling.

If some older application integer-divides the wheel delta by 120 and so never scrolls at all, set
`pointer.smoothScroll` to `false` to get the quantised behaviour back.

### Air mouse

On the Touchpad tab, switch **Touch → Motion**. The pad becomes a clutch:

- **Hold the pad and turn the phone** to move the cursor; let go to reposition your hand, like
  lifting a mouse. Nothing drifts while you are not holding.
- **Tap** to click. Pointing pauses the instant a finger lands, so the jolt of tapping never nudges
  the cursor off its target. Tap-and-a-half then hold is an air *drag*.
- **Lock** points hands-free until you unlock, background the page, or tap (after which it resumes
  once the phone settles).
- Two fingers still scroll.

It works held flat and pointed like a TV remote, or upright like a camera. The gyroscope reports
rotation about the phone's own axes, and which axis means "turn left" depends on the grip, so
`wwwroot/airmouse.js` projects the rotation onto gravity: rotation about the vertical is always
left/right, and rotation about the phone's right edge (made horizontal) is always up/down. Angular
velocity is used rather than absolute orientation, which leans on the magnetometer and drifts or
jumps near speakers and laptops. A soft dead zone of 1.5 °/s absorbs hand tremor.

The integrated angle goes into the **same smoothing path as a finger** — 1€ filter, frame
resampling, frame stamps and host playout all apply. At `airSensitivity` 1.0, about 35° of turn
crosses the desktop's width; the Speed slider applies too.

**It needs HTTPS.** Browsers only deliver motion sensor events to secure pages: Chrome has silently
not fired them over plain HTTP since 2019, and iOS will not show its motion permission prompt
either. So the host also listens on `securePort` (8788) with a self-signed certificate it generates
on first run (`%APPDATA%\SimpleRemote\https.pfx`, ECDSA P-256, naming every LAN address). On plain
HTTP, tapping Motion offers **Switch to secure connection**: the host issues a one-time token and
the phone opens `https://<pc>:8788/` already paired (listed as "… (secure)") and in Motion mode.
The browser warns about the certificate once — a LAN address can never get a publicly trusted
certificate — and the certificate is reused afterwards so the accepted exception keeps working. It
is regenerated only when it nears expiry or the PC's addresses change.

## Why it feels responsive

- **Motion is coalesced to one packet per animation frame.** Sending every `touchmove` (90–120 Hz
  on a modern phone) does not lower latency — it queues packets behind each other and raises it.
- **Clicks and keys bypass the frame timer** and flush immediately, with any pending motion written
  ahead of them, so the cursor is always in place before the button goes down.
- **A binary wire format for input.** A mouse move is 5 bytes; the WebSocket frame header costs
  more than the payload. Several events arriving together become a single batched `SendInput` call.
- **No allocation on the hot path**, on either side — one reused `ArrayBuffer` in the browser, a
  pooled buffer and a stack span on the host.
- **Compression is off** for input frames. Deflating 5 bytes is pure overhead.
- **A 1-second ping** shows live round-trip time in the corner of the UI, and doubles as a keepalive
  that stops phone Wi-Fi power-saving from parking the radio between gestures.

Expect single-digit to low-double-digit milliseconds on 5 GHz Wi-Fi.

## Security

The server binds to your LAN over plain HTTP. That is a deliberate trade: a self-signed certificate
would put a full-page browser warning between the user and a working remote, which defeats the
point of scanning a code.

What that means in practice:

- Anyone already on your network who has a valid device token can control the PC. Tokens are
  256-bit and issued only by scanning a code shown on your own screen.
- **Traffic is not encrypted.** Someone able to watch your LAN can see what you type.
- The firewall rule is scoped to **private and domain** profiles only, never public — an
  input-injection port has no business being open on café Wi-Fi.
- Failed authentication is rate-limited per source address.

Set `useHttps` and `certPath` in `config.json` to move to TLS; the client derives its WebSocket
scheme from the page, so nothing else needs changing.

The **secure port** (`securePort`, 8788 by default, `0` to disable) serves the same UI over HTTPS
with a self-signed certificate, for features browsers reserve for secure pages — see *Air mouse*.
Plain HTTP stays the default for pairing so scanning a code never starts with a certificate
warning. If the secure port cannot start (in use, or no certificate), the remote still comes up on
plain HTTP and the Motion button explains what is missing.

### What plain HTTP costs you

These need a *secure context*, so they only work on the secure port (or on `localhost`):

- **The air mouse** (motion sensors).

- **Reading the phone clipboard automatically.** Pushing PC → phone works fine; phone → PC needs
  you to paste into the box manually. The UI says so rather than offering a button that fails.
- **Keeping the phone screen awake** (`navigator.wakeLock`). The screen dims on its usual timer.

## Troubleshooting

**The QR scans but the page never loads.** Almost always the firewall or the wrong network adapter.
The pairing window has a *Fix firewall* button, and a **Network** dropdown — machines with Hyper-V,
WSL, Docker or a VPN have several plausible-looking addresses, and only one of them is the network
your phone is on. Simple Remote ranks real Wi-Fi and Ethernet adapters above virtual ones, but if
the guess is wrong, pick the right one from the dropdown.

**The cursor or scrolling moves too fast or too slowly.** Use the Speed slider under the trackpad
— it covers 25% to 300% and takes effect immediately. If even 300% is not enough, raise
`pointer.sensitivity` in `config.json`, and see *Pointer feel* above: the Windows *Enhance pointer
precision* setting matters as much as anything in the config file.

**Scrolling moves the wrong way.** Set `pointer.naturalScroll` to `false`.

**Media buttons do nothing in some app.** Apps that do not register with the Windows media session
still usually honour hardware media keys, which is what Simple Remote falls back to. Apps that
honour neither cannot be controlled.

**Port 8787 is in use.** Change `port` in `config.json` and restart.

**Switch to secure connection opens a page that never loads.** The firewall rule predates the secure
port and only allows 8787. Press *Fix firewall* in the pairing window again; it now opens both ports.

**Motion will not start.** The panel that replaces the pad says why, with a line of raw details
(`source`, `events`, `readings`, `permission`, any sensor `error`) worth quoting in a bug report.
The usual causes:

- *Motion sensors are blocked* — Chrome never prompts for sensors; a site that has them blocked just
  receives nothing. Tap the icon at the left of the address bar → Permissions → Motion sensors →
  Allow, then **Try again**.
- *Motion access was not allowed* (iPhone) — Safari remembers a denial until the tab is closed.
  Close the tab, reopen the page and tap Motion again.
- *No motion data arrived* — the browser accepted the request but sent nothing for three seconds.
  Battery saver modes can suspend sensors.

Chromium browsers are read through the Generic Sensor API (`Gyroscope` + `Accelerometer`), which
reports failures explicitly; everything else, and Chromium as a fallback, through `devicemotion`.

**The air mouse moves the wrong way on one phone.** Gravity is reported with opposite signs by
different browsers; `airmouse.js` assumes iOS inverts it. If a browser disagrees, that assumption is
the place to fix.

## Layout

```
src/SimpleRemote.Host/
  Server/       Kestrel host, endpoints, per-socket receive loop, binary codec
  Input/        SendInput interop, injector, Unicode typing, key-combo parser
  Media/        SMTC now-playing, Core Audio volume
  Pairing/      Device registry and QR tokens
  Tray/         Tray icon and pairing window
  wwwroot/      The web UI (embedded into the exe; no build step)
tests/          xunit
```
