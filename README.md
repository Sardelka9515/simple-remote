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
| **Trackpad** | Drag to move, tap to click, two-finger tap for right click, two-finger drag to scroll with momentum, tap-and-a-half to drag. Dedicated L/M/R buttons. |
| **Keyboard** | Type with the phone keyboard (Unicode, so emoji and any layout work), plus arrows, function keys, and latching Ctrl/Alt/Shift/Win for real shortcuts. |
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
node tests/client/gestures.js     # client: gesture -> button event sequences
```

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
  "pointer": {
    "sensitivity": 0.55,
    "acceleration": 0.4,
    "maxSpeed": 3.0,
    "scrollSpeed": 1.0,
    "naturalScroll": true,
    "smoothScroll": true,
    "tapHoldMs": 200
  },
  "shortcuts": [
    { "id": "netflix", "label": "Netflix", "icon": "🎬",
      "action": { "type": "launch", "target": "https://netflix.com" } },
    { "id": "close", "label": "Close window", "icon": "✕",
      "action": { "type": "keys", "target": "Alt+F4" } }
  ]
}
```

Shortcut actions are either `keys` (a combo such as `Ctrl+Shift+Esc`, `Win+P`, `MediaPlayPause`) or
`launch` (a path, document or URL). A combo that fails to parse is dropped at startup rather than
failing silently when tapped.

### Pointer feel

**If the cursor feels too slow or too fast, use the Speed slider under the trackpad.** It applies
live to both cursor and scrolling, and is saved per device — a tablet and a phone driving the same
PC can each have their own setting. That is the intended way to tune feel; the config file below
only moves the centre point of the slider's range.

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

Instead:

1. Every sample is recorded with **its own timestamp**, including the individual samples the
   browser coalesced into one delivered event (`getCoalescedEvents`). That recovers the true
   high-rate stream and keeps total displacement exact.
2. Velocity is measured **across a fixed 45 ms window**, which is stable no matter how the events
   happened to be delivered.
3. The resulting gain is **low-pass filtered**, so it eases as the finger accelerates rather than
   stepping between frames.

Simulated against a constant-speed drag with ±3 ms of timestamp jitter, this cuts gain variance
about ninefold (coefficient of variation 8.7% → 1.0%) while leaving the mean gain unchanged — the
cursor stops twitching without becoming slower or laggier.

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

Simulated against a constant-speed drag, per-frame motion goes from varying 6.9–19.6 px (±24%) to
exactly 13.4 px every frame, with the mean unchanged.

Three details that matter:

- **It never extrapolates** past the newest sample. Guessing where the finger went next overshoots
  and then corrects, which looks exactly like the jitter this is meant to remove. A stalled finger
  simply stops.
- **The lag is measured, not guessed.** Interpolation needs a sample either side of the target
  time, so the target lags the input by a little over one sample interval — about 12 ms on a 120 Hz
  digitizer, ~25 ms on a 60 Hz one. That lag is the entire cost of the technique, so it is derived
  from the observed sample rate rather than making everyone pay the worst case.
- **The tail is flushed** when the finger lifts. Interpolating behind the stream means the last
  fraction of a gesture is still unsent at that moment; without flushing it, the cursor lands
  short and a fast flick visibly loses travel.

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

If some older application integer-divides the wheel delta by 120 and so never scrolls at all, set
`pointer.smoothScroll` to `false` to get the quantised behaviour back.

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

### Two things plain HTTP costs you

Both need a *secure context*, so they only work over HTTPS (or on `localhost`):

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
