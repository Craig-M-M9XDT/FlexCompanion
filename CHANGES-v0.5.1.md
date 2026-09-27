# Flex Companion v0.5.1

- Changed Aether shared-pan presentation from a 30/20 Hz polling clock to event-driven, source-paced updates. This removes the beat/judder caused when Aether/radio FFT frames (commonly ~25 fps) were sampled by Companion at a different cadence.
- Added latest-frame coalescing so localhost pan bursts never build a UI backlog.
- Changed FLEX meter presentation to be driven by complete VITA meter packets instead of a 30 Hz polling timer. Packet bursts are coalesced and a 250 ms watchdog remains as fallback.
- Network Saver now limits only Companion-owned FFT fallback traffic; it no longer changes meter cadence or Aether shared-pan cadence.
- Aether shared-pan status now reports `source-paced radio FFT` when active.
- Preserves the v0.5.0 Aether bridge, DAX-IQ fallback, GPLv3 licence and AetherSDR acknowledgement.
