<!-- 2026-11-11 23:47 -->
<!-- English version of the protocol notes -->

# Protocols

Both devices are HID devices behind a USB dongle, and the values come from the dongle's vendor
collections. The app writes an **output report** with `WriteFile` and waits for the matching
**input report** with an overlapped `ReadFile` (with a timeout, no busy waiting).

Windows splits an interface into collections by top-level usage. Each collection opens through
its own path, `\\?\hid#vid_xxxx&pid_xxxx&mi_xx&colNN#...`. The buffer must be exactly
`HIDP_CAPS.OutputReportByteLength` / `InputReportByteLength`.

All bytes below include the report id as the first byte, as `WriteFile` / `ReadFile` see them.

---

## ATK MAD R+ (dongle "MAD 8K DONGLE", `373B:1040`)

- **Collection:** interface 1, usage page `0xFF02`, usage `0x0002`. The other vendor collections,
  `FF03..FF06`, do not accept report id `08`.
- **Source:** the ATK/Compx protocol as reverse-engineered in
  [cyberphantom52/libatk-rs](https://github.com/cyberphantom52/libatk-rs) (`src/command.rs`,
  `src/types.rs`), verified on this dongle.

### Command format (17 bytes)

| Byte | Field | Value |
|---|---|---|
| 0 | report id | `08` |
| 1 | command id | see below |
| 2 | status | `00` |
| 3–4 | EEPROM address (big-endian) | `00 00` for status queries |
| 5 | data length | `00` in a request |
| 6–15 | data (10 bytes) | zeros |
| 16 | checksum | `0x55 − (sum of bytes 0..15) & 0xFF` |

The reply comes back with the same report id `08` and the same command id in byte 1. The data
length is in byte 5 and the data starts at byte 6.

### Commands

| ID | Purpose | Reply |
|---|---|---|
| `03` | GetWirelessMouseOnline | `data[0]` = 1 mouse linked, 0 asleep/off. Answered by the **dongle itself, at once** |
| `04` | GetBatteryLevel | `data[0]` = battery %, `data[1]` = 1 charging / 0 not, `data[2..3]` = voltage, mV (BE). Goes over the radio to the mouse |

The other ids (`01`..`1B`: EEPROM, DPI, config, version, etc.) are listed in
`libatk-rs/src/types.rs`.

> **Never send `07` SetEEPROM or `09` RestoreFactory blindly.** `08` is GetEEPROM, read-only;
> the report rate is at address `0x0000`.

#### Report rate

- **Measured motion packet rate** (2026-11-11, Raw Input from a focused window, mouse moved in
  circles): ~730 packets/s, which looks like the 1000 Hz mode.
- **Measure from a focused window.** Windows 11 throttles raw input of a background process to
  about 125 Hz.
- **Value in EEPROM.** ATK Hub reads EEPROM (`08 08`) when opened. Address `0x0000` = `01`, the
  1000 Hz mode, which matches the measurement.
- **Rate encoding.** Switching the rate in the Hub wrote `01`, `02`, `08`, `10`, `20` to
  `0x0000`, so the rates are encoded as bits. Only `01` has been matched to a real rate.

### Example (captured 2026-11-11)

```
-> 08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49
<- 08 04 00 00 00 02 14 00 0e 07 00 00 00 00 00 00 1e     ; 0x14 = 20 %, not charging, 0x0E07 = 3591 mV
<- 08 04 00 00 00 02 23 01 0e 99 ...                       ; 35 %, charging, 3737 mV
<- 08 03 00 00 00 01 00 ...                                ; mouse asleep (the dongle answered itself)
```

### Sleep

The mouse falls asleep after ~1 min without movement: the receiver LED goes out, and the mouse
does not answer `04` until it is moved. So the app asks `03` first (0.5 s timeout) and sends `04`
(1.5 s) only to a linked mouse. A sleeping mouse shows its last known level, not dimmed.

### What the mouse sends by itself (captured 2026-11-11, listening on FF02)

| Event | Reports |
|---|---|
| woke up (moved) | `08 04 ... <%> 00 <mV>` and `08 0a 00 00 00 0a 40` |
| put on the charger (power only) | `08 0a ... 40` ×2–3, then `08 04 ... <%> 01 <mV>` |
| lying on the charger | falls asleep after ~10–20 s (`03` → 0, receiver LED off); when moved, `0a 40` + `08 04 ... 01` again |
| taken off the charger | `08 0a ... 40` ×2, **no** `08 04`; the `0` flag is only visible by asking `04` |

