# Flex Companion v0.6.3

## Transmit controls

- Adds a dedicated **TX** tab to the Windows radio panel.
- Adds radio-authoritative **RF power** control with support for the radio's `tx_rf_power_changes_allowed` and `max_power_level` limits.
- Adds **microphone gain** control and follows the radio's live `mic_level` status.
- Adds **PROC** enable plus **Normal / DX / DX+** processor level selection.
- Subscribes to FLEX transmit status so changes made in SmartSDR/AetherSDR are reflected back into Companion.

## AetherSDR TX noise reduction

- Adds an **Aether TX RN2** toggle for AetherSDR's client-side RNNoise microphone denoiser.
- Uses AetherSDR's local opt-in AutomationServer rather than changing radio-side DSP state.
- Gives a clear status when Aether's automation bridge is disabled, authentication is required, or the AetherTX Gate control is not currently exposed.
- RN2 remains a voice-mic feature in AetherSDR; its digital/DAX/RADE paths bypass TX RN2.

## Packaging

- Version advanced to **0.6.3** for Windows and Raspberry Pi packages.
- The existing Raspberry Pi build remains otherwise unchanged in this release.

GPLv3. AetherSDR attribution retained.
