# Flex Control Companion — Licence Dashboard & PTT Select (development preview)

This is an **unmerged test build**; it does not change v0.6.4.

## Licence dashboard

The existing API subscription `sub license all` reports live feature-state and source-reason information from the radio.

A FLEX-6600M running SmartSDR 4.3.16 has been observed (screenshot supplied by operator) to report 25 features, of which SMARTLINK and MULTIFLEX are enabled, both reason BUILT_IN; highest_major_version=3.

Radio-reported groups:
- **Included with licence**: BUILT_IN (not an individual paid unlock).
- **Individually unlocked**: enabled LICENSE_FILE entry only.
- **SmartSDR+ features**: PLUS, whether enabled or disabled.
- **Early Access**: EA, whether enabled or disabled.
- **Other / licence-file features**: other reasons, including disabled LICENSE_FILE entries.

This grouping is for display, not a newly computed entitlement. Missing features are never assumed disabled. Exports are local CSV/JSON snapshots of reported facts; the export does not send licence writes.

### CSV and JSON export

Use Licences > Radio A/B > Export CSV / Export JSON.

CSV quotes fields and prefixes formula-like cell values for safe opening in common spreadsheet applications. JSON records feature names, radio status, dates, groups and any received subscription fields.

## PTT Select

From the main window choose **PTT select**, select Radio A or B, then choose:

- **Keep selected audio (PC/DAX)**: corresponds to SmartSDR 4.3 PTT Override OFF, the default.
- **Use last analogue microphone**: corresponds to SmartSDR 4.3 PTT Override ON.

The selector changes a **pending client-side choice**. Actual radio setting is shown in a separate line and can be changed in SmartSDR > Settings > Radio Setup > Phone/CW > PTT Override (or on an M-model Menu > Phone/CW).

An experimental API Apply control is disabled unless the radio explicitly reports `ptt_override=0|1` on the transmit or interlock status plane, RX interlock is READY or RECEIVE, and the desired and current states differ. If those conditions hold and the operator explicitly presses Apply, it tries the same-plane candidate setter once and reports the reply; it never claims confirmed state until a new status message is observed. The exact third-party PTT setter is **not yet verified** for v4.3.16. Therefore its enabled state is not sufficient proof of RF/audio behavior.

The feature never asserts PTT, changes selected microphone/DAX input, changes TXREQ, or overrides TX safety interlocks. Under MultiFLEX, FlexRadio states the override applies only when local PTT is asserted by the transmitting client.

Official reference: https://helpdesk.flexradio.com/hc/en-us/articles/56307893056155-How-to-Set-PTT-Override

## Test protocol

1. Confirm feature statuses match SmartSDR 4.3.16's native Unlocked Features display and export JSON locally.
2. Select PC Mic, DAX TX OFF, and verify the selected audio under MOX, without exceeding low test power.
3. Verify physical RCA PTT on a dummy load with PTT Override OFF preserves PC mic; with ON uses analogue mic.
4. Confirm the Companion never keys the transmitter itself.
5. Confirm status reporting on both radio slots and reconnect handling.

Do not merge the experimental PTT setter into the production release without a live 4.3.16 API trace and RF verification.
