# Flex Control Companion — native Raspberry Pi / Linux build

A from-scratch rebuild of the Companion's Raspberry Pi app in **Python + Qt (PySide6)** with a
**C++ core** for the per-packet and per-frame work. It uses the same FLEX API logic as the .NET
build: discovery, the TCP command channel, VITA-49 meters and DAX IQ, the DSP control rules and
the passband-centred spectrum. It also shares the same settings file, so you can switch between
the two builds freely.

Why a native build: no .NET runtime, no single-file extraction, nothing architecture-specific to
download. The installer builds from source on the Pi, so it also runs on **32-bit** Pi OS (with
Trixie's PySide6), on any 64-bit Debian/Ubuntu desktop, and on x86 PCs.

## Install

On the Pi, from a terminal on the desktop:

```bash
curl -fsSL https://raw.githubusercontent.com/Craig-M-M9XDT/FlexCompanion/main/pi-native/deploy/install.sh | sudo bash -s -- --touch
```

Until this branch is merged into `main`, add `--ref pi-native-python`:

```bash
curl -fsSL https://raw.githubusercontent.com/Craig-M-M9XDT/FlexCompanion/pi-native-python/pi-native/deploy/install.sh | sudo bash -s -- --ref pi-native-python --touch
```

The installer:

1. installs the system packages it needs (Python, compiler, CMake, Qt runtime libraries);
2. makes a private Python environment in `/opt/flexcompanion-native`;
3. installs PySide6 — from apt on Trixie, or a Bookworm-compatible wheel from PyPI (newer
   PySide6 ARM64 wheels need glibc 2.39, which Bookworm doesn't have, so it pins `<6.8.1` there);
4. compiles the C++ core. If the compile fails, it installs the pure-Python version instead, which
   is slower but complete;
5. adds a menu entry and a `flexcompanion-native` command.

| Option | Effect |
| --- | --- |
| `--touch` | large touch controls (also switches on automatically at the first touch) |
| `--kiosk` | start full-screen |
| `--autostart` | start when the desktop logs in |
| `--ref <branch>` | install a branch or tag (default `main`) |
| `--from <dir>` | install from a local checkout of `pi-native/` |
| `--uninstall` | remove it; settings are kept |

For a dedicated touch panel, use `--touch --kiosk --autostart`.

**No radio to hand?** Run `flexcompanion-native --demo`. It starts a built-in simulated FLEX-6600,
so you can try the whole UI, including meters, the spectrum and the DSP buttons.

## What's in this version

- FLEX LAN discovery, plus manual IP or `IP:port`
- two independent radio slots (A/B) with automatic reconnect, and reconnect at start-up
- station and slice selection, with "follow active slice" and binding to the owning GUI station
- DSP controls: NR, NRF, NRL, NRS, RNN, NB, ANF, ANFL, ANFT, noise floor, DIV, ESC phase/gain.
  These use the same capability rules as the .NET build: controls fail open, unsupported
  features are marked when the radio says so, and the notch / RNN controls are greyed out in
  DIGx / RTTY / FDV / CW modes and restored afterwards
- source-paced RX S-meter / TX meter, subscribed only to the meters on screen (Power, SWR, Proc,
  Mic, Vdd, Current, Temp), with peak hold
- compact spectrum from a Companion-owned DAX IQ stream, centred on the receive passband
  (3–192 kHz), with filter shading, carrier marker and **tap-to-tune**
- Network saver (caps DAX IQ at 24 kHz for Wi-Fi / VPN / two radios)
- Station tab: bands, modes, ATU / BYP / TUNE / MOX, amplifier OPERATE / STANDBY, profile load,
  macros (`@mode`, `@band`) and a raw FLEX command box
- fits every official Raspberry Pi touchscreen in touch mode, windowed or `--kiosk` full-screen:
  the 7" Touch Display (800×480), and Touch Display 2 (720×1280 portrait, its default, or
  1280×720 when rotated). Below 1000 px wide, the radio list moves into a RADIOS tab; the
  sidebar scrolls; the window never opens bigger than the screen

Next phases, already present in the .NET code: Best AGC-T calibration, the DX cluster with
click-to-tune spots, Power Genius XL telemetry, and the Aether shared-pan bridge.

## Layout

```
pi-native/
  src/core/flexcore.cpp      C++ (pybind11): VITA-49 decode, IQ ring buffer + FFT, resampling
  flexcompanion/
    _core.py / _fallback.py  loads the C++ core, or the identical numpy implementation
    client.py                TCP 4992 command/status + UDP VITA receiver
    discovery.py             UDP 4992 radio discovery
    session.py               one radio slot (port of RadioViewModel)
    params.py meters.py spectrum.py station.py settings.py
    sim.py                   simulated FLEX radio (tests and --demo)
    ui/                      PySide6 window, radio and station panels, meter/spectrum widgets
  tests/                     40 tests: core parity, protocol logic, end-to-end vs the simulator, UI
  deploy/install.sh
```

All radio state lives on one thread, like the .NET dispatcher. Network threads post into it,
and blocking command sequences run on a small worker pool. IQ samples go from the UDP thread
straight into the C++ ring buffer, and a worker computes the FFT with the GIL released, so the
UI thread only draws.

| Per item (x86 test machine) | numpy | C++ core |
| --- | --- | --- |
| DAX IQ packet (512 pairs) | 18 µs | 3.5 µs |
| meter packet | 5.2 µs | 0.4 µs |
| 4096-point spectrum frame | 213 µs | 102 µs |

## Developing

```bash
cd pi-native
pip install PySide6-Essentials numpy pytest scikit-build-core pybind11
pip install --no-build-isolation -e .
QT_QPA_PLATFORM=offscreen pytest -q          # run from outside the folder to test an installed copy
python -m flexcompanion --demo               # app + simulated radio
flexcompanion-sim --host 0.0.0.0 --discovery # simulated radio visible to other machines
```

`FLEXCOMPANION_PURE_PYTHON=1` forces the numpy fallback. CI (`.github/workflows/pi-native.yml`)
runs the tests on x64 and ARM64 with both backends, and runs the installer in Debian Bookworm and
Trixie ARM64 containers.

## Troubleshooting

- **Nothing appears when started from the menu**: see `~/.cache/flexcompanion-native/last-run.log`.
- **Started over SSH**: the launcher attaches to the Pi's desktop session if someone is logged in.
- **No radios listed**: enter the radio's IP. Discovery needs UDP 4992 broadcasts from the same
  subnet.
- **Header says "numpy fallback"**: the C++ core didn't compile. Re-run the installer and check
  its output.
