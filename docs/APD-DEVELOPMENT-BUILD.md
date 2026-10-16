# Flex Control Companion — APD LAB development preview
**Version:** 0.6.5-apd.2 | **Platform:** Windows x64 | **Status:** TEST ONLY

This is a side-by-side portable preview for evaluating the **SmartSignal APD diagnostic dashboard**. It is **not** a production release, and has not been tested against a real FLEX-8000 or Aurora.

## Installation

1. Extract the GitHub Actions artifact ZIP and then extract the inner `FlexControlCompanion-APD-LAB-v0.6.5-apd.2-win-x64.zip` to a separate folder.
2. Run `FlexCompanion.exe`. The preview is self-contained; .NET need not be installed.
3. Do **not** replace an existing installed Companion executable. The preview uses a separate settings location: `%APPDATA%\FlexCompanion-APD-LAB\settings.json`.
4. With an APD-capable radio, connect as usual and open the **APD** tab for that slot. Alternatively, **no real radio is required**: use the bundled synthetic FLEX-8600 test server described below. A real FLEX-6000 does not support APD.
5. When testing the feedback sampler, use only correctly isolated, rated hardware. The debug tool **does not protect RF inputs** from overload.

## Testing with no FLEX-8000 or Aurora — included synthetic FLEX-8600

You **do not need any APD-capable hardware** to check the app's TCP/API, dashboard bindings, sampler controls, calibration-state handling and meter capture.

1. Extract the full portable APD LAB ZIP. It includes `APD-Simulator.exe` beside `FlexCompanion.exe`.
2. Run `FlexCompanion.exe`, choose an **empty / disconnected** radio slot and open its **APD** tab.
3. Click **Start simulated FLEX-8600 (NO RF)**. This starts a separate labelled simulator console bound to **127.0.0.1:4992**, connects that slot and opens the APD diagnostics window.
4. Click **Enable APD** in the dashboard. Synthetic status progresses from CALIBRATING to ACTIVE within about three seconds. Toggle sampler ports and save a status + meter snapshot.
5. In the simulator console, run `drive 75`, `freq 14.150000`, `ant ANT2` to change synthetic calibration identity and verify status tracking. Try `tx on` to generate **synthetic** forward power meters (still no RF, PTT or SDR connection); `tx off` returns to receive. `reset` clears calibration; `quit` closes the virtual server.
6. The snapshot will identify the simulated radio by its `APD_SIMULATOR_NO_RF` nickname.

**Safety guarantees:** The simulator listens on localhost only, cannot connect to a real transceiver, provides no RF and explicitly rejects actual PTT/transmit commands and `file download apd_log`. Its example ALC, MIC, SWR, frequency-error and calibration readings are **SYNTHETIC**, not measurements or evidence of SmartSignal hardware behavior. It does not emulate FPGA/DSP predistortion, receiver overload, ADC feedback or RF-path attenuation. Use real hardware later to validate those physical features.

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
