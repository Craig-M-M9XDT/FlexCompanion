# Flex Companion — Raspberry Pi / touch build

This is the Avalonia UI port of Flex Companion v0.6.2. It keeps the same FLEX TCP/UDP protocol code and radio view-model behaviour as the Windows build, but replaces WPF with Avalonia so the application can run on Raspberry Pi OS 64-bit and other Linux ARM64 systems.

## Target

- Raspberry Pi 5 (recommended) or Pi 4
- Raspberry Pi OS 64-bit / Debian ARM64
- X11 or Wayland desktop session
- wired Ethernet recommended for dual-radio use
- .NET is bundled into the published app (`--self-contained true`), so the target Pi does not need the .NET runtime installed

Avalonia 12.x supports Raspberry Pi OS ARM64. The current project is pinned to Avalonia 12.1.3 and .NET 8. The Pi spectrum path includes the v0.6.2 receive-passband centring logic used by the Windows build.

## Touch + responsive UI

The UI is deliberately resolution-independent rather than tied to a 1920×1080 desktop:

- **>= 1080 logical px wide and >= 650 high:** desktop layout with sidebar and two radio columns.
- **below either breakpoint:** compact layout switches to `RADIO A`, `RADIO B`, and `STATION` tabs so controls stay large enough to use.
- **below 850×560:** micro layout reduces non-essential text while retaining large controls.
- the first actual **touch pointer event automatically enables Touch mode**; `--touch` or `FLEXCOMPANION_TOUCH=1` can force it at startup.
- Touch mode raises buttons, combo boxes, sliders, text boxes and tab headers to roughly **48 logical px** minimum height.
- `--kiosk` starts full-screen. The in-app Kiosk switch can enter/leave full-screen too.
- Avalonia uses the operating system DPI/scale factor as well, so a high-DPI 7-inch panel and a normal desktop monitor do not need separate XAML layouts.

Supported examples include 800×480, 1024×600, 1280×800 and 1920×1080. On the smallest displays, one radio occupies the screen at a time via tabs while both radio sessions remain connected and running.

## Build on Windows (cross-publish for Pi)

Install the .NET 8 SDK, open PowerShell in this `Pi` folder and run:

```powershell
.\publish-pi.ps1
```

Output:

```text
publish/pi-arm64/
```

Copy that complete folder to the Pi.

## Build on Linux / Raspberry Pi

With the .NET 8 SDK installed:

```bash
chmod +x publish-pi.sh
./publish-pi.sh
```

This publishes `linux-arm64`, self-contained, single-file, with native libraries embedded for extraction.

## Raspberry Pi runtime dependencies

For Raspberry Pi OS / Debian desktop, ensure the normal Avalonia Linux dependencies are present:

```bash
sudo apt update
sudo apt install -y libx11-6 libice6 libsm6 libfontconfig1
```

Most Raspberry Pi OS Desktop images already contain these.

## Run directly

```bash
chmod +x FlexCompanion
./FlexCompanion
```

Force touch controls:

```bash
./FlexCompanion --touch
```

Full-screen touch panel / kiosk:

```bash
./FlexCompanion --touch --kiosk
```

## Install on the Pi

From the source tree, copy `deploy/install-pi.sh` beside or point it at the published folder:

```bash
sudo ./deploy/install-pi.sh ./publish/pi-arm64 --touch
```

For login autostart:

```bash
sudo ./deploy/install-pi.sh ./publish/pi-arm64 --touch --autostart
```

For a dedicated full-screen panel:

```bash
sudo ./deploy/install-pi.sh ./publish/pi-arm64 --touch --kiosk --autostart
```

## Touch keyboard

Flex Companion uses ordinary Avalonia text boxes, so touch keyboard behaviour is controlled by the Raspberry Pi desktop environment. If your image does not supply an on-screen keyboard, install/enable one such as `squeekboard` or `wvkbd` at OS level.

## Functional scope in this port

The Pi UI uses the existing Companion radio/session implementation, including:

- FLEX LAN discovery + manual IP
- two independent radio slots
- station/slice selection
- source-paced S-meter/TX meter updates
- NR/NB/ANF-family controls
- ESC/Diversity controls
- CW auto-tune controls
- Best AGC-T logic
- compact FFT with Aether shared-pan preference and DAX-IQ fallback
- Network Saver
- quick band/mode/ATU/TUNE/MOX station controls
- native station back-end code remains in the project (DX cluster / PGXL), ready for additional touch panels

The Pi presentation intentionally uses a low-overhead linear meter and trace-only spectrum rather than the WPF analogue dial/waterfall. This keeps touch/input latency predictable on an ARM board.

## Licence / provenance

Flex Companion remains GPLv3. AetherSDR-derived/ported portions retain the acknowledgement in the repository root `NOTICE.md`.
