# Flex Companion v0.4.10

- Reworked the DAX-IQ FFT path for dual-radio performance. FFT transforms no longer execute on the WPF UI thread.
- Added one background FFT worker per connected radio, allowing the two radio spectra to use separate CPU worker threads/cores.
- The FFT worker is latest-frame-only: incoming IQ is written to a ring buffer and stale display frames are dropped instead of queued, preventing latency from accumulating.
- Replaced the per-frame double-precision FFT with a single-precision planned FFT that caches the Hann window, bit-reversal map and twiddle factors. Expensive trigonometry is no longer recalculated every frame.
- DAX-IQ ring-buffer ingestion now uses block copies instead of per-sample modulo/validation work.
- Added an independent FFT presentation timer (about 30 FPS normally / 20 FPS in Network saver) so spectrum smoothness no longer follows the slower meter refresh timer.
- Removed the per-frame spectrum-floor allocation + full Array.Sort; SpectrumView now estimates the 20th-percentile floor with a fixed stack histogram.
- WPF rendering is explicitly left in its Direct3D hardware-capable default mode. GPU rendering therefore remains available, while the small 1K-8K FFT transforms stay on background CPU workers where GPU transfer overhead would normally cost more than it saves.
- Network saver, two-radio operation, DAX-IQ ownership rules and all existing radio functionality are retained.
