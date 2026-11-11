<!-- 2026-11-11 23:47 -->
<!-- English README; personal-project disclaimer -->

# TrayBattery

> **A personal project.** I built this for my own setup: one specific mouse, one specific
> headset, Windows 11. It is published in case the code or the protocol notes help someone, but
> it is not a general-purpose battery monitor. Other devices are not supported unless someone
> adds them (see [Adding a device](#adding-a-device)), and feature requests are unlikely to be
> picked up.

A Windows 11 taskbar widget that shows the battery of an **ATK MAD R+** wireless mouse and a
**HyperX Cloud III S Wireless** headset: a device icon with four dots under it. It sits right on
the taskbar, just left of the tray arrow. It is always visible and never hides in the overflow.

![TrayBattery on the taskbar: mouse fully charged, headset off](docs/taskbar-widget.png)

| Battery | Dots |
|---|---|
| 0–20 % | 1 red |
| 21–40 % | 2 orange |
| 41–70 % | 3 green |
| 71–100 % | 4 green |
| mouse asleep | last known level, drawn normally |
| mouse fell asleep while charging | estimated: +2 %/min from the last real reading, up to 100 %; tooltip shows "≈" |
| no link | dimmed icon, four grey dots |

- **Charging:** a yellow bolt in the icon's top-right corner, without animation.
- **Hover:** a tooltip with the model and %. For a sleeping mouse it also says when that value
  was read. With no link it shows "no link since HH:MM" and the last charge.
- **Left click** refreshes. **Right click** opens a menu: one greyed line per device
  (`<device> | 99%`), Refresh, Settings…, the version (greyed), Exit.
- **Settings window:** standard Windows controls. It is created when opened, destroyed when
  closed, and every change applies immediately.
  - Dot style.
  - Icon order, left to right.
  - Start with Windows.
  - **Advanced** checkbox: poll interval (10–3600 s, default 60), dot thresholds (default
    20/40/70 %), the charge rate assumed for a mouse asleep on the charger (default 2 %/min,
    0 turns the estimate off), and Defaults.
- **Dot styles:**
  - **coloured:** as in the table above, with a yellow bolt;
  - **monochrome:** dots and bolt in the icon colour;
  - **monochrome, low battery dot red:** everything in the icon colour except the single dot at
    0–20 %.

## Install

A single portable `TrayBattery.exe`, no installer. The script puts the latest release into
`%LOCALAPPDATA%\Programs\TrayBattery`, turns on autostart (`HKCU\...\Run`) and adds a Start menu
shortcut:

```powershell
irm https://raw.githubusercontent.com/freefakesmile/tray-battery/main/install.ps1 | iex
```

From a clone:

| Command | What it does |
|---|---|
| `.\install.ps1` | installs or updates to the latest release |
| `.\install.ps1 -FromFile .\TrayBattery.exe` | installs a local build |
| `.\install.ps1 -Uninstall` | removes everything, including the registry settings |

New release: `git tag vX.Y.Z && git push --tags`. Actions builds the exe, stamps the version
from the tag and attaches the exe to the release.

## How it works

The whole app is one file, [`TrayBattery.cs`](TrayBattery.cs):
- C# 5 / .NET Framework 4.8, which ships with every Windows 10/11.
- No third-party libraries and no WinForms.
- Own window class and message loop.
- Native menu (`TrackPopupMenuEx`) and timers (`SetTimer`).
- GDI+ drawing (`System.Drawing`).
- HID goes straight through `hid.dll` / `setupapi.dll`.

Device protocols are in [docs/protocols.md](docs/protocols.md).

A hidden top-level `App` window receives the broadcasts instead of `SystemEvents`:

| Message | Event |
|---|---|
| `WM_DEVICECHANGE` | dongle plugged or unplugged |
| `TaskbarCreated` | Explorer restarted |
| `WM_POWERBROADCAST` | sleep / resume |
| `WM_WTSSESSION_CHANGE` | lock / unlock |
| `WM_SETTINGCHANGE` (`ImmersiveColorSet`) | taskbar theme changed |
| `WM_ENDSESSION` | shutdown; the time of the last contact is saved |

### The widget

- A layered window (`WS_EX_LAYERED`, per-pixel alpha through `UpdateLayeredWindow`), created as a
  child of `Shell_TrayWnd`.
- It sits flush left of `TrayNotifyWnd`, whose left edge on Win11 is the tray arrow, and spans
  the full taskbar height.
- Being a child window, it hides together with the taskbar (full-screen games, auto-hide).
- The background has alpha 1: invisible, but still clickable.
- `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` follows the tray. It is restricted to Explorer's
  taskbar thread.
- After an Explorer restart, `TaskbarCreated` re-attaches the widget.
- Tooltips are the native `tooltips_class32`.
- Sizes scale from the taskbar height (48 px = 100 %). The colour follows the taskbar theme
  (`SystemUsesLightTheme`).

### CPU use

Measured 2026-11-11 in normal use with `QueryProcessCycleTime`:
- about 44k cycles/s, i.e. ~0.9 ms of one core per minute (0.0015 %);
- 0.4 wake-ups/s;
- ~4.5 MB private working set.

The only extra wake-ups come while the cursor moves over the taskbar. In those seconds Windows
delivers cursor events to the tray-movement hook, about 0.3 % of a core.

How it stays low:
- **Polling:** one poll per interval, by default once a minute.
  - The timer is `SetCoalescableTimer` with 5 s tolerance, so Windows batches it with other
    wake-ups.
  - Polls run on one long-lived thread that sleeps on an event.
  - The headset is asked only for its level. Its charging state is read after it connects and
    every 10th poll.
- **Hover:** hovering over a sleeping or disconnected device polls at once, at most every 5 s.
- **Device reports:** the headset reports power on/off and charger in/out, and the mouse reports
  its level when it wakes or starts charging.
  - These arrive immediately, without polling.
  - Each device has one pending overlapped read, and its thread sleeps in the kernel until a
    report arrives.
- **I/O:** plain overlapped `WriteFile` / `ReadFile` plus `WaitForSingleObject` with a timeout.
  - No Task/async and no wait loops.
  - A sleeping mouse is not asked over the radio.
- **Device list:** the HID interfaces are cached and re-read on `WM_DEVICECHANGE`.
- **Idle:** timers stop while the session is locked or the PC sleeps.
- **Efficiency mode:** EcoQoS (`SetProcessInformation(ProcessPowerThrottling)`) plus Idle
  priority. The process runs on E-cores at low clocks and never triggers turbo boost.
- **Repaints:** only when a value changes or on hover.

### Registry

Settings live in `HKCU\Software\TrayBattery`:

| Value | Meaning |
|---|---|
| `Monochrome` | dot style: 0 coloured, 1 monochrome, 2 monochrome with a red low dot |
| `Order` | device names joined by `\|` |
| `PollSeconds` | poll interval |
| `Thresholds` | dot thresholds, `20\|40\|70` |
| `ChargeRate` | estimated charge rate of a mouse asleep on the charger, %/min |
| `Advanced` | whether the advanced block is expanded |

The theme and the settings are read once and cached.

The last known level and the time of the last contact live in `HKCU\Software\TrayBattery\Last`
(`<device>` = `<%>|<ticks>`), so a mouse that is asleep at sign-in still shows its charge. They
are written only when:
- the level changes;
- a device falls asleep or disconnects;
- the app exits;
- the session ends.

## Build

Locally, with the compiler that ships with Windows (no SDK needed):

```powershell
.\build.ps1
```

On GitHub, [`.github/workflows/build.yml`](.github/workflows/build.yml) builds on `windows-latest`
on every push; the exe is in the run's artifacts. A `v*` tag also creates a release with
`TrayBattery.exe`.

## Debugging

`TrayBattery.exe --dump out.txt` polls every device once and writes the result to a text file,
without the widget.

## Adding a device

1. Subclass `Gadget`: `Name`, `Glyph` (a Segoe Fluent Icons character), and `Query()` returning
   a `Reading`.
2. Find the collection: `Hid.Find(vid, pid)`, then filter by `UsagePage`.
3. Exchange data with `Hid.Transact(dev, request, isReply, timeoutMs)`. It writes the output
   report and waits for an input report for which `isReply` returns `true`.
4. If the device sends reports on its own, override `PushCollection()` and `ApplyPush(...)`.
5. Add it to `all` in the `App` constructor. Its place on the taskbar is set in Settings.
