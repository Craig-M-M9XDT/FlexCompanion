<p align="center">
  <img src="Assets/FlexCompanionIcon.png" alt="Flex Companion app icon" width="180" />
</p>

# Flex Companion

Created by **Craig Magee M9XDT**.

Flex Companion is a compact companion for FLEX-6000, FLEX-8000 and Aurora radios. The main Windows build uses WPF, while the `Pi/` project provides a responsive Avalonia/.NET 8 build for Raspberry Pi OS and Linux ARM64. It runs alongside a normal FLEX GUI client, connects through the FLEX network API, and provides DSP controls, metering, spectrum/AGC tools and native station-control functions.

## Raspberry Pi / touch build

A responsive **Avalonia/.NET 8 ARM64** build is available under `Pi/`. It is designed for Raspberry Pi 4/5 and touchscreen controllers, with automatic layout breakpoints from **800×480** through desktop resolutions, automatic touch-target scaling after the first touch event, optional `--touch` forcing, and `--kiosk` full-screen mode.

On compact displays the UI switches to large `RADIO A`, `RADIO B` and `STATION` tabs while both radio sessions can remain connected. The Pi build keeps the v0.6.2 receive-passband-centred spectrum behaviour, source-paced metering, Aether shared-pan preference with DAX-IQ fallback, Network Saver and Best AGC-T. See [`Pi/README-PI.md`](Pi/README-PI.md) for ARM64 publish, install and autostart instructions.

## Meter layouts

Each radio slot has four persistent meter layouts:

- **Simple Analogue** — a wide automatic RX S-meter / selectable TX meter with moving-needle ballistics and delayed-release peak hold.
- **Digital Select** — RX, power and SWR bars plus one selectable TX measurement.
- **Multi Analogue** — one wide compound dial with three colour-coded needles and independent peak markers. Each needle has its own scale and can independently show Power, SWR, Processor, Mic, Vdd, Current or PA Temperature.
- **Digital Multimeter** — RX plus all seven TX measurements together in a compact eight-tile display.

Meter presentation remains driven by incoming FLEX VITA meter packets. Updates are coalesced to the latest packet, and optional TX telemetry is subscribed only when the selected layout displays it.

## Spectrum performance / Aether shared-pan mode

Flex Companion now uses an **AUTO spectrum source**. When a locally patched AetherSDR instance is available, Companion reuses the same radio-generated panadapter FFT frames Aether is already receiving. Companion creates **no extra DAX IQ stream and performs no local FFT** in that mode. The compact view re-slices the existing pan frame to the selected slice, following the same basic architecture as Aether's mini-pan.

If no Aether bridge frame is available, Companion waits briefly and automatically falls back to its existing DAX-IQ spectrum worker. The fallback remains latest-frame-only and runs FFT work off the UI dispatcher.

The compact renderer is trace-only by default (Aether-style). On Windows the analogue meter caches its static face so display-refresh updates redraw only the moving needle; the Pi presentation uses a lightweight linear meter. These changes are aimed at smooth dual-radio operation rather than raw CPU throughput.

The optional Aether source patch is in `AetherBridgePatch/`. It mirrors `RadioModel::panFeedSpectrumReady` to **localhost UDP 7331 only** and does not create another radio pan, slice, DAX assignment or radio stream.

## Radio features

- NR, NB, ANF, NRF, NRL, NRS, RNN, ANFL and ANFT controls, including levels where the radio exposes one.
- Contextual DSP error/status messages distinguish unsupported hardware/firmware from temporary mode/state restrictions (for example DIGU notch-filter restrictions), while retaining the raw FLEX error code.
- Noise-floor scaling, ESC/diversity controls and CW auto tune.
- One automatic RX/TX meter: RX S-meter while receiving, then the selected TX measurement while transmitting, then back to RX. Analogue/digital presentation is selectable in the Windows build.
- Optional compact spectrum with selectable width (3–192 kHz), centred on the selected receive passband with a carrier marker. USB/LSB and digital modes are offset to their filter centre; AM/FM stay symmetrical, and narrow selections expand when needed to keep the whole filter visible. It prefers Aether shared-pan data and falls back to DAX IQ.
- Best AGC-T calibration with slice-specific audio analysis.
- LAN discovery plus manual IP entry.
- Two independent radio slots (A/B).
- Native radio DVK controls are intentionally not exposed in this build because current radios can refuse DVK commands from a bound non-GUI companion client. A future local Voice Macros feature can provide independent PC-side recording/playback without taking a GUI-client slot.
- Windows: custom wallpaper, always-on-top and Aether-inspired dark presentation.
- Raspberry Pi/Linux: responsive Avalonia presentation with touch and kiosk modes.

## Native station tools — no FRStack required

