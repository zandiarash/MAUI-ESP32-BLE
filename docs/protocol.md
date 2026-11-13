# IotStarter protocol specification

Protocol version **1** · HTTP API version **1**.

This document and `src/IotStarter.Shared` must change together. Every constant below has a name in `ProtocolConstants`, `ProtocolCodes`, `HttpContract` or `ModelCatalog`. All multi-byte integers are **little-endian**.

---

## 1. Identity

| Term | Definition | Example |
|---|---|---|
| Device ID | The ESP32 Wi-Fi station MAC address (6 bytes), written as 12 uppercase hex digits | `A1B2C3D4E5F6` |
| Short ID | The last 3 bytes of the device ID | `D4E5F6` |
| BLE name | `IOT-` + the last 4 hex digits | `IOT-E5F6` |
| Host name | `iot-` + the device ID in lowercase (DHCP and mDNS) | `iot-a1b2c3d4e5f6.local` |

The device ID is the only identity the app relies on. IP addresses change with DHCP, and iOS hides BLE addresses. The same ID appears in the advertisement (short form), the Info characteristic, the UDP discovery reply, the mDNS TXT record and `GET /info`.

## 2. BLE advertising

nanoFramework 1.x supports legacy advertising only: 31 bytes of advertising data plus a 31-byte scan response.

**Placement rule (from the library source).** `BluetoothLEAdvertisement.CreateLegacyAdvertisements` places data sections in order and moves any section that does not fit into the scan response. It always re-appends manufacturer data at the end. So the device builds a custom advertisement (`CustomAdvertisement = true`), and the packets come out as:

| Packet | AD type | Content | Bytes |
|---|---|---|---|
| ADV | `0x01` Flags | `0x06` (LE General Discoverable, BR/EDR not supported) | 3 |
| ADV | `0x07` Complete list of 128-bit UUIDs | service UUID | 2 + 16 = 18 |
| ADV | `0x09` Complete local name | `IOT-E5F6` | 2 + 8 = 10 |
| | | **ADV total** | **31 / 31** |
| SCAN RSP | `0xFF` Manufacturer specific | company `0xFFFF`, then version (1), model ID (2), short ID (3) | 2 + 2 + 6 = 10 |
| | | **Scan response total** | **10 / 31** |

The manufacturer payload is built by `MessageCodec.EncodeManufacturerPayload`.

Consequences:
- A passive scanner sees the UUID and the name only. Android, iOS and nRF Connect all scan actively, so they also receive the scan response.
- The app merges the scan response with the advertisement for each peripheral. Until the manufacturer data arrives, it identifies the device from the name suffix and shows the model as "identifying…".
- **Company ID `0xFFFF`** is the Bluetooth SIG test value. Replace it with an assigned ID before shipping.

## 3. GATT service

The primary service UUID is `54173fa7-8b86-4bf4-acd4-a120112bf9e3`.

| Characteristic | UUID | Properties | Protection | Value |
|---|---|---|---|---|
| Info | `cd51670e-5dc7-4022-8df5-01e6abf15b43` | Read | Plain | Fixed 14 bytes (§3.1) |
| State | `6e4e8482-ec16-42f9-bf02-0e892e1af341` | Read, Notify | Plain | Fixed 6 bytes (§3.2) |
| Command | `5f3311b1-56db-42d6-bd50-2f07bad65599` | Write | **Encryption required** | Chunked frames (§4) |
| Result | `b6474bd8-690e-4bf4-8fa3-2c303a31ee4e` | Notify | **Encryption required** | Chunked frames (§4) |

The first write to Command makes the phone pair (LE Secure Connections, **Just Works**, IO capability `NoInputNoOutput`). The device sets `AllowBonding = false`, so no keys are stored on the device. See §8 for the stronger option.

Result is notify-only. Its CCCD write is also protected, so subscribing to it triggers pairing as well.

### 3.1 Info (14 bytes, read)

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | Protocol version (`1`) |
| 1 | 2 | Model ID |
| 3 | 6 | Device ID (MAC bytes in transmission order) |
| 9 | 3 | Firmware version: major, minor, patch |
| 12 | 2 | Capability flags |

Decoders accept values longer than 14 bytes so later versions can append fields.

