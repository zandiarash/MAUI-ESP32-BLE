# IotStarter — Stage 1 design review

Status: **approved** (2026-11-12) · see §11 for amendments made during implementation

---

## 1. Assumptions (defaults applied)

Every bracketed input in the brief was still open, so these defaults apply:

| Input | Value used |
|---|---|
| Board | ESP32-WROOM-32 DevKit (classic ESP32-D0WD), 4 MB flash, **no PSRAM** |
| Catalogue | Model `0x0001` "4-channel switch", outputs GPIO 25, 26, 27, 33, active-high |
| Status LED / reset button | LED GPIO 2, button GPIO 0 (BOOT, active-low with an internal pull-up) |
| Platforms | Android first, Windows for debugging, nothing that blocks iOS |
| Names | Solution `IotStarter`, app "IoT Starter", BLE prefix `IOT` |
| Look / language | Palette proposed at stage 5. English, resource-based strings, RTL-safe layouts |

**Pin check (ESP32 datasheet, classic):** GPIO 25, 26, 27 and 33 are general-purpose input/output pins. None of them is a strapping pin (0, 2, 5, 12, 15), a flash pin (6–11) or an input-only pin (34–39), so all four are fine. GPIO 2 is a strapping pin, but driving an LED from it after boot is harmless. GPIO 0 is read only once the firmware is running, never at power-on.

## 2. What I verified in this session (evidence)

