# Flex Companion v0.4.7

- Added contextual DSP error mapping so mode/state refusals are no longer mistaken for permanent unsupported features.
- `0x5000002D` remains a permanent per-connection unsupported-setting result and greys the affected control.
- `0x50004001` is now reported as a radio/context refusal without disabling the feature permanently.
- `0xE2000000` is now treated as a generic radio/state error and never used by itself to permanently disable a feature.
- Added explicit mappings for the known FLEX `SL_INVALID_DSP_ALG_FOR_MODE` (`0x50000061`) and `SL_INVALID_CMD_FOR_MODE` (`0x50000085`) errors.
- ANF / ANFL / ANFT are temporarily gated in DIGU, DIGL, RTTY and FDV-family modes, then automatically restored when a compatible mode is selected.
- In CW-family modes, ANF / RNN / ANFL / ANFT are temporarily gated, following AetherSDR mode presentation.
- Friendly status messages now retain the raw FLEX error code, e.g. `ANF unavailable in DIGU mode [0xE2000000]`.
- Where `sub license all` explicitly reports a relevant feature disabled, the status line includes that radio-reported reason without treating unknown entitlement as disabled.
- Improved disabled-control tooltips and the bottom status area so temporary mode restrictions are distinguishable from unsupported hardware/firmware.
