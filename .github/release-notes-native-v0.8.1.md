# Flex Control Companion v0.8.1 — Native Raspberry Pi / Linux

Native Python + PySide6 + C++ edition, independently of the Windows/.NET application.

## Improvements
- Reuse C++ FFT scratch buffers for reduced per-frame allocation pressure.
- Reduce NumPy fallback FFT ring-buffer copy and indexing overhead.
- Reject FFT sizes larger than the allocated IQ ring-buffer capacity (native and fallback backends).
- Reduce TCP status-line processing allocations and bound accepted line sizes.
- Expire timed-out blocking radio commands from the pending map, with race-safe completion.
- Restore clearly labelled software **PTT / MOX** control.
- Add a SmartSDR 4.3 PTT Override guide. Hardware PTT Override is still configured within SmartSDR; Companion does **not** write undocumented interlock/PTT properties.

## Install / update
On Raspberry Pi OS (Bookworm or Trixie, 32-bit or 64-bit where PySide6 is available):

```bash
curl -fsSL https://raw.githubusercontent.com/Craig-M-M9XDT/FlexCompanion/v0.8.1/pi-native/deploy/install.sh | sudo bash -s -- --ref v0.8.1 --touch
```

For a dedicated touchscreen, append `--kiosk --autostart`. Run `flexcompanion-native --version` to verify 0.8.1.

**Assets:** portable source tarball (the installer builds on your Pi), tested ARM64/Python 3.11 wheel for advanced installations, and SHA256 checksums. Wheel by itself does not install the desktop entry or full Qt application dependencies: the installer remains the recommended route.

Release automation requires passing native-C++ and NumPy tests on x64 and ARM64. This does not constitute real-radio on-air PTT or RF validation.
