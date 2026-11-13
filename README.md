# IoT Starter

ESP32 devices running **.NET nanoFramework** that are set up over **Bluetooth LE** and then controlled over the **local network** by a **.NET MAUI Blazor Hybrid** app (.NET 11, C# 15, Ant Design Blazor).

1. A device with no Wi-Fi credentials advertises a provisioning service over BLE.
2. The app finds it, identifies the model, and sends the Wi-Fi name, the password and a per-device API token over an encrypted BLE link.
3. The device joins Wi-Fi with BLE still up and reports the result: success with its IP address, or a specific error such as a wrong password. On success it reboots with BLE off.
4. From then on the app finds it on the LAN (last known IP → mDNS → UDP probe, always confirmed by device ID) and switches its outputs over HTTP.

| Document | Content |
|---|---|
| [docs/protocol.md](docs/protocol.md) | BLE and HTTP protocol: advertising layout, GATT, framing, codes, sequences, timeouts, discovery |
| [docs/design-review.md](docs/design-review.md) | Assumptions, decisions, package versions, and the amendments made during implementation |
| [docs/ui-design-brief.md](docs/ui-design-brief.md) | Visual system, components per screen, wireframes |

---

## 1. Solution layout

```
IotStarter.sln
├─ src/IotStarter.Shared/            Shared Project: protocol constants, codes, codecs, model catalogue
├─ src/IotStarter.Firmware/          nanoFramework app (.nfproj), compiles the Shared sources
├─ src/IotStarter.App.Core/          net11.0 library: BLE protocol client, wizard state machine,
│                                    locator, HTTP client, device store, app state, demo mode
├─ src/IotStarter.App/               MAUI Blazor Hybrid app: UI and platform services
├─ tests/IotStarter.App.Core.Tests/  xUnit v3: codecs, firmware JSON parser, locator, wizard,
│                                    device manager, resource keys
└─ docs/
```

The firmware and the app share code by **compiling the same files** (`IotStarter.Shared.projitems`), not by referencing an assembly. Shared code is written in a conservative C# subset: no generics, LINQ or records, and `#nullable disable` at the top of each file.

## 2. Prerequisites

| Tool | Version used | Notes |
|---|---|---|
| Visual Studio 2026 | 18.x | Workloads: *.NET Multi-platform App UI*, *Mobile development with .NET*. Extension: **.NET nanoFramework** (from the Marketplace) |
| .NET SDK | 11.0.100 | Pinned in `global.json` (roll-forward to the latest feature band) |
| MAUI workloads | android 37.2 / ios 27.2 / maui-windows 11.0 | `dotnet workload install maui` |
| nanoff | 2.5.171 | `dotnet tool install -g nanoff` |
| USB driver | CP210x or CH340 | Depends on the DevKit's USB-UART chip |
| Hardware | ESP32-WROOM-32 DevKit, 4 MB flash, no PSRAM | Outputs on GPIO 25, 26, 27, 33 (active-high); LED GPIO 2; button GPIO 0 (BOOT) |

## 3. Flash the nanoFramework firmware (once per board)

1. Connect the board and find its COM port in Device Manager.
2. Check the chip revision: `nanoff --platform esp32 --serialport COM5 --devicedetails` prints `revision N`.
3. Flash an image **with Bluetooth**:

   ```powershell
   # Chip revision 3 or later (most current WROOM-32 modules):
   nanoff --platform esp32 --target ESP32_BLE_REV3 --serialport COM5 --update --masserase

   # Older chips (rev 0/1); this image also runs on rev 3:
   nanoff --platform esp32 --target ESP32_BLE_REV0 --serialport COM5 --update --masserase
   ```

   Without `--fwversion`, nanoff installs the latest **stable** 1.18.0.x image. Do not pass `--preview`: preview images are for the 2.0 class libraries. `--masserase` also clears the Wi-Fi configuration store, so the device starts factory-fresh.

   Images without BLE (`ESP32_REV0`, `ESP32_REV3`) make `nanoFramework.Device.Bluetooth` throw "not supported".

## 4. Build and deploy the firmware

`.nfproj` files build only through the nanoFramework project system in Visual Studio, **not** with `dotnet build`.

1. Open `IotStarter.sln` in Visual Studio 2026 with the nanoFramework extension.
2. Restore NuGet packages: right-click the solution → *Restore NuGet Packages*. The firmware uses `packages.config`, so packages land in `./packages/`.
3. Select the board in **Device Explorer**, set `IotStarter.Firmware` as the startup project and press **F5**. Output appears in the *Debug* window. The passphrase and token are never logged.

### Firmware NuGet packages (to recreate the project from the VS template if needed)

Create a *Blank Application (.NET nanoFramework)* project, add the Shared project reference (*Add → Shared Project Reference → IotStarter.Shared*), add `NANOFRAMEWORK` to the conditional compilation symbols, copy the source folders, and install **exactly** these versions. The set is consistent: every library depends on CoreLibrary 1.17.11.

| Package | Version |
|---|---|
| nanoFramework.CoreLibrary | 1.17.11 |
| nanoFramework.Runtime.Events | **1.11.39** (do **not** take 2.0.1: it requires CoreLibrary 2.0 preview) |
| nanoFramework.Runtime.Native | 1.7.11 |
| nanoFramework.Device.Bluetooth | 1.1.125 |
| nanoFramework.Hardware.Esp32 | 1.6.42 |
| nanoFramework.System.Device.Wifi | 1.5.152 |
| nanoFramework.System.Device.Gpio | 1.1.64 |
| nanoFramework.System.Net | 1.11.64 |
| nanoFramework.System.Net.Http.Server | 1.5.214 |
| nanoFramework.WebServer | 1.2.162 |
| nanoFramework.Iot.Device.MulticastDns | 1.0.337 |
| nanoFramework.System.Net.Sockets.UdpClient | 1.1.109 |
| nanoFramework.System.Buffers.Helpers | 1.0.334 |
| nanoFramework.System.Buffers.Binary.BinaryPrimitives | 1.2.862 |
| nanoFramework.System.IO.FileSystem | 1.1.96 |
| nanoFramework.System.IO.Streams | 1.1.96 |
| nanoFramework.System.Text | 1.3.42 |
| nanoFramework.System.Collections | 1.5.75 |
| nanoFramework.System.Threading | 1.1.52 |

Deployment fails when the managed packages do not match the native assemblies in the flashed image. If Visual Studio reports a version mismatch, update the firmware with `nanoff --update` (section 3) rather than mixing package versions.

### Firmware configuration (`FirmwareConfig.cs`)

| Setting | Default | Meaning |
|---|---|---|
| `Strategy` | `JoinWhileBleActive` | Default strategy: join Wi-Fi while BLE stays up. `VerifyAfterReboot` is the low-RAM fallback (section 9) |
| `EnableMdns` | `true` | DNS-SD responder; turn off if RAM is tight (UDP discovery remains) |
| `RestartViaDeepSleep` | `false` | Use a 1 s deep-sleep timer wake-up instead of a software reboot, only if a reboot leaves the radio in a bad state |
| `ModelId` | `0x0001` | Model this image is built for (`ModelCatalog` in Shared) |

### LED patterns (GPIO 2)

| Pattern | Meaning |
|---|---|
| Two short blinks, repeating | Waiting to be set up over BLE |
| Three short blinks, repeating | Recovery window: offline for 5 min, credentials kept; accepts new Wi-Fi details only from the owning phone |
| Slow blink | Joining Wi-Fi |
| Solid | Online |
| Fast blink | Identify (3 s), or reset button held (keep holding) |

**Factory reset:** hold BOOT (GPIO 0) for **5 seconds** while the firmware is running. This erases the Wi-Fi credentials, the API token and the settings, then reboots into setup mode. Do not hold it while plugging in: that enters the ESP32 download mode.

## 5. Build and run the app

### Command line

```powershell
# Unit tests (Microsoft Testing Platform, opted in via global.json)
dotnet test tests/IotStarter.App.Core.Tests

# Android (device connected with USB debugging)
dotnet build src/IotStarter.App -f net11.0-android -t:Run

# Windows (desktop debugging; BLE works if the PC has Bluetooth LE)
dotnet build src/IotStarter.App -f net11.0-windows10.0.19041.0 -t:Run

# iOS: needs a Mac (paired from Visual Studio, or build on macOS)
dotnet build src/IotStarter.App -f net11.0-ios
```

Build the **projects**, not `IotStarter.sln`, from the command line. The solution contains the `.nfproj`, which `dotnet build` cannot build.

### Visual Studio

Set `IotStarter.App` as the startup project, pick an Android device or *Windows Machine*, and press F5.

### Demo mode (no hardware)

Turn on *Settings → Demo mode*. Simulated devices then advertise and answer the real protocol code. The simulator uses the same shared codec as the firmware.
- The password for **Home-WiFi** is `demo1234`. Any other password gives "Wrong Wi-Fi password", and an SSID not in the list gives "Can't find …".
- One simulated device has an **unknown model**.
- Restarting a demo device gives it a new IP address, which shows rediscovery at work.

BLE cannot be exercised in the Android emulator or the iOS simulator, so demo mode is how to review the UI there.

### Package versions (app)

| Package | Version | Note |
|---|---|---|
| Microsoft.Maui.Controls / Components.WebView.Maui | 11.0.10 | |
| **AntDesign** | **1.6.3-dotnet11.rc3** | **Prerelease.** The latest stable (1.6.2) crashes on .NET 11 when an `Input` renders (`Could not load type 'WebEventCallbackFactoryEventArgsExtensions'`: the type was removed in ASP.NET Core 11). 1.6.3-dotnet11.rc3 is the vendor's `net11.0` build. Move to the first stable 1.6.3+ when published |
| Plugin.BLE | 3.2.1 | Ships `net9.0-*` assets, which `net11.0-*` consumes. All use is behind `IBleTransport` (`Services/PluginBleTransport.cs`) |
| Zeroconf | 3.7.16 | mDNS browse; its iOS build uses the system Bonjour API |
| Microsoft.Extensions.Localization | 11.0.0 | `IStringLocalizer<Strings>` over `Resources/Strings.resx` |
| xunit.v3 | 4.0.3 | Tests |

## 6. Platform requirements and why

### Android
| Item | Why |
|---|---|
| `BLUETOOTH_SCAN` (`neverForLocation`) and `BLUETOOTH_CONNECT` | Required at runtime on Android 12+ to scan and connect. Requested through MAUI's `Permissions.Bluetooth` when the wizard starts |
| `ACCESS_FINE_LOCATION` (maxSdk 30), `BLUETOOTH`, `BLUETOOTH_ADMIN` (maxSdk 30) | Android 11 and older can only scan with location permission, **and Location must be switched on**. The wizard tells the user on those versions |
| `network_security_config.xml` with `cleartextTrafficPermitted="true"` | Devices serve plain HTTP at DHCP addresses. Android's domain config cannot express IP ranges, so cleartext is allowed app-wide. The app only speaks HTTP to devices it found on the LAN |
| `CHANGE_WIFI_MULTICAST_STATE` and a **multicast lock** | Android drops incoming multicast unless an app holds a lock; `ZeroconfMdnsBrowser` takes one only for the duration of a browse |
| `ACCESS_WIFI_STATE`, `ACCESS_NETWORK_STATE` | Detect "on Wi-Fi vs mobile data" and network changes (rediscovery trigger) |
| `VIBRATE` | Haptic tick when a switch changes |
| Min API 24 | The .NET 11 floor |

### iOS (no choices block it; not built here)
| `Info.plist` key | Why |
|---|---|
| `NSBluetoothAlwaysUsageDescription` | Required before CoreBluetooth can scan |
| `NSLocalNetworkUsageDescription` | iOS 14+ asks before the app talks to the local network |
| `NSBonjourServices` = `_iotstarter._tcp` | iOS only lets apps browse service types declared here |
| `NSAppTransportSecurity / NSAllowsLocalNetworking` | Allows plain HTTP to local addresses |

iOS blocks UDP **broadcast** and raw multicast unless Apple grants the `com.apple.developer.networking.multicast` entitlement. Without it, discovery on iOS relies on the last known IP plus **Bonjour** (mDNS). The UDP probe fails quietly there.

### Windows
The app runs **unpackaged** (`WindowsPackageType=None`), which needs no capabilities. If you package it (MSIX), `Package.appxmanifest` already declares `<DeviceCapability Name="bluetooth"/>` and `privateNetworkClientServer`.

## 7. Manual test plan (acceptance tests on real hardware)

Before starting: factory-fresh device (flashed with `--masserase`), phone on the **2.4 GHz** Wi-Fi network you will provision, demo mode **off**.

| # | Steps | Expected |
|---|---|---|
| 1 | Power the device. Open the app → **Add**. | Within ~10 s the list shows `IOT-xxxx` with "4-channel switch" (briefly "Identifying…"). nRF Connect shows the service UUID and the name in ADV, and manufacturer data `FFFF 01 0100 xxxxxx` in the scan response |
| 2 | Select it, accept the pairing prompt, pick your network, enter the **correct** password, **Connect**. | Progress: sent → joining → finding. Success in ≤ 30 s, the LED goes solid, naming screen, then device controls. The device appears **Online** in Devices |
| 3 | Reset the device (hold BOOT 5 s), add it again with a **wrong** password. Then **Check details and try again** with the right one. | "Wrong Wi-Fi password" (not a generic error). The retry keeps the network and password and succeeds without restarting the device or the app |
| 4a | Unplug and replug the device. | It rejoins Wi-Fi; the LED goes slow-blink then solid, never two-blink (BLE never starts); Online in the app |
| 4b | In the router, give the device a different IP (DHCP reservation), restart it, then pull to refresh (or reopen the app). | The device is found again (Device → Address shows the new IP, "found by name" or "found by search") with no user action |
| 5 | Toggle each output on and off from the card and from the Device screen; measure GPIO 25/26/27/33. Close and reopen the app. | Pin levels follow (active-high). A haptic tick on each change. After reopening, the switches show the device's true state |
| 6 | Switch the router off for 10 minutes, then on. | The device keeps its credentials. After 5 min it shows three blinks (recovery window) and retries every 60 s. When the router returns it reconnects by itself and shows Online in the app after refresh |
| 7 | Hold BOOT 5 s. In the app, Device → **Forget on this phone** (or **Factory reset** while online). | The device returns to two-blink setup mode and is discoverable again. The device disappears from the app list |
| 8a | `curl http://<ip>/api/v1/outputs` (no header); `curl -H "ApiKey: wrong" …`; `curl -X PUT -H "ApiKey: <token>" -d '{"on":true}' http://<ip>/api/v1/outputs/9` | 401, 401, 404 |
| 8b | `curl -X PUT -H "ApiKey: <token>" -d 'garbage' …/outputs/1`; `-d '{"on":"yes"}'`; a 1 KB body | 400, 400, 413; the device keeps running |
| 8c | With nRF Connect, write random bytes and out-of-order chunks to Command (after pairing) | `Ack(0, InvalidFrame)` or nothing; the device keeps running |
| 9 | Turn Bluetooth off → Add. Deny the permission → Add. Put the phone on mobile data → Devices. | Specific screens: "Turn on Bluetooth" (opens settings), "Allow nearby devices" or "Open Settings", and the banner "You're on mobile data…"; devices show offline with "Why?" |
| 10 | Read the logs: the VS Debug output (firmware), `adb logcat`, and the VS Output window (app). | No passphrase or token anywhere. The firmware logs only the SSID and the passphrase **length** |

To read the token for test 8, use a debug build: set a breakpoint in `DeviceManager.AddProvisionedAsync`, or temporarily print `await SecureStorage.GetAsync("token.<ID>")`. Never ship code that logs it.

## 8. Security

**Baseline (implemented):**
- **Wi-Fi credentials** travel only over the BLE Command characteristic, which requires an encrypted link: LE Secure Connections, Just Works, no bonding. The app never stores or logs the password; it is kept in memory only during the wizard, so a retry can reuse it.
- **Per-device API token:** 32 random bytes generated by the app (`RandomNumberGenerator`). The device keeps it in its flash settings record; the app keeps it in `SecureStorage` (Android Keystore, iOS Keychain, Windows DPAPI). Every HTTP route except `/info` requires it. Factory reset erases it on the device; "Forget" erases it on the phone.
- **Recovery window:** new credentials are accepted only with the existing token (constant-time comparison on the device), so a stranger cannot take the device over during a router outage.
- **Pairing strength:** Just Works protects against passive eavesdropping, not against an active attacker during the few seconds of pairing. The stronger option the hardware allows is a **static passkey printed on each device's label**: the device declares `DisplayOnly` and accepts that passkey in `PairingRequested`, as `BluetoothLESample2` does. Numeric comparison would need a display.

**Plain HTTP is observable on the LAN.** Anyone on the same network can capture the token and replay requests. There are two hardening options, described here but **not built**:

| | A. HTTPS on the device | B. Signed requests (HMAC) |
|---|---|---|
| **How** | `nanoFramework.WebServer` supports `HttpProtocol.Https` with a certificate (mbedTLS). Generate a per-device key pair and self-signed certificate at provisioning; send its SHA-256 fingerprint to the app over the encrypted BLE link; the app **pins** that fingerprint (no CA needed) | The token never travels again. Each request carries `timestamp`, `nonce` and `HMAC-SHA256(token, method‖path‖timestamp‖nonce‖SHA-256(body))`. The device checks the MAC and rejects stale timestamps and replayed nonces |
| **Protects** | Confidentiality and integrity of everything, including output states | Integrity and replay; responses stay readable on the LAN |
| **RAM / flash** | Each TLS session costs roughly 35–45 KB of internal heap during the handshake. On a no-PSRAM WROOM-32 with Wi-Fi up, that means **one connection at a time**, and BLE cannot run alongside. Certificate generation on the device takes several seconds | A few hundred bytes. SHA-256 and HMAC must be available on the device (verify whether a nanoFramework 1.x crypto package exposes them, or add a small managed SHA-256) |
| **Latency** | +200–800 ms per new connection (handshake) unless connections are kept alive | Negligible |
| **Complexity** | Certificate lifecycle, pinning in `DeviceApiClient`, plus a MAUI `HttpClientHandler` callback per platform | Clock tolerance (the device has SNTP when online), a nonce cache, and a shared canonicalisation spec |
| **Recommendation** | For devices with PSRAM, or when output state is sensitive | The best value on this hardware |

## 9. Known limitations

- **Not run on hardware in this repository's development:** firmware deployment, BLE with real phones, and RAM headroom. See "Open items to verify".
- **Scan results show no security type:** nanoFramework 1.x's `WifiAvailableNetwork` has no security field, so the device sends `Unknown` and the app always offers a password field ("leave empty for an open network").
- **Wrong-password detection:** the ESP32 maps `AUTH_FAIL`, handshake timeouts, `ASSOC_FAIL` and `BEACON_TIMEOUT` to "invalid credential". A very weak signal can therefore occasionally show as "Wrong Wi-Fi password".
- **Advertising:** the 31-byte ADV packet holds Flags, the 128-bit UUID and the name; the model block is in the scan response. A passive scanner sees only the name.
- **Stale pairing keys:** the device does not bond, but some phones keep keys anyway. If pairing fails after a factory reset, remove `IOT-xxxx` from the phone's Bluetooth settings (the wizard says so).
- **Low-RAM fallback** (`VerifyAfterReboot`): the device cannot scan during setup (the user types the SSID). A failed join is reported only after the device restarts into setup mode; the wizard reconnects and shows the reason.
- **Wrong method on a known HTTP path** returns 404, not 405 (a `nanoFramework.WebServer` limitation). Request bodies are limited to 256 bytes.
- **iOS** is configured but not built or tested here (it needs a Mac). Without Apple's multicast entitlement, UDP discovery is unavailable on iOS.
- **Ant Design Blazor** runs on a vendor prerelease built for .NET 11 (see section 5).
- **Firmware updates (OTA), cloud access, MQTT and WPA-Enterprise** are out of scope.

### Open items to verify on real hardware
1. Plugin.BLE 3.2.1 at runtime on .NET 11 (Android CoreCLR and Windows): scan filter, MTU request, pairing triggered by the protected Result subscription and Command write.
2. Whether `EncryptionRequired` on the notify-only Result characteristic also protects its CCCD, so subscribing triggers pairing.
3. Whether advertising resumes after a phone disconnects. The firmware re-advertises after 500 ms if the status is not `Started`.
4. Free managed and native heap with BLE, Wi-Fi, HTTP and mDNS up (the firmware logs `MEM` lines at each stage). If tight, set `EnableMdns = false` or `Strategy = VerifyAfterReboot`.
5. Zeroconf's Bonjour path on iOS 17+.
6. Whether a software reboot after BLE shutdown always comes up cleanly (otherwise set `RestartViaDeepSleep = true`).

## 10. Logs

- **Firmware:** the Visual Studio Debug output, with lines like `[12s INF] Wi-Fi: joining 'Home-WiFi' (passphrase length 12)` and `[13s MEM] after BLE start: managed free=… native internal free=…`.
- **App:** the Debug output (DEBUG builds) and `adb logcat -s DOTNET`.
