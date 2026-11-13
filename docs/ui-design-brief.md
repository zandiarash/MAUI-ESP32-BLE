# IoT Starter: UI design brief

Status: **approved 2026-11-13** (teal palette, "Add" as the middle tab, English with RTL support) and implemented. Right-to-left layouts can be checked with Settings → Language → "English (right-to-left preview)".

## 1. Visual system (design tokens)

The tokens are defined once as CSS custom properties (`--iot-*`) in `wwwroot/css/tokens.css`. Two blocks hold them: `[data-theme=light]` and `[data-theme=dark]`.

**How they reach Ant Design.** Ant Design Blazor 1.6.2 has no v5-style token API. It ships two things: `ant-design-blazor.variable.css`, which exposes about 600 `--ant-*` variables (mostly primary and status colours), and a separate full `ant-design-blazor.dark.css`.

- **Light theme:** load `variable.css` and map `--ant-primary-color`, `--ant-success-color` and the rest onto our tokens.
- **Dark theme:** swap to `dark.css`, then apply the same mapping plus a small override layer (`antd-overrides.css`) for primary-tinted components (Switch, Button, Steps, focus rings), because `dark.css` hard-codes its blue.
- **"Follow system":** a `matchMedia('(prefers-color-scheme: dark)')` listener sets `data-theme`.

### Palette

| Token | Light | Dark | Use |
|---|---|---|---|
| `--iot-primary` | `#0F766E` (teal 700) | `#2DD4BF` (teal 400) | Brand, primary buttons, switches on, links |
| `--iot-primary-soft` | `#CCFBF1` | `#134E4A` | Selected rows, icon tiles |
| `--iot-bg` | `#F6F7F9` | `#0B0F14` | App background |
| `--iot-surface` | `#FFFFFF` | `#151B23` | Cards, sheets |
| `--iot-surface-raised` | `#FFFFFF` + shadow | `#1C2430` | Drawers, bottom nav |
| `--iot-border` | `#E4E7EC` | `#2A3441` | Dividers, card borders |
| `--iot-text` | `#101828` | `#F2F4F7` | Primary text |
| `--iot-text-2` | `#475467` | `#98A2B3` | Secondary text |
| `--iot-text-3` | `#667085` | `#7A8699` | Captions, disabled |
| `--iot-online` | `#12B76A` | `#32D583` | Online dot |
| `--iot-offline` | `#98A2B3` | `#667085` | Offline dot |
| `--iot-warning` | `#B54708` | `#FDB022` | Warnings, unknown model |
| `--iot-danger` | `#D92D20` | `#F97066` | Errors, factory reset |

All text and icon pairs meet WCAG AA, for example `#0F766E` on white is about 5.5:1 and `#2DD4BF` on `#0B0F14` is about 10:1. The palette is deliberately not the Ant Design default blue, so the app has its own identity. **Swap point:** change `--iot-primary` and `--iot-primary-soft`, and everything follows.

### Type, spacing, shape and motion

- **Font:** the system stack (`-apple-system, "Segoe UI", Roboto, "Noto Sans", "Noto Sans Arabic", sans-serif`), so nothing is downloaded and RTL scripts render natively.
- **Type scale (size/line height, px):** caption 12/16 · small 14/20 · **body 16/24** (16 keeps iOS from zooming into inputs) · title 18/26 (600) · page title 22/28 (600) · hero 28/36 (600, only on wizard result screens).
- **Spacing (4-pt scale):** 4 · 8 · 12 · 16 · 20 · 24 · 32 · 40. The page gutter is 16, card padding is 16, and the gap between sections is 24.
- **Radius:** 8 for controls · 14 for cards · 20 for the top corners of sheets and drawers · 999 for pills and status dots.
- **Touch:** every tap target is at least 44 × 44 px. Buttons use AntD size `Large`, list rows are at least 56 px tall, and switch rows toggle on a tap anywhere in the row.
- **Motion:** 150 ms for presses and toggles, 200 ms for step and route transitions (fade plus an 8 px slide), 250 ms for drawers. Easing is `cubic-bezier(.2,0,0,1)`. All motion is off under `prefers-reduced-motion`.
- **App feel:**
  - No text selection on controls, no overscroll bounce or pinch zoom.
  - `-webkit-tap-highlight-color: transparent`, and no hover-only affordances.
  - Fixed-height skeletons, so loading never shifts the layout.
  - Safe-area insets via `env(safe-area-inset-*)`.
  - A haptic tick on every switch change.
