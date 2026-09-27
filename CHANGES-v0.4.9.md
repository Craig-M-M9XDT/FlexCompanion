# Flex Companion v0.4.9

- Added **Network saver** mode for dual-radio, Wi-Fi and VPN/remote use.
- Network saver caps Companion DAX IQ to 24 kHz per radio and reduces the UI/meter refresh rate from 20 Hz to 10 Hz.
- Replaced `sub meter all` with `meter list` + selective meter subscriptions. Companion now requests only the selected slice LEVEL meter, forward power, SWR, and the optional TX meter currently shown.
- Meter subscriptions are rebuilt after slice/mode/frequency changes because FLEX slice meter IDs are dynamic.
- If a radio/firmware does not support the selective path, Companion safely falls back to `sub meter all`.
- DAX IQ packet buffers now use `ArrayPool<float>` rather than allocating two new arrays per UDP packet, significantly reducing GC pressure when two FFTs are running.
- The selected FFT width is preserved while Network saver is active; widths above 24 kHz are temporarily displayed at 24 kHz and automatically return to the selected width when saver mode is disabled.