`08 0a` is command `0x0A` ReportMouseStatus from libatk. `data[0] = 0x40` means "link to the
mouse came up". It arrives on any wake-up, not only on charging.

How the app uses these reports:
- It keeps a read open on FF02 and applies `08 04` immediately.
- On `08 0a` it polls once, 0.5 s after the burst ends. Measured 2026-11-11: after being taken
  off the charger the mouse stays linked for ~10 s, and a poll 1.3 s later returned flag 0.
- While the mouse is asleep, the last charging flag is kept. Otherwise the bolt would blink
  between its reports, since on the charger it falls asleep after ~20 s without movement.

On the charger the percentage reads high (20 → 40 % in a couple of minutes), because it is
measured under charging voltage.

#### A mouse asleep on the charger reports nothing (verified 2026-11-11)

- `04` gets no reply even within 4 s, and the mouse sends nothing until it is moved.
- ATK Hub sends `03` once a minute. If that returns 0, it does not send `04`, and it shows
  "charging" from the last report it got.

The app estimates the level itself, but only when the last real reading said "charging" and the
mouse then fell asleep:
- the estimate adds the configured rate (default +2 %/min) to that reading, up to 100 %;
- the tooltip marks it with "≈";
- the first real reading after wake-up replaces the estimate;
- a mouse that fell asleep while not charging is left alone.

---

## HyperX Cloud III S Wireless (dongle `03F0:06BE`, other revisions `03F0:02CC`)

- **Collection:** interface 3.
  - Requests are accepted by the collection with usage page `0x01C0`, usage `0x0001`. On
    `0xFFC0`, `WriteFile` fails.
  - The app tries every collection with `OutputReportByteLength >= 64` and uses the first one
    that accepts the write. The reply arrives on the same collection.
- **Source:** [LennardKittner/HyperHeadset](https://github.com/LennardKittner/HyperHeadset)
  (`cloud_iii_s_wireless`) and the write-up in
  [HeyOkay/HaloBattery docs/protocols.md](https://github.com/HeyOkay/HaloBattery/blob/main/docs/protocols.md);
  verified on this dongle.

### Request (64 bytes, output report)

```
0C 02 03 01 00 <cmd> 00 00 ... 00
```

`0C` is the report id. Byte 3 = `01` means "read"; `00` is "write", do not use it. Sent as a
**feature report**, the same packet is ignored by the dongle, so it must be an output report.

### Reply (input report `0C`)

```
0C .. .. .. .. <cmd> <value> ...
```

| cmd | `value` |
|---|---|
| `06` | battery % (`FF` = no link to the headset) |
| `48` | 0 not charging, 1 charging, 2 fully charged |

### Example (captured 2026-11-11)

```
-> 0C 02 03 01 00 06 00 ...
<- 0C 02 03 01 00 06 42 0F 27 ...     ; 0x42 = 66 %
-> 0C 02 03 01 00 48 00 ...
<- 0C 02 03 01 00 48 00 ...           ; not charging
```

### Notifications (input report `0D`, same collection `01C0`)

`0D 02 03 00 <type> <value> ...` is sent by the headset on its own. The app keeps a permanent
overlapped read on `01C0` and updates the icon at once.

| type (byte 4) | value (byte 5) | Verified 2026-11-11 |
|---|---|---|
| `01` | battery % | per HaloBattery's notes |
| `0A` | 1 charging, 0 not | charger in / out: arrived immediately |
| `0C` | 1 headset linked, 0 link lost | power on / off |
| `0D` | `00` | came on power-on before `0C 01`, meaning unknown |

After `0C 01` the headset needs ~1–2 s before it answers `06`, so the app polls 1.5 s later.
While the headset is off, `06` answers `FF`.

### Caveats

- According to HyperHeadset #36, after the dongle is replugged it sometimes stops answering
  until it is reconnected again or the PC restarts.

---

## How to capture a new protocol

1. Run `python -m pip install hidapi`, then list the collections:
   ```python
   import hid
   for d in hid.enumerate(VID, PID): print(hex(d["usage_page"]), hex(d["usage"]), d["path"])
   ```
2. Capture the vendor software's traffic with USBPcap + Wireshark (filter `usbhid.data` or
   `usb.transfer_type == 0x01`). Look for SET_REPORT / interrupt OUT and the IN replies.
3. Replay the request with `h.write(bytes)` to the right collection and `h.read(64)` from all of
   the device's collections: the reply may arrive somewhere other than where you wrote.