- **RTL and localisation:**
  - All strings come from `Resources/Strings.resx` through `IStringLocalizer`.
  - `<html dir>` follows the culture, and `ConfigProvider` sets the AntD direction.
  - CSS uses logical properties only (`margin-inline-start`, `inset-inline-end`), and directional icons (chevrons, back) are mirrored.

## 2. Layout

- **Phone (< 768 px):** one column, a top bar with the page title and contextual action, and a **bottom navigation** with three items: Devices · Add · Settings. This bar is custom-built, since AntD has no mobile tab bar.
- **Desktop window (≥ 768 px):** the same three items as a left rail, with content capped at 720 px wide and centred. The screens themselves are the same.
- **Modals:** small modals are not used on phones. Pickers and confirmations open as **bottom drawers** (`Drawer Placement="bottom"`), and the wizard uses **full-screen steps**.

## 3. Screens: AntD components and wireframes

### Devices (home)
Components: `Card`, `Switch`, `Badge` (status dot), `Tag` ("Unknown model"), `Skeleton`, `Empty`, `Alert` (network banner), `Button`, `MessageService` (toasts). Pull-to-refresh is a small custom component (touch events through JS interop), because AntD has none.

```
┌──────────────────────────────────┐
│ Devices                    ⟳     │  top bar (⟳ = refresh on desktop)
├──────────────────────────────────┤
│ ⚠ You're on mobile data. Connect │  Alert, only when not on Wi-Fi
│   to your home Wi-Fi to control  │
│   your devices.                  │
│ ┌──────────────────────────────┐ │
│ │ [▦] Kitchen          ● Online│ │  Card: model icon tile, name, status dot
│ │     4-channel switch         │ │
│ │ Ch 1 [■ ] Ch 2 [ □] Ch 3 [ □]│ │  quick toggles (wrap; 44 px targets)
│ │ Ch 4 [ □]                 ›  │ │  › opens device screen
│ └──────────────────────────────┘ │
│ ┌──────────────────────────────┐ │
│ │ [▦] Garage          ○ Offline│ │  offline: toggles disabled, hint line:
│ │ Not found on this network  › │ │  "Not found on this network"
│ └──────────────────────────────┘ │
├──────────────────────────────────┤
│  ▦ Devices    ＋ Add    ⚙ Settings│  bottom nav
└──────────────────────────────────┘
```

| State | Design |
|---|---|
| Loading | Two card skeletons of the exact card height |
| Empty | Illustration, "No devices yet", "Add your first device to control it from here", primary "Add device" |
| Error / unauthorised | The card shows "This phone is no longer allowed to control it. Set it up again." with "Forget" |
| Offline | Dot plus "Not found on this network"; tap shows the two causes (other network, client isolation) |

### Add device (full-screen wizard)
Components: `Steps` (compact, top: Find · Wi-Fi · Connect · Done), `List` with `Avatar`, a custom signal-bars icon, `Spin`, `Input`, `InputPassword` (show/hide), `Form` validation, `Alert` (2.4 GHz note), `Steps` (vertical, live progress), `Result`, `Button`. Close (✕) asks before abandoning a running join.

```
 1 Find nearby                    2 Choose Wi-Fi                  3 Connecting
┌──────────────────────┐        ┌──────────────────────┐        ┌──────────────────────┐
│ ✕  Add a device      │        │ ‹  Wi-Fi for Kitchen │        │    Connecting…       │
│ ●○○○                 │        │ ○●○○                 │        │ ○○●○                 │
│ Looking for devices… │        │ ⓘ Your device needs  │        │ ✓ Sent Wi-Fi details │
│ ┌──────────────────┐ │        │   a 2.4 GHz network. │        │ ◌ Joining Home-WiFi  │
│ │[▦] IOT-E5F6 ▂▄▆  │ │        │ Networks it can see ⟳│        │ ○ Finding it on your │
│ │ 4-channel switch │ │        │ ◉ Home-WiFi   ▂▄▆█   │        │   network            │
│ ├──────────────────┤ │        │ ○ Office      ▂▄▆    │        │                      │
│ │[?] IOT-42D8 ▂▄   │ │        │ ○ Other (hidden)…    │        │                      │
│ │ Unknown model ⚠  │ │        │ Password  [•••••] 👁 │        │                      │
│ └──────────────────┘ │        │ Leave empty for an   │        │                      │
│ Can't see yours?     │        │ open network.        │        │                      │
│ Hold its button 5 s… │        │ [ Connect          ] │        │ [ Cancel           ] │
└──────────────────────┘        └──────────────────────┘        └──────────────────────┘
```

