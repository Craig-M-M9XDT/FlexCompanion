# Flex Control Companion v0.6.5

## Improvements
- Diagnostic, mode-independent RX/TX spectrum status when an Aether shared panadapter FFT frame is available.
- Clear indication that DAX IQ is RX-side only and does not provide a calibrated TX spectrum measurement.
- Corrected VITA-49 voltage/current decoding and additional PA-current meter naming variants on Windows and Raspberry Pi.
- Optional PayPal donation button in the Windows About dialog.

## Important limitations
- Radio-generated TX FFT availability still requires testing with a real FLEX radio and Aether bridge; this release does **not** guarantee TX FFT in every mode.
- The FLEX-6000 PA current meter may not expose valid current values; the app does not synthesise an estimated reading.
- The Raspberry Pi build has not been hardware validated for TX FFT.
- Support donations are optional; application remains GPLv3.
- No change to SmartSDR licensing or radio firmware.

## Downloads
- Windows x64: FlexControlCompanion-v0.6.5-windows-x64.zip
- Raspberry Pi ARM64: FlexControlCompanion-v0.6.5-pi-arm64.tar.gz

Builds are self-contained. Source and attribution are included in repository.

## Rebuilt v0.6.5 correction (18 October 2026)
- Fixed the Windows About dialog crash caused by a stray literal newline escape in XAML.
- Restored correct voltage/current meter scaling on Windows and Raspberry Pi (addresses VDD reading approximately one-quarter of actual voltage).
- PA current reporting depends on compatible radio firmware/meter support; no synthetic amperage is displayed.
- Download files were rebuilt and replaced under the existing v0.6.5 release tag. Re-download and replace your prior installation.
