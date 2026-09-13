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

Publish a standalone executable (no .NET runtime needed on the target machine):

```bash
dotnet publish src/SimpleRemote.Host -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

That produces a single `SimpleRemote.exe` of roughly 160 MB. The web UI is compiled into the
executable as embedded resources, so there is genuinely nothing beside it to copy. If the target
machine already has the .NET 10 runtime, drop `--self-contained true` for a ~2 MB executable
instead.

Run the tests:

```bash
dotnet test
```

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
    "naturalScroll": true
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