**Capability flags**

| Bit | Name | Meaning |
|---|---|---|
| 0x0001 | `CanScan` | The device can scan Wi-Fi while BLE is up |
| 0x0002 | `TokenRequired` | Recovery window: SetCredentials must carry the existing API token |
| 0x0004 | `DeferredVerification` | Low-RAM strategy: credentials are verified after a reboot with BLE off |
| 0x0008 | `Mdns` | The device answers DNS-SD queries |
| 0x0010 | `ScanReportsSecurity` | Scan entries carry real open/secured information (not set by nanoFramework 1.x, see §4.4) |

### 3.2 State (6 bytes, read and notify)

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | State |
| 1 | 1 | Last error |
| 2 | 4 | IPv4 address (`0.0.0.0` until connected) |

**States**

| Value | Name | Meaning |
|---|---|---|
| 1 | Ready | Waiting for a command. A non-zero error byte explains the previous attempt |
| 2 | Scanning | A Wi-Fi scan is running |
| 3 | Connecting | Joining Wi-Fi with the received credentials |
| 4 | Connected | Joined; the IPv4 address is valid. The device reboots about 1.5 s later |
| 5 | Restarting | Fallback strategy: credentials stored unverified; reboot follows |

**Error codes** (also used in Ack and ProvisionResult)

| Value | Name | User-facing meaning |
|---|---|---|
| 0 | None | — |
| 1 | InvalidFrame | Chunk out of order, stale, bad CRC |
| 2 | InvalidCommand | Unknown message type or command not allowed now |
| 3 | InvalidPayload | Malformed or out-of-range fields |
| 4 | Busy | Another operation is running |
| 5 | NetworkNotFound | The SSID is not in range (or it is 5 GHz only) |
| 6 | WrongPassword | Authentication with the access point failed |
| 7 | NoIpAddress | Associated, but DHCP gave no address |
| 8 | Timeout | No answer from the access point in time |
| 9 | NotAuthorized | Recovery window: the token does not match |
| 10 | StorageFailed | Saving credentials or settings failed |
| 11 | UnsupportedSecurity | The network uses a security mode the device cannot use (for example WPA-Enterprise) |
| 12 | PreviousAttemptFailed | Fallback strategy: the post-reboot join failed (the specific reason replaces this when known) |
| 13 | Cancelled | Cancelled by the app |
| 255 | Internal | Unexpected device error |

## 4. Framed messages (Command and Result)

### 4.1 Chunks

Every GATT write to Command and every notification on Result is one chunk:

```
byte 0   : bit 7 = END (last chunk), bits 0..6 = chunk index (0..127)
byte 1   : message id (0..255, chosen by the sender, echoed in nothing)
byte 2.. : data (1 .. chunkSize-2 bytes)
```

- **Chunk size.** The default is **20 bytes**, which fits the default ATT MTU of 23. Each side may use up to `MTU − 3` (at most 244) when it knows the negotiated MTU: the app from its MTU request, the device from `GattSubscribedClient.MaxNotificationSize`. The receiver does not care about chunk size.
- **Reassembly rules** (`FrameAssembler`):
  - Index 0 always starts a new message and discards any partial one.
  - Any other index must equal the previous index + 1 and carry the same message id. Otherwise the message is rejected (`InvalidFrame`).
  - A gap of more than **3000 ms** between chunks discards the partial message.
  - A total size over **512 bytes** is rejected (`InvalidPayload`).
  - A chunk with no data is rejected.
- After reassembly the message envelope is checked (§4.2). On any rejection the device sends `Ack(cmd = 0, error)` and keeps running.

### 4.2 Message envelope

```
[type: 1][payload: n][crc16: 2]
```

The CRC is CRC-16/CCITT-FALSE (polynomial 0x1021, initial value 0xFFFF) over `type + payload`. It catches chunking bugs; link-layer integrity comes from BLE.

### 4.3 Commands (app → device, Command characteristic)

| Type | Name | Payload | Allowed in state |
|---|---|---|---|
| 0x01 | Identify | — | any |
| 0x02 | ScanNetworks | — | Ready |
| 0x03 | SetCredentials | see below | Ready |
| 0x04 | Cancel | — | Scanning, Connecting |

**SetCredentials payload**