| Step | States |
|---|---|
| Find | Blocked: Bluetooth off ("Turn on Bluetooth" plus a button that opens settings) · permission denied ("Allow nearby devices", or "Open settings" if denied permanently) · none found after 15 s (tips, including "On Android 11 and older, location must be on") · pairing prompt ("Tap *Pair* if your phone asks") |
| Wi-Fi | Skeleton list while the device scans · scan failed ("Couldn't list networks", Retry, or type the name) · hidden SSID entry · inline validation (too short, too long, unsupported characters) · recovery window ("This device already belongs to you. Enter the new Wi-Fi password.") |
| Connecting | Live, driven by State notifications, with Cancel |
| Done | `Result` success, then the name field (pre-filled), then "Open controls" |
| Failure | `Result` with a specific title, advice and a retry that keeps the input (table below) |

| Failure | Title / advice |
|---|---|
| Wrong password | "Wrong Wi-Fi password". Check upper and lower case and try again |
| Network not found | "Can't find *Home-WiFi*". Move closer to the router; the network must be 2.4 GHz |
| No IP | "The router didn't give it an address". Restart the router, or check its DHCP and MAC filter |
| Unreachable | "Connected, but your phone can't reach it". Your phone may be on a different network, or the router may isolate wireless clients (guest Wi-Fi or "AP isolation") |
| Not owner | "This device is set up with another phone". Factory reset it (hold the button 5 s) |

### Device
Components: `List` (switch rows), `Switch` (with `Loading` while pending), `Descriptions` (model, firmware, IP, found via), `Button`, `Drawer` (bottom) for rename and destructive confirmations, `Input`.

```
┌──────────────────────────────────┐
│ ‹  Kitchen                   ⋯   │  ⋯ = rename (bottom drawer)
│    ● Online · 192.168.1.41       │
├──────────────────────────────────┤
│ Channel 1                  [■ ] │  56 px rows; pending = spinner in knob;
│ Channel 2                  [ □] │  rollback + toast "Couldn't switch
│ Channel 3                  [ □] │  Channel 2. Check it's powered."
│ Channel 4                  [ □] │
├──────────────────────────────────┤
│ About                            │
│ Model      4-channel switch      │
│ Firmware   1.0.0                 │
│ Address    192.168.1.41 (mDNS)   │
├──────────────────────────────────┤
│ [ Identify (blink LED) ]         │
│ [ Restart ]                      │
│ [ Forget on this phone ]         │  bottom-drawer confirm
│ [ Factory reset ]  (danger)      │  bottom-drawer confirm, explains effect
└──────────────────────────────────┘
```

| State | Design |
|---|---|
| Loading | Row skeletons |
| Offline | Switches disabled, banner "Not found on this network", Retry |
| Unknown model | Rows come from the device's own `/outputs` |

### Settings
Components: `Segmented` (Light · Dark · System), `List`, `Drawer` (language picker), `Switch` (Demo mode, with an explanation), `Typography`.

```
┌──────────────────────────────────┐
│ Settings                         │
│ Appearance  [Light|Dark|System]  │
│ Language    English           ›  │
│ Demo mode                  [ □]  │
│ Try the app without hardware.    │
│ About       Version 1.0 · Help › │
└──────────────────────────────────┘
```

## 4. Questions for approval
1. **Brand colour:** is teal (`#0F766E` / `#2DD4BF`) acceptable, or do you have a brand colour?
2. **Bottom navigation:** keep "Add" as a middle tab, or use a floating "＋" on Devices instead (the alternative)?
3. **Languages:** English only for now (all strings localisable, layouts RTL-checked with a pseudo-RTL test culture)? Name any language you want shipped.
