# AetherSDR → Flex Companion shared-pan bridge

Flex Companion 0.5.0 can reuse the **same radio-generated FFT bins that AetherSDR already receives** for its normal panadapter / mini-pan. This removes Companion's extra DAX-IQ stream and local FFT whenever Aether is present.

The bridge is deliberately local-only: Aether mirrors `RadioModel::panFeedSpectrumReady` frames to `127.0.0.1:7331` using the small FCSP v1 binary format. It does **not** create a panadapter, DAX IQ stream, slice, or additional radio subscription.

## Apply to an AetherSDR source checkout

Windows example:

```powershell
py .\AetherBridgePatch\apply_aether_bridge.py C:\src\AetherSDR
```

Linux example:

```bash
python3 AetherBridgePatch/apply_aether_bridge.py ~/src/AetherSDR
```

Then rebuild AetherSDR normally. No extra Qt module is required; the patch uses Qt Core/Network classes Aether already links.

The patch was written against the current AetherSDR `MainWindow_Session.cpp` layout used during Flex Companion 0.5.0 development. If Aether changes that wiring point later, the script stops with an anchor error rather than modifying an unknown location.

When patched Aether and Flex Companion are both running on the same PC, Companion's spectrum status changes to:

`Aether shared pan · radio FFT · no extra DAX IQ stream`

If no bridge frames are seen for the selected radio/pan, Companion waits briefly and automatically falls back to its DAX-IQ FFT path.

## FCSP v1 packet

Little-endian header: `"FCSP"`, version byte, flags byte, serial length (u16), pan stream id (u32), bin count (u16), reserved (u16), Aether source timestamp ns (i64), UTF-8 serial, then `binCount` float32 dBm FFT bins.

Both AetherSDR and Flex Companion remain GPLv3 software; preserve their existing licence and attribution notices when distributing modified builds.