The old FRStack REST window has been removed. Its day-to-day station-control role is now implemented directly in Companion:

- quick band and mode buttons;
- ATU start/bypass, TUNE and MOX;
- global/TX/mic profile loading;
- configurable multi-command FLEX macros;
- raw FLEX API command box for advanced functions;
- Telnet DX Cluster client (DX Spider / AR-Cluster / CC Cluster), live spot list and click-to-tune;
- optional forwarding of cluster spots to the FLEX radio with `spot add`, so attached GUI clients can render them too;
- FLEX-reported amplifier discovery and OPERATE/STANDBY control through `amplifier set <handle> operate=...`;
- direct Power Genius XL LAN telemetry on TCP 9008 for power, SWR and available PA telemetry.

The station panel can target radio slot A or B independently.

## Network saver / dual-radio performance

The **Network saver** toggle is intended for two-radio operation, Wi-Fi and VPN/ZeroTier. It caps a **Companion-owned fallback DAX-IQ stream** at 24 kHz and subscribes only to the FLEX meters Companion actually displays. Meter presentation remains source-paced so the S-meter does not become artificially sluggish. When the Aether shared-pan source is active, Network saver does not cap the requested mini-pan span because the bridge adds no radio/LAN stream.

## DAX / FFT / AGC-T

The mini spectrum first looks for the optional Aether shared-pan bridge. In that mode it reuses Aether's existing radio FFT bins and needs no DAX IQ channel. If Aether is not present or is not patched, Companion automatically falls back to a FLEX DAX IQ stream and its local FFT worker; the SmartDAX application itself is not required for that fallback.

Best AGC-T uses slice-specific receive audio rather than the RF S-meter because the AGC knee is observable in post-AGC audio level, not in the pre-AGC RF level. Temporary analysis streams are intended to be released when calibration ends.

## Feature-entitlement policy

Companion does **not** turn a generic SmartSDR+ subscription label into a blanket client-side feature gate. A missing or unknown feature-entitlement status therefore fails open: the control remains available and Companion sends the valid FLEX command.

The radio remains authoritative. If the radio explicitly reports a feature disabled or rejects a command, Companion shows that response; this policy does not bypass radio-enforced licensing or firmware capability checks.

This follows the distinction used in AetherSDR: some functions are genuinely entitlement-backed, while other runtime capabilities must be judged from what the hardware/firmware actually reports and accepts rather than from a generic subscription label.

## Optional Aether shared-pan bridge

Run `AetherBridgePatch/apply_aether_bridge.py` against an AetherSDR source checkout, then rebuild Aether normally. The patch is intentionally small and GPLv3-compatible. When Companion sees fresh frames matching the connected radio serial and selected pan stream, the status under the spectrum reads `Aether shared pan · radio FFT · no extra DAX IQ stream`.

Aether's mini-pan design reuses the active panadapter's already-streaming FFT bins rather than creating another radio object; Companion follows that principle through the localhost bridge. Without the patch, all other Companion functions continue to work and the spectrum falls back automatically.

## Windows build

Requires Windows and the **.NET 8 SDK**. No NuGet packages are currently required for the WPF project.

Run:

    build.bat

The published application is written to:

    publish\FlexCompanion.exe

You can also open `FlexCompanion.csproj` in Visual Studio 2022.

## Raspberry Pi build

Install the .NET 8 SDK on a Windows/Linux build machine and use the scripts under `Pi/` to publish the self-contained `linux-arm64` application. On Windows:

    cd Pi
    .\publish-pi.ps1

See `Pi/README-PI.md` for Linux/Pi publishing, runtime dependencies, `--touch`, `--kiosk` and autostart installation.

## First run

1. Allow Flex Companion through the local firewall on private networks where applicable.
2. Select a discovered radio and connect it to slot A or B, or enter its IP manually.
3. Select/follow the desired GUI station and slice in the radio panel.
4. Open the Station controls for amplifier, DX-cluster, profile, macro and quick-control functions.

Settings are stored under the platform application-data folder in `FlexCompanion/settings.json` (`%AppData%\FlexCompanion\settings.json` on Windows).

## GPL / AetherSDR attribution

Flex Companion is GPLv3 software. It incorporates/ports GPLv3 implementation ideas and code from AetherSDR, including station/peripheral protocol work. See `LICENSE` and `NOTICE.md`.

AetherSDR: https://github.com/aethersdr/AetherSDR

FLEX/SmartSDR names are used only to describe interoperability. Flex Companion is not an official FlexRadio product.

## Build status

Source-level consistency and XAML/AXAML parsing are checked during development, but final Windows and Raspberry Pi binaries should still be validated on the target OS/radio combination. When a control is refused, the amber status line contains the radio's returned error and is the most useful diagnostic to report.