| Field | Size | Rule |
|---|---|---|
| ssidLen | 1 | 1..32 |
| ssid | ssidLen | raw bytes (UTF-8 in practice) |
| passLen | 1 | 0 (open network) or 8..63 |
| passphrase | passLen | printable ASCII 0x20..0x7E (WPA2-Personal) |
| token | 32 | random API token generated by the app; must not be all zero |

The largest SetCredentials message is 1 + 1 + 32 + 1 + 63 + 32 + 2 = 132 bytes, which is 8 chunks at 20 bytes.

In the recovery window (`TokenRequired` set), the token must equal the device's stored token (constant-time comparison), or the device answers `NotAuthorized`.

### 4.4 Results (device → app, Result characteristic)

| Type | Name | Payload |
|---|---|---|
| 0x81 | Ack | `command (1)`, `error (1)`; sent for every command, and with `command = 0` for frame errors |
| 0x82 | ScanEntry | `rssi (int8 dBm)`, `security (1)`, `ssidLen (1)`, `ssid` |
| 0x83 | ScanDone | `count (1)` |
| 0x84 | ProvisionResult | `error (1)`, `ipv4 (4)` |

**Security byte:** 0 = open, 1 = secured, 2 = unknown. nanoFramework 1.x's `WifiAvailableNetwork` exposes SSID, BSSID, RSSI and signal bars, but **not the security type**, so firmware 1.x always sends 2 and does not set `ScanReportsSecurity`. The app therefore always offers the password field, with "leave empty for an open network".

Hidden networks never appear in scan results; the app lets the user type an SSID.

Duplicate SSIDs (several access points) are merged on the device, which keeps the strongest RSSI. At most 20 entries are sent, strongest first.

## 5. Sequences

### 5.1 Default strategy (BLE and Wi-Fi together)

```
App                                   Device
 |  scan (filter: service UUID)         |  advertising, LED provisioning pattern
 |<---------------- ADV + SCAN RSP -----|
 |  connect, request MTU 247            |
 |  read Info, read State               |
 |  subscribe State, subscribe Result --|--> pairing (Just Works, no bonding)
 |  Command: ScanNetworks ------------->|  state=Scanning
 |<---------------------- Ack(0x02, 0) -|
 |<------------------- ScanEntry × n ---|
 |<----------------------- ScanDone(n) -|  state=Ready
 |  Command: SetCredentials ----------->|  validate
 |<---------------------- Ack(0x03, 0) -|  state=Connecting
 |                                      |  WifiAdapter.Connect (≤ 20 s) + DHCP (≤ 15 s)
 |<---- State: Connected, ip -----------|  success: save Wi-Fi config + token
 |<---- ProvisionResult(None, ip) ------|
 |                                      |  wait 1.5 s, stop advertising, dispose BLE, reboot
 |  GET http://ip/api/v1/info  -------->|  (normal mode)
 |  confirm device ID, save device + token, open device screen
```

On failure the device sends `State: Ready, error = WrongPassword | NetworkNotFound | NoIpAddress | Timeout` followed by `ProvisionResult(error, 0.0.0.0)`. It stays in provisioning mode, so the app can resend SetCredentials without reconnecting.

### 5.2 Fallback strategy (`DeferredVerification`)

```
App                                   Device
 |  Command: SetCredentials ----------->|  validate, save Wi-Fi config + token, flag "unverified"
 |<---------------------- Ack(0x03, 0) -|
 |<---- State: Restarting --------------|
 |<---- ProvisionResult(None, 0.0.0.0) -|  wait 1.5 s, reboot (BLE never started)
 |  locate device on network (60 s) ... |  join Wi-Fi with BLE off
 |                                      |   ok   → clear flag, normal mode
 |                                      |   fail → erase Wi-Fi + token, lastError = reason, reboot
 |  (not found) rescan BLE, read State  |  provisioning: State = Ready, error = reason
```

### 5.3 Recovery window

In normal mode, after 5 minutes without Wi-Fi the device stores a "recovery window" flag and reboots into provisioning with `TokenRequired` set, keeping its credentials. The window lasts 5 minutes. In the default strategy the device also retries the stored network every 60 s while no phone is connected, and reboots to normal mode as soon as it gets an address. When the window ends, the flag is cleared and the device reboots into normal mode.

