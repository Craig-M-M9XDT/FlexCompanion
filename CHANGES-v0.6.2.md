# Flex Companion v0.6.2

- Centres the compact FFT on the selected slice's actual receive-filter passband instead of always centring on the carrier.
- Correctly offsets USB, LSB, DIGU, DIGL, RTTY and related modes, while keeping AM, SAM, FM and NFM symmetrical.
- Uses the radio-reported `filter_lo` and `filter_hi` edges, with safe mode defaults while fresh slice metadata is arriving.
- Automatically widens a small selected FFT span when necessary so the full receive filter and carrier marker remain visible.
- Applies the same passband-centred behaviour to both the Aether shared-pan path and the DAX-IQ fallback.
- Keeps Network saver capped at 24 kHz for Companion-owned DAX-IQ traffic.
