# Flex Control Companion — APD LAB development preview
**Version:** 0.6.5-apd.1 | **Platform:** Windows x64 | **Status:** TEST ONLY

This is a side-by-side portable preview for evaluating the **SmartSignal APD diagnostic dashboard**. It is **not** a production release, and has not been tested against a real FLEX-8000 or Aurora.

## Installation

1. Extract the GitHub Actions artifact ZIP and then extract the inner `FlexControlCompanion-APD-LAB-v0.6.5-apd.1-win-x64.zip` to a separate folder.
2. Run `FlexCompanion.exe`. The preview is self-contained; .NET need not be installed.
3. Do **not** replace an existing installed Companion executable. The preview uses a separate settings location: `%APPDATA%\FlexCompanion-APD-LAB\settings.json`.
4. Connect your APD-capable radio as usual and open the **APD** tab for that slot. A FLEX-6000 does not support APD; the dashboard may show unavailable.
5. When testing the feedback sampler, use only correctly isolated, rated hardware. The debug tool **does not protect RF inputs** from overload.

## Included functionality

- The separate APD window shows enable/configurable/equalizer-active, the radio's sampler list, and manual APD commands; reset/sampler apply are blocked while transmitting.
- Shows radio TX audio, ALC (only when actually provided), forward power, SWR, and PGXL output power/SWR when connected to the matching slot.
- Shows an **ESTIMATE** of feedback level from measured PGXL watts and operator-entered coupler/pad loss. **Changing these dB values does NOT change any hardware attenuation**.
- Shows the APD calibration identity from status fields `ant`, `freq`, `rfpower`; only labels it as matching the current TX settings when all fields and the TX slice context are available.
- Shows `sample_index`, `tx_error_mHz` and `rx_error_mHz` **when actually reported**; frequency error is **NOT ADC feedback power**.
- The new **Save APD status + meter snapshot** button exports local observations (raw APD status lines, dynamic meter inventory and raw meter samples) into a text file for analysis. It is a **read-only** collection feature.

## Deliberately NOT included

- No ADC feedback meter unless the actual meter or calibrated sampler level is identified.
- No IMD before/after claims without separately sampled and measured transmitted RF.
- No automatic RF calibration, two-tone transmit, MOX, PTT or hardware attenuator writes.
- No `file download apd_log` command. That diagnostic endpoint is reportedly restricted to authorised Alpha-licensed configurations; this preview does not attempt to bypass restrictions.
- No Linux/Raspberry Pi APD dashboard in this initial test build.

## Suggested controlled test

Connect first **without transmitting**; review APD state and sampler options and save a diagnostic snapshot. Record which model/firmware/version was tested. If using a PGXL, validate its port connections, coupling loss, RF input limits and matching Station target first. Only then compare the status while using the radio **in the normal, authorised manner**, preferably with a rated dummy load and external RF instrumentation.

Capture one snapshot with APD disabled, another with it enabled, and one after the radio reports its correction active. If you do an IMD test, make measurements at the same external RF power, with identical analyser settings, and take precautions against measurement-receiver overload. The sample logs do not themselves measure IMD.

Submit your exported text logs with the radio model, firmware and test condition. Redact any identifying IP addresses or station details before posting logs publicly.

## Build provenance
Portable Win-x64 self-contained executable created from GitHub PR #10 by **APD development preview — Windows build**. Its GitHub artifact has a finite retention period and is not a GitHub Release.

Existing v0.6.4 release assets and `main` remain unchanged. GPL-3.0-only.
