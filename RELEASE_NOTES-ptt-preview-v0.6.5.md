# Flex Control Companion v0.6.5 — PTT Audio Preview 1

**Preview / pre-release, not a production-tested firmware workaround.** This build adds a permanent UI feature for monitoring physical PTT and automatically retaining the selected PC microphone **only on compatible radio firmware that explicitly advertises a controllable PTT Override setting**.

## New

- Windows WPF: **PTT & Mic** tab on both Radio A and Radio B.
- Raspberry Pi ARM64 Avalonia: **PTT / MIC** tab on both radio slots.
- Raspberry Pi native Linux (Python/Qt): corresponding source changes available in the tagged source tree; this release includes prebuilt ARM64 Avalonia rather than a compiled native Qt package.
- **Automatically preserve PC microphone when rear PTT is used** enabled by default, saved in per-slot preferences.
- Monitor `transmit mic_selection`, `interlock source` (RCA/ACC/MIC), radio transmit state, and any advertised `ptt_override` status.
- Attempt override OFF only while radio is idle, only when a real override status parameter has been observed; show failed commands rather than claiming success.
- No software MOX, TX ownership seizure, PA safety-interlock modification, or SD-card firmware patch.

## Known limitation — your 4.2.20 radio

**SmartSDR 4.2.20's RCA/PTT-to-PC-mic audio routing remains unchanged.** Its existing hardware PTT audio switch is enforced by radio firmware, and an external companion application cannot yet turn it off using a verified command. FCC on 4.2.20 provides diagnostics and an explicit compatibility message, **not a working fix**.

FlexRadio documents the option for SmartSDR 4.3, but the exact TCP command for third-party clients is not yet verified. The provisional command adapter in this build must be tested against actual supported firmware before calling the function fully operational.

## Installing

**Windows 10/11 x64:** Download `FlexControlCompanion-v0.6.5-ptt-preview.1-windows-x64.zip`, extract into a new folder and run `FlexCompanion.exe`. Keep your existing version available in a separate folder. This is a self-contained .NET 8 WPF build.

**Raspberry Pi / Linux ARM64:** Download `FlexControlCompanion-v0.6.5-ptt-preview.1-pi-arm64.tar.gz`, extract to a new folder and run `./FlexCompanion` on a compatible graphical ARM64 Linux desktop. This is the .NET 8 Avalonia build.

## Test

1. Connect FCC to the FLEX radio and ensure SmartSDR or AetherSDR is operating the station.
2. Select PC microphone in the radio GUI, then open **PTT & Mic** (or **PTT / MIC**).
3. Press/release the rear-panel RCA PTT while safely connected to a dummy load at low power. Confirm FCC reports the RCA PTT source and does not seize TX control.
4. If the radio doesn't advertise a PTT Override property, the panel must say **unsupported/not advertised**.
5. Confirm audio modulation independently. PTT detection alone is not evidence that the radio is using PC mic audio.

## Source and builds

Source branch: `release/ptt-preview-v0.6.5` (copied from feature PR #8).
PR under review: https://github.com/Craig-M-M9XDT/FlexCompanion/pull/8
Compatibility details: `docs/ptt-audio-auto.md`

Built from this release branch using Windows and Linux ARM64 CI. SHA-256 checksums are provided as a release asset.
