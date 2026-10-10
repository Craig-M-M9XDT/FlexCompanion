# Hardware PTT with a PC microphone — Flex Control Companion

## What the option does

Each FCC radio slot (A and B) has **Automatically preserve PC audio with rear-panel PTT**, enabled by default and stored in the normal per-slot settings. The Windows WPF, Raspberry Pi Avalonia and native Python/Qt builds all display:

- Radio-reported `transmit mic_selection`
- Radio-reported `interlock source` and `state`
- Whether an explicit PTT Override capability is advertised, and the result of attempting to set it.

FCC does **not** key the transmitter, change a TX safety interlock, switch microphone sources locally, intercept RCA contacts, or claim ownership of another MultiFLEX station.

## Firmware limitations

FlexRadio's 28 September 2026 guidance says that **before SmartSDR 4.3** a physical PTT switches to the last analog microphone, even when PC/DAX audio is selected. **From SmartSDR 4.3**, PTT Override is disabled by default, preserving the selected PC/DAX source. On models with SmartSDR 4.2.20, this feature is consequently **monitor-only** unless a compatible radio-side firmware/API extension is installed.

References:
- https://helpdesk.flexradio.com/hc/en-us/articles/56307893056155-How-to-Set-PTT-Override
- https://helpdesk.flexradio.com/hc/en-us/articles/46785537438619-How-to-use-a-PC-or-USB-Mic-with-SmartSDR

No writable PTT Override API field has been verified on a live v4.3 radio yet. The implementation **never sends a PTT Override command** unless the radio itself first reports an explicit `ptt_override=0|1` status field on the `transmit` or `interlock` status plane. This property name and the two candidate setter routes are provisional, not confirmed FlexRadio API documentation.

If the radio reports `ptt_override=1`, PC microphone is selected and interlock state is READY/RECEIVE, FCC tries the matching candidate command **once per observed state transition**:

| Advertised on | Candidate command (only after the field is reported) |
|---|---|
| `transmit` | `transmit set ptt_override=0` |
| `interlock` | `interlock ptt_override=0` |

A nonzero response disables further attempts for that session until the state changes. A success response is **not** proof that audio routing changed: FCC waits for a radio-reported `ptt_override=0` before displaying confirmation. If firmware uses a different API field/route, update the adapter once captured from the official client. No fallback is attempted with guessed commands on 4.2.20.

**Note:** The rear-panel *PTT* input is distinct from the rear *TX REQ* amplifier/antenna transmit-inhibit input. Do not change `rca_txreq_enable` to try to solve microphone routing.

## Suggested verification (dummy load / low power)

1. Start SmartSDR or AetherSDR on the target FLEX and select the **PC** microphone; turn DAX TX off for this voice test.
2. Connect FCC to the radio in slot A or B; open the **PTT & Mic** / **PTT / MIC** / **PTT / PC AUDIO** panel.
3. Confirm the microphone selection reads PC and the radio interlock is READY or RECEIVE.
4. Observe whether the radio actually advertises `ptt_override`. If not, FCC must show **unsupported/not advertised** and must not attempt a write.
5. Check MOX first, then the physical RCA footswitch with low power into a dummy load, and verify modulation as well as RF PTT.
6. Confirm FCC never changes TX owner, SWR/temperature protections or radio PTT itself.

## Developer tests

- Native Python policy: `pytest pi-native/tests/test_ptt_override.py`
- Native Pi build CI exercises both C++ DSP and numpy fallback.
- .NET WPF / Avalonia PR validation compiles both projects.
- RF/PTT behaviour remains **hardware-unverified** until logged against the relevant exact firmware build.
