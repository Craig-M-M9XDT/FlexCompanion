# Flex Companion v0.4.4

## Best AGC-T DSP isolation

- Best AGC-T no longer refuses to run merely because noise reduction is enabled.
- Before a sweep, Companion snapshots the selected slice's reported NR/NRF/NRL/NRS/RNN, NB, ANF/ANFL/ANFT states.
- Any enabled noise-mitigation functions are temporarily switched off. Unsupported/unreported controls are not probed.
- Companion waits 400 ms after the DSP changes before beginning RMS sampling so the first sweep point is not contaminated by the previous filter state.
- As soon as the sweep finishes, the exact pre-scan DSP states are restored automatically.
- The same restoration runs on Cancel/Restore, slice changes, and orderly disconnect.
- The scan stays locked to the slice it started on.
- UI messaging now explains the automatic temporary DSP suppression instead of instructing the operator to turn NR off manually.