| Check | Result |
|---|---|
| .NET SDK on this machine | `11.0.100`; workloads android 37.2, ios/maccatalyst 27.2, maui-windows 11.0.0 (VS 18.13) |
| C# 15 **union types** without preview | A `union Result(Ok, Fail)` with an exhaustive `switch` **built under the default LangVersion** on SDK 11.0.100. `closed` classes also compile (note: `closed` already implies abstract; writing `abstract` as well is error CS9384) |
| Plugin.BLE 3.2.1 on `net11.0-*` | The package has no `net10`/`net11` assets (it tops out at `net9.0-android35.0`, `net9.0-ios18.0`, `net9.0-windows10.0.19041`). NuGet picks the net9 assets, and a probe project calling `CrossBluetoothLE.Current.Adapter` **compiles cleanly for `net11.0-android` and `net11.0-windows10.0.19041.0`**. Runtime behaviour is not yet proven (see §9) |
| AntDesign | Latest stable is **1.6.2** (assets up to `net10.0`, which `net11.0` consumes). It compiled in the same probe. A `1.6.3-dotnet11.rc3` prerelease exists; I will not use it |
| nanoFramework 1.x set | Latest 1.x packages all depend on **CoreLibrary 1.17.11** and Runtime.Events **1.11.39**, so they are mutually consistent |
| **Trap found** | `nanoFramework.Runtime.Events` **2.0.1 is listed as stable**, but it depends on `CoreLibrary 2.0.0-preview.29`, so it belongs to the 2.x line. NuGet "update all" will offer it. It must be pinned to **1.11.39** |
| Firmware targets | `nanoff 2.5.171 --listtargets --platform esp32` still lists **`ESP32_BLE_REV0`** and **`ESP32_BLE_REV3`** (stable 1.18.0.x). The June 2026 consolidation ([blog post](https://nanoframework.net/how-we-went-from-21-firmware-packages-to-11-and-why-youll-love-it/)) renamed S2/S3/C3/C5/C6/C61/P4 targets only, not the classic ESP32 ones |

## 3. Where I differ from the brief (with reasons)

1. **Two extra projects.** The brief's three-project layout leaves the app's services testable only through a MAUI multi-target project, which a plain test runner cannot reference cleanly. I propose adding `src/IotStarter.App.Core` (plain `net11.0` class library: services, protocol client, state machines, models) and `tests/IotStarter.App.Core.Tests`. The MAUI app keeps the UI and the platform implementations (BLE, permissions, SecureStorage, haptics). This is the "fourth project" the brief allows, plus one library to make it work.
2. **Plugin.BLE is kept, with a compatibility caveat.** It compiles on net11 (see §2), so I am not switching libraries. All use goes through `IBleTransport`, and the runtime check is on the "verify" list. If it misbehaves at runtime on .NET 11's CoreCLR-based Android, the replacement (Shiny.BluetoothLE 6.5.2) swaps in behind the same interface.
3. **The recovery window also retries Wi-Fi.** In the dual-stack strategy, while the 5-minute token-protected provisioning window is open, the device also tries the stored network every 60 s and reboots to normal mode as soon as it gets an IP. Otherwise a router that comes back at minute 5 would wait up to 5 more minutes. This does not happen in the fallback (low-RAM) strategy.
4. **No persistent bonding is a request, not a guarantee.** The device will not keep bonds (it erases any stored bond keys on factory reset and does not request bonding). Android and iOS decide for themselves whether to keep keys. If a phone holds stale keys after a factory reset, pairing fails. The app detects that failure and tells the user to remove "IOT-xxxx" in the phone's Bluetooth settings. I will confirm on real phones whether stale keys happen at all with no-bonding pairing.

Nothing in the brief contradicts the official documentation that I could reach.

## 4. Solution tree

```
IotStarter.sln
├─ src/
│  ├─ IotStarter.Shared/            Shared Project (.shproj + .projitems)
│  │   ProtocolConstants.cs         UUIDs, version, sizes, ports, mDNS type, routes, JSON names
│  │   ProtocolCodes.cs             enums: DeviceState, ErrorCode, CommandType, ResultType, Capability
│  │   ModelCatalog.cs              model IDs and their output tables
│  │   FrameCodec.cs                chunker / reassembler (byte[] only)
│  │   MessageCodec.cs              encode/decode of Info, State, commands, results
│  │   DeviceIdentity.cs            MAC → device ID / short ID / BLE name
│  ├─ IotStarter.Firmware/          nanoFramework app (.nfproj), defines NANOFRAMEWORK
│  │   Program.cs, Board/, Settings/, Provisioning/, Network/, Api/, Discovery/, Diagnostics/
│  ├─ IotStarter.App.Core/          net11.0: services, state machines, models, DTOs (STJ source-gen)
│  └─ IotStarter.App/               MAUI Blazor Hybrid: Razor UI, platform services, resources
├─ tests/IotStarter.App.Core.Tests/ xUnit v3: codec, locator, wizard state machine
├─ docs/protocol.md
├─ docs/design-review.md            (this file)
└─ README.md
```

## 5. Protocol outline (the full spec comes at stage 2)

All multi-byte integers are little-endian. **Device ID** = the 6-byte Wi-Fi station MAC, written as 12 uppercase hex digits. **Short ID** = its last 3 bytes.

### Advertising (legacy, 31 + 31 bytes)

| Packet | Field | Bytes |
|---|---|---|
| ADV | Flags | 3 |
| ADV | Complete list of 128-bit service UUIDs (1 UUID) | 2 + 16 = 18 |
| ADV | Manufacturer data: company `0xFFFF`, version (1), model (2), short ID (3) | 2 + 2 + 6 = 10 |
| | **ADV total** | **31 / 31** |
| SCAN RSP | Complete local name `IOT-1A2B` | 2 + 8 = 10 / 31 |

The ADV packet is exactly full. If the library adds anything else, or will not put the name in the scan response, the fallback is to drop the version byte from the manufacturer data (the version is still readable from the Info characteristic).

### GATT service (fresh random UUIDs at stage 2)

| Characteristic | Properties | Layout |
|---|---|---|
| Info | Read | `ver u8, model u16, deviceId[6], fw u8×3, caps u16` |
| State | Read, Notify | `state u8, error u8, ipv4[4]` |
| Command | Write (encryption) | chunked frames |
| Result | Notify (encryption) | chunked frames |

**Chunk:** `[hdr u8][msgId u8][data …]`. `hdr` = bit 7 END, bits 0–6 chunk index. Chunks are 20 bytes by default (up to MTU−3 when the MTU is larger). A message is at most 512 bytes. Out-of-order index, a wrong msgId, oversize, or a gap longer than 3 s between chunks → the buffer is discarded and an `InvalidFrame` acknowledgement is sent.

**Message:** `[type u8][payload …][crc16-ccitt u16]`.

| Code | Message | Payload |
|---|---|---|
| 0x01 | Identify | — |
| 0x02 | ScanNetworks | — |
| 0x03 | SetCredentials | `ssidLen u8, ssid, passLen u8, pass, token[32]` |
| 0x04 | Cancel | — |
| 0x81 | Ack | `cmd u8, error u8` |
| 0x82 | ScanEntry | `rssi i8, secured u8, ssidLen u8, ssid` |
| 0x83 | ScanDone | `count u8` |
| 0x84 | ProvisionResult | `error u8, ipv4[4]` |

**States:** Ready 1, Scanning 2, Connecting 3, Connected 4, Restarting 5, RecoveryWindow 6.

**Errors:** None 0, InvalidFrame 1, InvalidCommand 2, InvalidPayload 3, Busy 4, NetworkNotFound 5, WrongPassword 6, NoIpAddress 7, Timeout 8, NotAuthorized 9 (token mismatch in the recovery window), StorageFailed 10, UnsupportedSecurity 11, PreviousAttemptFailed 12 (fallback strategy), Internal 255.

**Capability flags:** CanScan, TokenRequired, DeferredVerification, Mdns.

### HTTP and discovery

- HTTP API: `http://<ip>:80/api/v1/...`, with the token as 64 hex characters in the `ApiKey` header (the WebServer library's built-in API-key header).
- mDNS: service `_iotstarter._tcp.local`, TXT `id=…, model=…, api=1`, host `iot-1a2b.local`.
- UDP probe: port 48731. The request is `IOTS?` plus a version byte. The reply is sent to the sender only, as JSON `{id, model, port, api}`.

## 6. Firmware state machine

```
POWER ON → all outputs safe-off → load settings
  │
  ├─ pendingVerification (fallback only) ─► VERIFY (BLE off)
  │        ok  → clear flag ─────────────► NORMAL
  │        fail→ erase creds, lastError=reason, reboot ─► PROVISIONING(open)
  │
  ├─ no stored SSID ───────────────────────► PROVISIONING(open)
  ├─ recoveryWindow flag ──────────────────► PROVISIONING(token-required, 5 min)
  │                                            timeout or Wi-Fi back → clear flag, reboot → NORMAL
  └─ otherwise ────────────────────────────► NORMAL

PROVISIONING:  Advertising ⇄ Central connected
  Ready ─Scan─► Scanning ─► Ready
  Ready ─SetCredentials─► Connecting ─ok─► store creds+token → Connected(ip) → notify → 1.5 s
                                                → stop adv, dispose → Power.RebootDevice()
                                     └fail─► Ready + error (stay, BLE up)
  [fallback strategy] SetCredentials → store as unverified → Restarting → reboot

NORMAL (BLE never initialised):
  Connecting (back-off 2,4,8…60 s) ─ip─► Online: HTTP + mDNS + UDP, LED solid
  Online ─link lost─► Connecting
  Connecting > 5 min ─► set recoveryWindow flag, reboot

ANY STATE: button held 5 s → erase Wi-Fi config, token, settings → reboot
           unhandled worker exception → log, reboot
```

## 7. App add-device wizard state machine

```
CheckPrerequisites ─BT off / denied─► Blocked(reason, fix) ─retry─┐
        │ok                                                      │
        ▼                                                        │
Scanning (live list) ──select──► Connecting ─► Pairing ─► ReadingInfo
        ▲                              │fail                     │
        └──────────── Failed(reason, retry) ◄────────────────────┤
                                                                 ▼
                                ChoosingNetwork (device scan / hidden SSID)
                                         │ ssid + password (validated)
                                         ▼
                           Provisioning (driven by State notifications, cancellable)
                ┌────────────────────────┼────────────────────────┐
         error (WrongPassword…)     Connected(ip)            Restarting (fallback)
        → Failed, keeps input           │                  → WaitingForRejoin (locator, 60 s)
          retry → ChoosingNetwork       ▼                         │ not found → rescan BLE,
                                   HandingOver: GET /info at ip   │ read lastError → Failed
                                   (falls back to locator)◄───────┘
                                         │ ID confirmed → save device + token
                                         ▼
                                  Naming → Done → device screen
          unreachable → Failed(Unreachable: different network / client isolation)
```

The states are modelled as a C# 15 `union`, with exhaustive switches in the view-model layer.

## 8. Firmware target and flashing

- Use **`ESP32_BLE_REV3`** if `nanoff` reports chip revision 3 or later (most current WROOM-32 modules use ESP32-D0WD-V3). Otherwise use **`ESP32_BLE_REV0`**, which also runs on rev 3 chips.
- Command (replace COMx):
  `nanoff --platform esp32 --target ESP32_BLE_REV3 --serialport COMx --update --masserase`
  Without `--fwversion`, nanoff picks the latest stable 1.18.0.x image. Do not pass `--preview`.
- Tool: `dotnet tool install -g nanoff` (version 2.5.171 here).

## 9. Package list (exact versions)

**Firmware (nanoFramework 1.x line; all on CoreLibrary 1.17.x)**

| Package | Version |
|---|---|
| nanoFramework.CoreLibrary | 1.17.12 |
| nanoFramework.Runtime.Events | **1.11.39 (pinned; do not take 2.0.1)** |
| nanoFramework.Runtime.Native | 1.7.11 |
| nanoFramework.Device.Bluetooth | 1.1.125 |
| nanoFramework.System.Device.Wifi | 1.5.152 |
| nanoFramework.System.Net | 1.11.66 |
| nanoFramework.System.Net.Http.Server | 1.5.214 |
| nanoFramework.WebServer | 1.2.162 |
| nanoFramework.Json | 2.2.213 |
| nanoFramework.System.Device.Gpio | 1.1.64 |
| nanoFramework.Hardware.Esp32 | 1.6.42 |
| nanoFramework.System.IO.FileSystem | 1.1.96 |
| nanoFramework.System.IO.Streams | 1.1.96 |
| nanoFramework.System.Text | 1.3.42 |
| nanoFramework.System.Collections | 1.5.75 |
| nanoFramework.System.Threading | 1.1.52 |
| nanoFramework.Iot.Device.MulticastDns | 1.0.337 (pulls UdpClient 1.1.109, Buffers.Helpers 1.0.334) |

**App**

| Package | Version |
|---|---|
| Microsoft.Maui.Controls | 11.0.10 |
| Microsoft.AspNetCore.Components.WebView.Maui | matches 11.0.10 |
| AntDesign | 1.6.2 |
| Plugin.BLE | 3.2.1 |
| Zeroconf (mDNS on Android/Windows; Bonjour path on iOS) | 3.7.16 |
| CommunityToolkit.Maui (only if needed for safe-area/status bar) | 17.0.0 |
| Microsoft.Extensions.Localization | 11.0.0 |
| xunit.v3 / Microsoft.NET.Test.Sdk | 4.0.3 / 18.12.0 |

## 10. Open "verify" items (each will be isolated in a wrapper)

1. Plugin.BLE 3.2.1 at **runtime** on .NET 11 Android (CoreCLR by default) and Windows: scan filter by UUID, MTU request, encrypted write that triggers pairing.
2. Whether nanoFramework's advertising API lets me place manufacturer data and the 128-bit UUID in ADV and the name in SCAN RSP.
3. Whether `WifiAdapter.Connect` reports `InvalidCredential` for a wrong WPA2 password on ESP32, or `Timeout`/`NetworkNotAvailable`. **Acceptance test 3 depends on this.** If it is unreliable, I will combine the result with the scan result: SSID seen, secured, and no association means wrong password.
4. Characteristic protection level, no-bonding pairing and passkey options in `nanoFramework.Device.Bluetooth` 1.1.125 (BluetoothLESample2).
5. Native heap reporting (`NativeMemory.GetMemoryInfo` in Hardware.Esp32) and whether a DHCP host-name API exists in 1.x.
6. WebServer 1.2.162: path parameters (`/outputs/{id}`) and how the per-instance API key is configured.
7. Free RAM with BLE, Wi-Fi, HTTP and mDNS all up on a no-PSRAM module. This decides whether mDNS stays and whether the dual-stack strategy is the default.

**Pairing strength:** the device has only an LED and a button, so LE Secure Connections **Just Works** is the minimum. The stronger option the hardware allows is a **static per-device passkey** printed on the label (the device declares itself display-only and uses a fixed 6-digit passkey). That gives MITM protection as long as the label stays secret. Numeric comparison would need a display.

---

## 11. Amendments made during implementation (stages 2-4)

| Topic | Reviewed plan | What was built, and why |
|---|---|---|
| Advertising layout | Manufacturer data in ADV, name in SCAN RSP | The library source (`CreateLegacyAdvertisements`) always appends manufacturer data last, so the result is **ADV = Flags + UUID + name (31 B)** and **SCAN RSP = manufacturer data (10 B)**. Phones scan actively and receive both; the app merges them per peripheral and shows "identifying…" until the model arrives (`docs/protocol.md` §2) |
| `AddOrUpdateDataSection` | Planned API | `internal` in 1.1.125 (found by type-checking against the real assembly); replaced by `DataSections.Add(...)` as in the ImprovWifi sample |
| Scan results "secured or open" | Planned | `WifiAvailableNetwork` in 1.x exposes no security type. The device reports `Unknown`; the app always offers the password field ("leave empty for an open network"). Flag `ScanReportsSecurity` is reserved for a future firmware |
| nanoFramework.Json | Listed | Not used. Request bodies and the settings file use a strict ~300-line flat-JSON reader/writer (`Storage/FlatJson.cs`) that rejects nested or malformed input and is unit-tested on the desktop. This saves the reflection-based serializer's flash and RAM |
| CoreLibrary / System.Net | 1.17.12 / 1.11.66 | **1.17.11 / 1.11.64**, the exact versions every other 1.x package depends on (and the ImprovWifi sample uses) |
| Extra firmware packages | — | Transitive dependencies of MulticastDns, listed explicitly as packages.config requires: `System.Net.Sockets.UdpClient 1.1.109`, `System.Buffers.Helpers 1.0.334`, `System.Buffers.Binary.BinaryPrimitives 1.2.862`, `System.Net.Http.Server 1.5.214` |
| DHCP host name | Unknown | `WifiAdapter.SetDeviceName` exists and is used |
| mDNS TXT | — | The library writes TXT text raw, without DNS-SD length prefixes; the firmware builds the length-prefixed string itself |
| Wrong-password detection | Open question | The ESP32 native layer maps `AUTH_FAIL`, `AUTH_EXPIRE`, (4-way) handshake timeout, `ASSOC_FAIL` and `BEACON_TIMEOUT` to `InvalidCredential`, which the firmware reports as `WrongPassword` (for secured networks). `BEACON_TIMEOUT`/`ASSOC_FAIL` can also mean weak signal, so a rare false "wrong password" is possible |
| Test runner | VSTest | xunit.v3 4.x runs on Microsoft Testing Platform; `global.json` opts `dotnet test` into it |
| Microsoft.Extensions.*.Abstractions | Package references | Part of the .NET 11 shared framework (NU1510); references removed |
| AntDesign | 1.6.2 (stable) | **1.6.3-dotnet11.rc3 (prerelease).** 1.6.2 throws `TypeLoadException: WebEventCallbackFactoryEventArgsExtensions` on .NET 11 when an `Input` renders (seen in the running app). The prerelease is the vendor's `net11.0` build; switch to the first stable 1.6.3+ |
| Scoped CSS bundle link | Template default | Removed. With no `.razor.css` files the bundle does not exist, and on Windows the missing request hangs, which blocked page start-up |