## 6. Timeouts

| What | Value |
|---|---|
| Chunk reassembly gap | 3 s |
| Device Wi-Fi association (native, inside `WifiAdapter.Connect`) | 20 s |
| Device DHCP wait after association | 15 s |
| Device Wi-Fi join overall | 30 s |
| Device Wi-Fi scan | 10 s |
| Final status → reboot | 1.5 s |
| App: BLE connect | 15 s |
| App: wait for ProvisionResult after SetCredentials | 60 s |
| App: HTTP request | 5 s (2 s for last-known-IP probe) |
| App: post-reboot rediscovery | 60 s |
| Device: offline before recovery window | 5 min |
| Device: recovery window length | 5 min |
| Factory reset hold | 5 s |

## 7. Local network API

Base URL: `http://<ip>:80/api/v1`. Bodies are JSON in UTF-8.

**Authentication.** Every route except `GET /info` needs the header `ApiKey: <token as 64 lowercase hex characters>`. The header name is fixed by `nanoFramework.WebServer`. A missing or wrong token gets **401**.

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| GET | `/info` | — | 200 `{"id","model","name","fw","api"}` | — |
| GET | `/outputs` | — | 200 `{"outputs":[{"id","label","on"}]}` | 401 |
| PUT | `/outputs/{id}` | `{"on":true}` | 200 `{"id","label","on"}` | 400 bad body, 401, 404 unknown id |
| PUT | `/name` | `{"name":"Kitchen"}` | 200 `{"name"}` | 400 (empty or > 32 bytes), 401 |
| POST | `/system/identify` | — | 204 | 401 |
| POST | `/system/reboot` | — | 202, then the device reboots | 401 |
| POST | `/system/factory-reset` | — | 202, then wipe and reboot | 401 |

Field types: `id` is a string on `/info` (device ID) and a number on outputs. `model` is a number (for example `1`). `fw` is a string such as `"1.0.0"`. `api` is a number. `on` is a boolean.

Bodies are limited to 256 bytes and must be flat JSON objects. Unknown fields are ignored. A missing required field, a wrong type, or anything that is not JSON gets 400. A wrong method on a known path gets 405.

**Example**

```
PUT /api/v1/outputs/2 HTTP/1.1
ApiKey: 3f9a…(64 hex)
Content-Type: application/json

{"on":true}

HTTP/1.1 200 OK
{"id":2,"label":"Channel 2","on":true}
```

## 8. Discovery

The app tries these in order and always confirms the device ID with `GET /info`:

1. **Last known IP**, with a 2 s timeout.
2. **mDNS / DNS-SD.** The device answers:
   - `PTR _iotstarter._tcp.local` → `iot-a1b2c3d4e5f6._iotstarter._tcp.local`
   - `SRV` (instance) → port 80, target `iot-a1b2c3d4e5f6.local`
   - `TXT` (instance) → `id=A1B2C3D4E5F6`, `model=1`, `api=1`
   - `A iot-a1b2c3d4e5f6.local` → IPv4 address
3. **UDP probe.** The app broadcasts `"IOTS?"` + version byte to `255.255.255.255:48731`. Each device replies to the sender's address and port with `{"id":"A1B2C3D4E5F6","model":1,"port":80,"api":1}`.

On iOS, broadcast and raw multicast need Apple's multicast entitlement. Discovery there uses Bonjour (`NSBonjourServices` = `_iotstarter._tcp`), so step 2 is the only automatic step on iOS without the entitlement.

## 9. Security notes

- **Wi-Fi credentials** travel only over an encrypted BLE link (the Command characteristic requires encryption). They are never logged by the device or the app.
- **Just Works** pairing protects against passive eavesdropping, not against an active man-in-the-middle during the pairing moment. The stronger option this hardware allows is a **static passkey**: a 6-digit number printed on each device's label, with the device declaring `DisplayOnly` and accepting that passkey in `PairingRequested` (as in BluetoothLESample2). Numeric comparison would need a display.
- **API token** is 32 random bytes from the app, stored on the device and in the app's SecureStorage, and erased on factory reset. Over plain HTTP, other machines on the same network can observe it (see the README for the hardening options).
