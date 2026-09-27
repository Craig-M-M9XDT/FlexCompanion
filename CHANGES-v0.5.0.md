# Flex Companion v0.5.0

- Added **AUTO spectrum source**: prefer AetherSDR shared pan FFT frames, fall back to Companion DAX IQ.
- Added a localhost-only Aether bridge receiver on UDP 7331 using the FCSP v1 binary frame format.
- Added `AetherBridgePatch/apply_aether_bridge.py`, which taps Aether's existing `RadioModel::panFeedSpectrumReady` frames without creating another radio pan, slice, DAX channel or radio stream.
- Aether shared-pan frames are matched by radio serial + pan stream id and re-sliced locally around the selected slice.
- When Aether shared-pan mode is active, Companion automatically tears down any fallback DAX-IQ stream it created.
- If Aether is absent, Companion waits briefly then automatically restores the existing DAX-IQ FFT path.
- Changed compact spectrum to trace-only by default, removing the continuously updated WPF waterfall from the hot path.
- Cached the analogue meter's static face; display-refresh animation now redraws mainly the needle rather than recreating ticks, labels, gradients and bezel every frame.
- Network Saver no longer drops the S-meter presentation rate to 10 Hz; meter presentation stays at about 30 Hz because selective subscriptions already provide the bandwidth saving.
- Network Saver still caps Companion-owned DAX IQ to 24 kHz, but does not cap Aether shared-pan span because the local bridge adds no radio/LAN stream.
- Preserved GPLv3 licensing and AetherSDR acknowledgement.
